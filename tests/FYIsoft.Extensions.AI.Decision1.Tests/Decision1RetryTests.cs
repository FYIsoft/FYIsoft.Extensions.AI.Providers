using System.Net;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1RetryTests
{
    private static Func<int, CancellationToken, Task<HttpResponseMessage>> Script(params Func<HttpResponseMessage>[] steps) =>
        (n, _) => Task.FromResult(steps[Math.Min(n, steps.Length) - 1]());

    private static HttpResponseMessage Ok() => FakeDecision1Handler.Response(ProbeFixtures.StringStateNoulApikey);

    private static HttpResponseMessage Status(ProbeFixture fixture, Dictionary<string, string>? headers = null) =>
        FakeDecision1Handler.Response(fixture, headers);

    [Fact]
    public async Task RateLimited_WithResetHeader_WaitsTheResetThenSucceeds()
    {
        // A 429 in the deployment's real header shape: x-ratelimit-reset-requests in seconds, no Retry-After.
        using var test = TestClient.Create(new FakeDecision1Handler(Script(
            () => Status(ProbeFixtures.Synthetic429, new() { ["x-ratelimit-reset-requests"] = "3", ["x-ratelimit-remaining-requests"] = "0" }),
            Ok)));

        var response = await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Requests.Should().HaveCount(2);
        test.Delays.Should().Equal(TimeSpan.FromSeconds(3));
        response.Metadata.Attempts.Should().Be(2);
        response.GetAnswer<Decision1NoulAnswer>("billing").Noul.Should().Be(0.9980732633607365);
    }

    [Fact]
    public async Task RetryAfterMs_TakesPrecedenceOverRetryAfterAndReset()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(
            () => Status(ProbeFixtures.Synthetic429, new() { ["retry-after-ms"] = "250", ["retry-after"] = "7", ["x-ratelimit-reset-requests"] = "9" }),
            Ok)));

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Delays.Should().Equal(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task RetryAfter_IsHonored_AndCapped()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(
            () => Status(ProbeFixtures.Synthetic429, new() { ["retry-after"] = "7" }),
            () => Status(ProbeFixtures.Synthetic429, new() { ["retry-after"] = "600" }),
            Ok)), o => o.Retry.MaxRetryAfter = TimeSpan.FromSeconds(20));

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Delays.Should().Equal(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ServiceUnavailable_RetriesWithExponentialBackoff()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(
            () => Status(ProbeFixtures.Synthetic503),
            () => Status(ProbeFixtures.Synthetic503),
            Ok)), o => o.Retry.InitialDelay = TimeSpan.FromMilliseconds(100));

        var response = await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Requests.Should().HaveCount(3);
        test.Delays.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
        response.Metadata.Attempts.Should().Be(3);
    }

    [Fact]
    public void Backoff_IsJitteredDownward_AndCappedAtMaxDelay()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey), o =>
        {
            o.Retry.InitialDelay = TimeSpan.FromSeconds(1);
            o.Retry.MaxDelay = TimeSpan.FromSeconds(3);
            o.Retry.JitterRatio = 0.5;
        });
        test.Core.NextJitter = () => 0.5;

        test.Core.ComputeBackoff(1).Should().Be(TimeSpan.FromMilliseconds(750));
        test.Core.ComputeBackoff(2).Should().Be(TimeSpan.FromMilliseconds(1500));
        test.Core.ComputeBackoff(5).Should().Be(TimeSpan.FromMilliseconds(2250));
    }

    [Fact]
    public async Task RetriesExhausted_ThrowsTheTypedException_WithAttemptCount()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(() => Status(ProbeFixtures.Synthetic503))));

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());

        var ex = (await act.Should().ThrowAsync<Decision1ServerException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ex.Metadata!.Attempts.Should().Be(3, "the default is two retries after the first attempt");
        test.Handler.Requests.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task OtherTransientStatuses_AreRetried(int status)
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(
            () => Status(new ProbeFixture(status, "", new Dictionary<string, string>())),
            Ok)));

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(nameof(ProbeFixtures.BadType400))]
    [InlineData(nameof(ProbeFixtures.NoAuth401))]
    [InlineData(nameof(ProbeFixtures.Synthetic403))]
    [InlineData(nameof(ProbeFixtures.UnknownDeployment))]
    [InlineData(nameof(ProbeFixtures.Synthetic422))]
    public async Task NonTransientClientErrors_AreNeverRetried(string fixtureName)
    {
        var fixture = (ProbeFixture)typeof(ProbeFixtures).GetField(fixtureName)!.GetValue(null)!;
        using var test = TestClient.Create(new FakeDecision1Handler(fixture));

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());

        await act.Should().ThrowAsync<Decision1ApiException>();
        test.Handler.Requests.Should().HaveCount(1);
        test.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task AttemptTimeout_IsRetried_ThenSucceeds()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(async (n, ct) =>
        {
            if (n == 1)
                await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        }), o => o.Timeout = TimeSpan.FromMilliseconds(50));

        var response = await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Requests.Should().HaveCount(2);
        response.Metadata.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task AttemptTimeout_OnEveryAttempt_ThrowsTimeoutException()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        }), o => { o.Timeout = TimeSpan.FromMilliseconds(30); o.Retry.MaxRetries = 1; });

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());

        var ex = (await act.Should().ThrowAsync<Decision1TimeoutException>()).Which;
        ex.Timeout.Should().Be(TimeSpan.FromMilliseconds(30));
        test.Handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConnectionFailure_IsRetried_ThenSurfacesAsConnectionException()
    {
        using var test = TestClient.Create(new FakeDecision1Handler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known."))));

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());

        var ex = (await act.Should().ThrowAsync<Decision1ConnectionException>()).Which;
        ex.Should().NotBeOfType<Decision1TimeoutException>();
        ex.InnerException.Should().BeOfType<HttpRequestException>();
        test.Handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ConnectionFailure_ThenSuccess_Recovers()
    {
        using var test = TestClient.Create(new FakeDecision1Handler((n, _) => n == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("reset"))
            : Task.FromResult(Ok())));

        (await test.Client.DecideAsync("x", Questions.SingleNoul())).Metadata.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task MaxRetriesZero_DisablesRetries()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(() => Status(ProbeFixtures.Synthetic503))), o => o.Retry.MaxRetries = 0);

        await FluentActions.Invoking(() => test.Client.DecideAsync("x", Questions.SingleNoul())).Should().ThrowAsync<Decision1ServerException>();
        test.Handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task EveryAttempt_SendsTheSameBody()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(Script(() => Status(ProbeFixtures.Synthetic503), Ok)));

        await test.Client.DecideAsync("x", Questions.Combined());

        test.Handler.Requests[0].Body.Should().Be(test.Handler.Requests[1].Body);
    }
}
