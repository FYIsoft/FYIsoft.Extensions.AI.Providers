using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1RequestSerializationTests
{
    private static readonly JsonElement CombinedState = JsonDocument.Parse(
        """{"message":"Checkout is down again. This is my third request.","customer_contact_count":3}""").RootElement;

    [Fact]
    public async Task CombinedRequest_MatchesTheProbedWireShapeExactly()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));

        await test.Client.DecideAsync(CombinedState, Questions.Combined());

        test.Handler.Last.Body.Should().Be(
            """{"model":"decision-1-test","state":{"message":"Checkout is down again. This is my third request.","customer_contact_count":3},"questions":{"team":{"type":"choice","instructions":"Which team should handle this request?","criteria":{"billing":"Charges, invoices, and refunds","engineering":"Bugs, errors, and outages","support":"How-to and account questions"}},"severity":{"type":"score","instructions":"How severe is this issue?","criteria":["Cosmetic","Minor","Major","Critical"]},"repeat_contact":{"type":"noul","instructions":"Has the customer contacted support before?"},"refund":{"type":"noul","instructions":"Does the message ask for a refund?","criteria":{"true":"Asks for money back","false":"Does not ask for money back"}}}}""");
    }

    [Fact]
    public async Task Request_PostsJsonToTheSystemOnePath_WithoutApiVersion()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        await test.Client.DecideAsync("I was charged twice for October.", Questions.SingleNoul());

        var request = test.Handler.Last;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://synthetic-resource.services.ai.azure.com/providers/microsoft/v1/systemone"));
        request.Uri.Query.Should().BeEmpty("the service ignores api-version, so the client sends none");
        request.ContentType.Should().Be("application/json");
        request.Headers["Accept"].Should().Be("application/json");
        request.Headers["User-Agent"].Should().StartWith("FYIsoft.Extensions.AI.Decision1/");
    }

    [Theory]
    [InlineData("https://res.services.ai.azure.com")]
    [InlineData("https://res.services.ai.azure.com/")]
    [InlineData("https://res.cognitiveservices.azure.com")]
    public void Endpoint_AcceptsBothFoundryHostForms_AndTrailingSlash(string endpoint)
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey), o => o.Endpoint = new Uri(endpoint));

        test.Client.RequestUri.AbsoluteUri.Should().Be(endpoint.TrimEnd('/') + "/providers/microsoft/v1/systemone");
    }

    [Fact]
    public async Task Deployment_IsSentAsModel()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey), o => o.Deployment = "pi-decision-1");

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Last.Json.GetProperty("model").GetString().Should().Be("pi-decision-1");
    }

    [Fact]
    public async Task NoulWithoutCriteria_OmitsCriteria()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        await test.Client.DecideAsync("I was charged twice for October.", Decision1Question.Noul("Is this a billing problem?"), "billing");

        test.Handler.Last.Body.Should().Be(
            """{"model":"decision-1-test","state":"I was charged twice for October.","questions":{"billing":{"type":"noul","instructions":"Is this a billing problem?"}}}""");
    }

    [Fact]
    public async Task ChoiceCriteria_IsAJsonObjectInCallerOrder_NeverAnArray()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));
        var question = Decision1Question.Choice("Which report type is this?",
        [
            new("Income Statement", "Revenue and expenses"),
            new("Balance Sheet", "Assets and liabilities"),
            new("Cash Flow", "Cash movements"),
        ]);

        // The fixture answers "team", so the question uses that name.
        await test.Client.DecideAsync("Balance sheet as of 2026-09-30", question, "team");

        var criteria = test.Handler.Last.Json.GetProperty("questions").GetProperty("team").GetProperty("criteria");
        criteria.ValueKind.Should().Be(JsonValueKind.Object);
        criteria.EnumerateObject().Select(p => p.Name).Should().Equal("Income Statement", "Balance Sheet", "Cash Flow");
        criteria.GetProperty("Cash Flow").GetString().Should().Be("Cash movements");
    }

    [Fact]
    public async Task ScoreCriteria_IsAnArrayLowestFirst()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.Score10Levels));

        await test.Client.DecideAsync("The report total is off by 3 cents.",
            Decision1Question.Score("How material is this variance?", Enumerable.Range(1, 10).Select(i => i.ToString())), "materiality");

        test.Handler.Last.Body.Should().Be(
            """{"model":"decision-1-test","state":"The report total is off by 3 cents.","questions":{"materiality":{"type":"score","instructions":"How material is this variance?","criteria":["1","2","3","4","5","6","7","8","9","10"]}}}""");
    }

    [Fact]
    public async Task State_AcceptsJsonNode()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));
        var node = new JsonObject { ["invoice"] = 42, ["status"] = "paid twice" };

        await test.Client.DecideAsync(node, Questions.SingleNoul());

        test.Handler.Last.Json.GetProperty("state").GetRawText().Should().Be("""{"invoice":42,"status":"paid twice"}""");
    }

    [Fact]
    public async Task State_ObjectUsesTheClientSerializerOptions()
    {
        var snakeCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey), o => o.JsonSerializerOptions = snakeCase);

        await test.Client.DecideAsync(Decision1State.FromObject(new Ticket("Checkout is down again.", 3)), Questions.SingleNoul());

        test.Handler.Last.Json.GetProperty("state").GetRawText().Should().Be("""{"message":"Checkout is down again.","customer_contact_count":3}""");
    }

    [Fact]
    public async Task State_ObjectDefaultsToWebSerializerOptions()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        await test.Client.DecideAsync(Decision1State.FromObject(new Ticket("Down", 3)), Questions.SingleNoul());

        test.Handler.Last.Json.GetProperty("state").GetRawText().Should().Be("""{"message":"Down","customerContactCount":3}""");
    }

    [Fact]
    public async Task ApiKeyAuth_SendsApiKeyHeader_AndNoAuthorization()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Last.Headers["api-key"].Should().Be(TestClient.ApiKey);
        test.Handler.Last.Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task EntraAuth_SendsBearerToken_ForTheCognitiveServicesScope_AndNoApiKey()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        var credential = new FakeTokenCredential(time, TimeSpan.FromHours(1));
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices),
            o => { o.ApiKey = null; o.Credential = credential; }, time);

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        test.Handler.Last.Headers["Authorization"].Should().Be("Bearer synthetic-token-1");
        test.Handler.Last.Headers.Should().NotContainKey("api-key");
        credential.Scopes.Single().Should().Equal("https://cognitiveservices.azure.com/.default");
    }

    private sealed record Ticket(string Message, int CustomerContactCount);
}
