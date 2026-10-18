using System.Text;
using System.Text.Json.Nodes;
using Azure.Identity;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Extensions.AI.Decision1.Tests;

/// <summary>Skips unless DECISION1_LIVE_TESTS=1 and DECISION1_ENDPOINT and DECISION1_DEPLOYMENT are set.</summary>
public sealed class Decision1LiveFactAttribute : FactAttribute
{
    public Decision1LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DECISION1_LIVE_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Decision1ClientOptions.EndpointEnvironmentVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Decision1ClientOptions.DeploymentEnvironmentVariable)))
            Skip = "Set DECISION1_LIVE_TESTS=1, DECISION1_ENDPOINT and DECISION1_DEPLOYMENT to run live tests. " +
                   "DefaultAzureCredential is used unless DECISION1_API_KEY is set.";
    }
}

/// <summary>
/// Live checks against a real deployment. Three requests in total, to stay well inside a
/// 30 requests-per-minute deployment limit. Synthetic state only.
/// </summary>
[Trait("Category", "Live")]
public class Decision1LiveTests(ITestOutputHelper output)
{
    private Decision1Client Client(string? deployment = null)
    {
        var useKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Decision1ClientOptions.ApiKeyEnvironmentVariable));
        var options = Decision1ClientOptions.FromEnvironment();
        if (!useKey)
            options.Credential = new DefaultAzureCredential();
        if (deployment is not null)
            options.Deployment = deployment;
        options.Retry.MaxRetries = 1;
        var client = new Decision1Client(options);
        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; auth={(useKey ? "api-key" : "DefaultAzureCredential")}; host={client.RequestUri.Host}; deployment={client.Deployment}");
        return client;
    }

    [Decision1LiveFact]
    public async Task CombinedRequest_AnswersNoulChoiceAndScore()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var state = new JsonObject
        {
            ["message"] = "Checkout is down again. This is my third request.",
            ["customer_contact_count"] = 3,
        };

        var response = await client.DecideAsync(state, Questions.Combined(), cts.Token);

        response.Model.Should().Be("microsoft-decision-1");
        response.Answers.Keys.Should().BeEquivalentTo("team", "severity", "repeat_contact", "refund");
        var team = response.GetAnswer<Decision1ChoiceAnswer>("team");
        team.Choice.Should().Be("engineering");
        team.Probabilities.Keys.Should().BeEquivalentTo("billing", "engineering", "support");
        var severity = response.GetAnswer<Decision1ScoreAnswer>("severity");
        severity.Legend.Should().Equal("Cosmetic", "Minor", "Major", "Critical");
        severity.Score.Should().BeInRange(0, 3);
        severity.Probabilities.Sum().Should().BeApproximately(1.0, 1e-6);
        var repeat = response.GetAnswer<Decision1NoulAnswer>("repeat_contact");
        repeat.Noul.Should().BeInRange(0, 1);
        repeat.IsTrue().Should().BeTrue();
        response.GetAnswer<Decision1NoulAnswer>("refund").Noul.Should().BeInRange(0, 1);
        response.Usage.InputTokens.Should().BePositive();
        response.Metadata.RequestId.Should().NotBeNullOrWhiteSpace();

        output.WriteLine($"team={team.Choice} ({team.Confidence:0.000}); severity={severity.Score:0.000} nearest={severity.NearestLevel.Label}; " +
                         $"repeat_contact={repeat.Noul:0.000}; refund={response.GetAnswer<Decision1NoulAnswer>("refund").Noul:0.000}; " +
                         $"usage={response.Usage.InputTokens}/{response.Usage.OutputTokens}; region={response.Metadata.Region}; " +
                         $"remaining={response.Metadata.RateLimitRemainingRequests}/{response.Metadata.RateLimitLimitRequests}; attempts={response.Metadata.Attempts}");
    }

    [Decision1LiveFact]
    public async Task InvalidQuestionType_MapsTheServer400()
    {
        using var client = Client();
        // Bypasses client validation on purpose so the server, not the SDK, rejects the request.
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["model"] = client.Deployment,
            ["state"] = "x",
            ["questions"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "yesno", ["instructions"] = "Is it?" } },
        }.ToJsonString());

        var act = () => client.SendRawAsync(body, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<Decision1BadRequestException>()).Which;
        ex.ErrorCode.Should().Be("unsupported_request_argument");
        ex.RequestId.Should().NotBeNullOrWhiteSpace();
        output.WriteLine($"400: {ex.Message}");
    }

    [Decision1LiveFact]
    public async Task UnknownDeployment_MapsTheServer404()
    {
        using var client = Client(deployment: "no-such-deployment-sdk-live");

        var act = () => client.DecideAsync("x", Questions.SingleNoul());

        var ex = (await act.Should().ThrowAsync<Decision1NotFoundException>()).Which;
        ex.ErrorCode.Should().Be("DeploymentNotFound");
        ex.Metadata!.Attempts.Should().Be(1, "404 is never retried");
        output.WriteLine($"404: {ex.Message}");
    }
}
