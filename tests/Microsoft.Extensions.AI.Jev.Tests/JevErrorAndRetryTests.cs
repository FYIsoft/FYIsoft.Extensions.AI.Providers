using System.Net;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Jev.Tests;

public class JevErrorAndRetryTests
{
    private const string Ok = """{"model":"jev-1.13.0","answers":{"q":{"type":"noul","noul":0.2}},"usage":{"input_tokens":1,"output_tokens":1}}""";

    private static readonly Dictionary<string, JevQuestion> Questions = new() { ["q"] = JevQuestion.Noul("x") };

    [Theory]
    [InlineData(400, typeof(JevBadRequestException))]
    [InlineData(401, typeof(JevAuthenticationException))]
    [InlineData(403, typeof(JevPermissionDeniedException))]
    [InlineData(404, typeof(JevNotFoundException))]
    [InlineData(422, typeof(JevUnprocessableEntityException))]
    [InlineData(409, typeof(JevApiException))]
    public async Task MapsStatusToExceptionType(int status, Type expected)
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler().Respond((HttpStatusCode)status, """{"error":"nope"}"""));

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevApiException>();

        ex.Which.Should().BeOfType(expected);
        ex.Which.Message.Should().Be($"{status} nope");
        ex.Which.StatusCode.Should().Be((HttpStatusCode)status);
    }

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("""{"error":{"message":"nested"}}""", "nested")]
    [InlineData("""{"message":"msg"}""", "msg")]
    [InlineData("""{"detail":"det"}""", "det")]
    [InlineData("""{"detail":{"message":"det-msg"}}""", "det-msg")]
    [InlineData("""{"detail":[{"loc":["body","questions","q","criteria"],"msg":"field required"},{"loc":["body","state"],"msg":"bad"}]}""",
        "questions.q.criteria: field required; state: bad")]
    public async Task ExtractsErrorDetail(string body, string expected)
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler().Respond(HttpStatusCode.UnprocessableEntity, body));

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevUnprocessableEntityException>();

        ex.Which.Message.Should().Be($"422 {expected}");
    }

    [Fact]
    public async Task EmptyErrorBody_ReportsNoBody()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler().Respond(HttpStatusCode.BadRequest));

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevBadRequestException>();

        ex.Which.Message.Should().Be("400 status code (no body)");
    }

    [Fact]
    public async Task RetriesRetryableStatusThenSucceeds()
    {
        var handler = new FakeHttpHandler()
            .Respond((HttpStatusCode)529, """{"error":"overloaded"}""")
            .Respond(HttpStatusCode.TooManyRequests, null, r => r.Headers.Add("retry-after-ms", "1"))
            .Respond(HttpStatusCode.OK, Ok);
        using var client = FakeHttpHandler.CreateClient(handler);

        var response = await client.SystemOneAsync("s", Questions);

        response.GetAnswer<JevNoulAnswer>("q").IsYes.Should().BeFalse();
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Headers.Should().NotContainKey("X-TypeSafe-Retry-Count");
        handler.Requests[1].Headers["X-TypeSafe-Retry-Count"].Should().Be("1");
        handler.Requests[2].Headers["X-TypeSafe-Retry-Count"].Should().Be("2");
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""", r => r.Headers.Add("retry-after-ms", "1"))
            .Respond(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""", r => r.Headers.Add("retry-after-ms", "1"))
            .Respond(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""", r => r.Headers.Add("retry-after-ms", "1500"));
        using var client = FakeHttpHandler.CreateClient(handler);

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevRateLimitException>();

        ex.Which.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(1500));
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task DoesNotRetryClientErrors()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, "{}").Respond(HttpStatusCode.OK, Ok);
        using var client = FakeHttpHandler.CreateClient(handler);

        await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevBadRequestException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RetriesConnectionErrors()
    {
        var handler = new FakeHttpHandler().Throw(new HttpRequestException("refused")).Respond(HttpStatusCode.OK, Ok);
        using var client = FakeHttpHandler.CreateClient(handler);

        await client.SystemOneAsync("s", Questions);

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConnectionErrorWithoutRetries_ThrowsConnectionException()
    {
        var handler = new FakeHttpHandler().Throw(new HttpRequestException("refused"));
        using var client = FakeHttpHandler.CreateClient(handler, o => o.Retry = JevRetryPolicy.None);

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevConnectionException>();

        ex.Which.Message.Should().Be("Connection error.");
        ex.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Timeout_RetriesThenThrowsTimeoutException()
    {
        var handler = new FakeHttpHandler().Hang().Hang();
        using var client = FakeHttpHandler.CreateClient(handler, o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(50);
            o.Retry = o.Retry with { MaxRetries = 1 };
        });

        var ex = await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions)).Should().ThrowAsync<JevTimeoutException>();

        ex.Which.Message.Should().Be("Request timed out after 50ms.");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task CallerCancellation_IsNotWrappedOrRetried()
    {
        var handler = new FakeHttpHandler().Hang();
        using var client = FakeHttpHandler.CreateClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await FluentActions.Awaiting(() => client.SystemOneAsync("s", Questions, cancellationToken: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void Backoff_IsExponentialCappedAndJittered()
    {
        var policy = new JevRetryPolicy();

        for (var i = 0; i < 20; i++)
        {
            policy.ComputeBackoff(0).TotalMilliseconds.Should().BeInRange(375, 500);
            policy.ComputeBackoff(1).TotalMilliseconds.Should().BeInRange(750, 1000);
            policy.ComputeBackoff(10).TotalMilliseconds.Should().BeInRange(3750, 5000);
        }
    }
}
