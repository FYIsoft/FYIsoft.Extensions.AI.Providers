using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Jev.Tests;

public class JevClientTests
{
    private const string TriageResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "is_urgent": { "type": "noul", "noul": 0.95 },
            "department": { "type": "choice", "choice": "billing", "probabilities": { "billing": 0.88, "technical": 0.12, "sales": 0.0 }, "confidence": 0.81 },
            "frustration": { "type": "score", "score": 1.05, "legend": { "0": "Calm", "1": "Frustrated", "2": "Very angry" }, "probabilities": { "0": 0.0, "1": 0.95, "2": 0.05 }, "confidence": 0.92 },
            "future": { "confidence": 1, "type": "ranking" }
          },
          "usage": { "input_tokens": 296, "output_tokens": 20 }
        }
        """;

    private static Dictionary<string, JevQuestion> TriageQuestions() => new()
    {
        ["is_urgent"] = JevQuestion.Noul("Does this convey urgency?", "Explicitly time-sensitive", "No urgency expressed"),
        ["department"] = JevQuestion.Choice("Which team should handle this?", new Dictionary<string, JsonNode?>
        {
            ["billing"] = "Payments, invoicing, refunds",
            ["technical"] = "Bugs, outages, integrations",
            ["sales"] = null,
        }),
        ["frustration"] = JevQuestion.Score("How frustrated is the customer?", "Calm", "Frustrated", "Very angry"),
    };

    [Fact]
    public async Task SystemOne_SendsWireFormatAndHeaders()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse, r => r.Headers.Add("x-typesafe-request-id", "req_123"));
        using var client = FakeHttpHandler.CreateClient(handler, o => o.DefaultHeaders["X-Custom"] = "1");

        await client.SystemOneAsync("Help! My payouts have been failing for 3 days.", TriageQuestions());

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://api.test.local/v1/systemone"));
        request.Headers["Authorization"].Should().Be("Bearer test-key");
        request.Headers["X-Custom"].Should().Be("1");
        request.Headers.Should().ContainKey("X-TypeSafe-SDK").And.ContainKey("X-TypeSafe-Runtime").And.NotContainKey("X-TypeSafe-Retry-Count");

        var body = JsonNode.Parse(request.Body!)!;
        body["state"]!.GetValue<string>().Should().StartWith("Help!");
        body["model"]!.GetValue<string>().Should().Be("jev-latest");

        var questions = body["questions"]!;
        questions["is_urgent"]!.ToJsonString().Should().Be(
            """{"type":"noul","instructions":"Does this convey urgency?","criteria":{"true":"Explicitly time-sensitive","false":"No urgency expressed"}}""");
        questions["department"]!["criteria"]!.ToJsonString().Should().Be(
            """{"billing":"Payments, invoicing, refunds","technical":"Bugs, outages, integrations","sales":null}""");
        questions["frustration"]!.ToJsonString().Should().Be(
            """{"type":"score","instructions":"How frustrated is the customer?","criteria":["Calm","Frustrated","Very angry"]}""");
    }

    [Fact]
    public async Task SystemOne_SendsNullInstructionsAndStructuredState()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse);
        using var client = FakeHttpHandler.CreateClient(handler);

        await client.SystemOneAsync(
            new { document = "I was charged twice." },
            new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul() },
            model: "jev-preview");

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        body["state"]!.ToJsonString().Should().Be("""{"document":"I was charged twice."}""");
        body["model"]!.GetValue<string>().Should().Be("jev-preview");
        body["questions"]!["q"]!.ToJsonString().Should().Be("""{"type":"noul","instructions":null}""");
    }

    [Fact]
    public async Task SystemOne_ParsesAllAnswerTypes()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse, r => r.Headers.Add("x-typesafe-request-id", "req_123"));
        using var client = FakeHttpHandler.CreateClient(handler);

        var response = await client.SystemOneAsync("state", TriageQuestions());

        response.Model.Should().Be("jev-1.13.0");
        response.RequestId.Should().Be("req_123");
        response.Usage.InputTokens.Should().Be(296);
        response.Usage.OutputTokens.Should().Be(20);

        response.GetAnswer<JevNoulAnswer>("is_urgent").IsYes.Should().BeTrue();

        var department = response.GetAnswer<JevChoiceAnswer>("department");
        department.Choice.Should().Be("billing");
        department.Probabilities["technical"].Should().Be(0.12);
        department.Confidence.Should().Be(0.81);

        var frustration = response.GetAnswer<JevScoreAnswer>("frustration");
        frustration.Score.Should().Be(1.05);
        frustration.MostLikelyLevel.Should().Be(1);
        frustration.Legend["2"]!.GetValue<string>().Should().Be("Very angry");

        response.Answers["future"].Should().BeOfType<JevUnknownAnswer>().Which.Type.Should().Be("ranking");
        FluentActions.Invoking(() => response.GetAnswer<JevChoiceAnswer>("is_urgent")).Should().Throw<InvalidCastException>();
    }

    [Fact]
    public async Task SystemOne_DoesNotMutateCallerRequest()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse);
        using var client = FakeHttpHandler.CreateClient(handler);
        var request = new SystemOneRequest { State = "s", Questions = TriageQuestions() };

        await client.SystemOneAsync(request);

        request.Model.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public async Task SystemOne_RejectsInvalidScoreLevels(int levels)
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler());
        var question = new JevScoreQuestion { Instructions = "x", Criteria = Enumerable.Range(0, levels).Select(i => (JsonNode?)i.ToString()).ToList() };

        var act = () => client.SystemOneAsync("s", new Dictionary<string, JevQuestion> { ["q"] = question });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*levels*");
    }

    [Fact]
    public async Task SystemOne_RejectsEmptyQuestionsAndTooManyChoices()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler());

        await FluentActions.Awaiting(() => client.SystemOneAsync("s", new Dictionary<string, JevQuestion>()))
            .Should().ThrowAsync<ArgumentException>().WithMessage("At least one question is required.*");

        var tooMany = JevQuestion.Choice("x", Enumerable.Range(0, 256).Select(i => $"o{i}"));
        await FluentActions.Awaiting(() => client.SystemOneAsync("s", new Dictionary<string, JevQuestion> { ["q"] = tooMany }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*maximum is 255*");
    }

    [Fact]
    public async Task ListModels_UnwrapsModels()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"models":[{"name":"jev-latest","description":"Latest","release_date":"2026-09-01"}]}""");
        using var client = FakeHttpHandler.CreateClient(handler);

        var models = await client.ListModelsAsync();

        handler.Requests.Single().Uri.Should().Be(new Uri("https://api.test.local/v1/models"));
        handler.Requests.Single().Body.Should().BeNull();
        models.Should().ContainSingle().Which.ReleaseDate.Should().Be("2026-09-01");
    }

    [Fact]
    public async Task ListModels_ThrowsWhenModelsMissing()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}"));

        await FluentActions.Awaiting(() => client.ListModelsAsync()).Should().ThrowAsync<JevException>().WithMessage("*not an array*");
    }

    [Fact]
    public void Constructor_RequiresApiKey()
    {
        var previous = Environment.GetEnvironmentVariable(JevClientOptions.ApiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(JevClientOptions.ApiKeyEnvironmentVariable, "  ");
            FluentActions.Invoking(() => new JevClient(new JevClientOptions())).Should().Throw<JevException>().WithMessage("*TYPESAFE_API_KEY*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(JevClientOptions.ApiKeyEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Constructor_ResolvesDefaults()
    {
        using var client = new JevClient(new JevClientOptions { ApiKey = "k" });

        client.BaseUrl.Should().Be(new Uri("https://api.typesafe.ai"));
        client.DefaultModel.Should().Be("jev-latest");
    }
}
