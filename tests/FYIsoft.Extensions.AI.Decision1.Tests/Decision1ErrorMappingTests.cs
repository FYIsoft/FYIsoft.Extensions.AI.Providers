using System.Net;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1ErrorMappingTests
{
    private static async Task<Decision1ApiException> FailAsync(ProbeFixture fixture, Action<Decision1ClientOptions>? configure = null)
    {
        using var test = TestClient.Create(new FakeDecision1Handler(fixture), o => { o.Retry.MaxRetries = 0; configure?.Invoke(o); });
        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());
        return (await act.Should().ThrowAsync<Decision1ApiException>()).Which;
    }

    public static TheoryData<string, Type, int, string> RealProbeErrors => new()
    {
        { nameof(ProbeFixtures.Score11Levels), typeof(Decision1BadRequestException), 400, "unsupported_request_argument" },
        { nameof(ProbeFixtures.ChoiceArrayCriteria), typeof(Decision1BadRequestException), 400, "unsupported_request_argument" },
        { nameof(ProbeFixtures.BadType400), typeof(Decision1BadRequestException), 400, "unsupported_request_argument" },
        { nameof(ProbeFixtures.EmptyQuestions), typeof(Decision1BadRequestException), 400, "unsupported_request_argument" },
        { nameof(ProbeFixtures.UnknownDeployment), typeof(Decision1NotFoundException), 404, "DeploymentNotFound" },
        { nameof(ProbeFixtures.NoAuth401), typeof(Decision1AuthenticationException), 401, "401" },
        { nameof(ProbeFixtures.Synthetic403), typeof(Decision1PermissionDeniedException), 403, "PermissionDenied" },
        { nameof(ProbeFixtures.Synthetic422), typeof(Decision1UnprocessableEntityException), 422, "unprocessable_entity" },
        { nameof(ProbeFixtures.Synthetic429), typeof(Decision1RateLimitException), 429, "429" },
        { nameof(ProbeFixtures.Synthetic500), typeof(Decision1ServerException), 500, "InternalServerError" },
        { nameof(ProbeFixtures.Synthetic503), typeof(Decision1ServerException), 503, "ServiceUnavailable" },
    };

    [Theory]
    [MemberData(nameof(RealProbeErrors))]
    public async Task EveryStatus_MapsToItsTypedException(string fixtureName, Type expectedType, int status, string code)
    {
        var fixture = (ProbeFixture)typeof(ProbeFixtures).GetField(fixtureName)!.GetValue(null)!;

        var ex = await FailAsync(fixture);

        ex.Should().BeOfType(expectedType);
        ((int)ex.StatusCode).Should().Be(status);
        ex.ErrorCode.Should().Be(code);
        ex.RequestId.Should().Be(fixture.Headers["apim-request-id"]);
        ex.Metadata!.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task BadRequest_CarriesMessageDetailsAndRequestId()
    {
        var ex = await FailAsync(ProbeFixtures.Score11Levels);

        ex.ErrorMessage.Should().Be("Model does not support request argument supplied: score question s requires 2-10 levels");
        ex.Details.Should().Be("Model does not support request argument supplied: score question s requires 2-10 levels");
        ex.Message.Should().Be("Decision1 request failed with HTTP 400 (unsupported_request_argument): Model does not support request argument supplied: score question s requires 2-10 levels Request id: b68688b4-9eb0-4cb8-9317-d904380fc45d.");
        ex.Metadata!.RateLimitRemainingRequests.Should().Be(25);
    }

    [Fact]
    public async Task NotFound_ExplainsTheMissingDeployment()
    {
        var ex = await FailAsync(ProbeFixtures.UnknownDeployment);

        ex.Should().BeOfType<Decision1NotFoundException>();
        ex.ErrorMessage.Should().StartWith("The API deployment no-such-deployment does not exist.");
    }

    [Fact]
    public async Task Unauthorized_WithoutErrorHeaderOrDetails_UsesTheBodyCode()
    {
        var ex = await FailAsync(ProbeFixtures.NoAuth401);

        ex.ErrorCode.Should().Be("401");
        ex.Details.Should().BeNull();
        ex.ErrorMessage.Should().StartWith("Access denied due to invalid subscription key");
    }

    [Fact]
    public async Task Unauthorized_WithPermissionDeniedCode_StaysAnAuthenticationException()
    {
        // Seen in the SDK live run on 2026-10-18: a token for a principal without data-plane access got
        // HTTP 401 with code PermissionDenied (not the documented 403). Code and message are as observed;
        // the surrounding body shape follows the documented error format.
        var fixture = new ProbeFixture(401,
            """{"error":{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}}""",
            new Dictionary<string, string> { ["apim-request-id"] = "7b080e34-334b-464b-a572-467f5e06afbb" });

        var ex = await FailAsync(fixture);

        ex.Should().BeOfType<Decision1AuthenticationException>();
        ex.ErrorCode.Should().Be("PermissionDenied");
        ex.Message.Should().Be("Decision1 request failed with HTTP 401 (PermissionDenied): Principal does not have access to API/Operation. Request id: 7b080e34-334b-464b-a572-467f5e06afbb.");
    }

    [Fact]
    public async Task ErrorCode_FallsBackToTheHeader_WhenTheBodyHasNone()
    {
        var fixture = new ProbeFixture(400, """{"error":{"message":"bad"}}""",
            new Dictionary<string, string> { ["x-ms-error-code"] = "unsupported_request_argument", ["apim-request-id"] = "r1" });

        var ex = await FailAsync(fixture);

        ex.ErrorCode.Should().Be("unsupported_request_argument");
        ex.ErrorMessage.Should().Be("bad");
    }

    [Fact]
    public async Task NonJsonErrorBody_UsesStatusAndReason()
    {
        var fixture = new ProbeFixture(502, "<html>Bad gateway</html>", new Dictionary<string, string> { ["apim-request-id"] = "r2" });

        var ex = await FailAsync(fixture);

        ex.Should().BeOfType<Decision1ServerException>();
        ex.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        ex.ErrorCode.Should().BeNull();
        ex.ErrorMessage.Should().Be("HTTP 502 Bad Gateway");
        ex.Message.Should().NotContain("<html>");
    }

    [Fact]
    public async Task ObjectDetails_AreKeptAsRawJson()
    {
        var ex = await FailAsync(ProbeFixtures.Synthetic422);

        ex.Details.Should().Be("""{"question":"q"}""");
    }

    [Fact]
    public async Task OtherClientErrors_UseTheBaseApiException()
    {
        var ex = await FailAsync(new ProbeFixture(409, """{"error":{"code":"conflict","message":"Conflict"}}""", new Dictionary<string, string>()));

        ex.GetType().Should().Be(typeof(Decision1ApiException));
        ex.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RateLimit_CarriesTheUncappedRetryAfter()
    {
        var fixture = ProbeFixtures.Synthetic429 with
        {
            Headers = new Dictionary<string, string>(ProbeFixtures.Synthetic429.Headers) { ["retry-after"] = "120" },
        };

        var ex = await FailAsync(fixture);

        ex.Should().BeOfType<Decision1RateLimitException>().Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ExceptionHierarchy_SharesTheBaseType()
    {
        var ex = await FailAsync(ProbeFixtures.BadType400);

        ex.Should().BeAssignableTo<Decision1Exception>();
        typeof(Decision1TimeoutException).Should().BeDerivedFrom<Decision1ConnectionException>();
        typeof(Decision1ConnectionException).Should().BeDerivedFrom<Decision1Exception>();
    }

    [Fact]
    public async Task ErrorMessages_NeverContainTheApiKey()
    {
        var ex = await FailAsync(ProbeFixtures.NoAuth401);

        ex.ToString().Should().NotContain(TestClient.ApiKey);
    }
}
