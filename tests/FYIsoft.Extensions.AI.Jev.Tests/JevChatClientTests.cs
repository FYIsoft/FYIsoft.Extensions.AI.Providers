using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.Extensions.AI.Jev.Tests;

public class JevChatClientTests
{
    public enum Department
    {
        Billing,
        Technical,
        Sales,
    }

    public sealed record TicketTriage(
        [property: Description("Does this convey urgency?")] bool IsUrgent,
        [property: Description("Which team should handle this?")] Department Department);

    private const string TriageResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "isUrgent": { "type": "noul", "noul": 0.91 },
            "department": { "type": "choice", "choice": "Technical", "probabilities": { "Billing": 0.1, "Technical": 0.85, "Sales": 0.05 }, "confidence": 0.8 }
          },
          "usage": { "input_tokens": 120, "output_tokens": 7 }
        }
        """;

    [Fact]
    public async Task GetResponseAsyncOfT_DerivesQuestionsFromSchema()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse, r => r.Headers.Add("x-typesafe-request-id", "req_9"));
        using var client = FakeHttpHandler.CreateClient(handler);
        IChatClient chat = client.AsIChatClient();

        var response = await chat.GetResponseAsync<TicketTriage>("The export API has returned 500s since this morning.");

        response.Result.Should().Be(new TicketTriage(true, Department.Technical));
        response.ModelId.Should().Be("jev-1.13.0");
        response.ResponseId.Should().Be("req_9");
        response.Usage!.InputTokenCount.Should().Be(120);
        response.AsJevResponse()!.GetAnswer<JevChoiceAnswer>("department").Confidence.Should().Be(0.8);

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        body["state"]!.GetValue<string>().Should().StartWith("The export API");
        body["questions"]!["isUrgent"]!.ToJsonString().Should().Be("""{"type":"noul","instructions":"Does this convey urgency?"}""");
        body["questions"]!["department"]!["type"]!.GetValue<string>().Should().Be("choice");
        body["questions"]!["department"]!["criteria"]!.AsObject().Select(p => p.Key).Should().Equal("Billing", "Technical", "Sales");
    }

    [Fact]
    public async Task ExplicitQuestions_ReturnRawAnswersJson()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse);
        using var client = FakeHttpHandler.CreateClient(handler);
        using var chat = new JevChatClient(client, "jev-preview");

        var options = new ChatOptions().WithJevQuestions(new Dictionary<string, JevQuestion>
        {
            ["isUrgent"] = JevQuestion.Noul("Urgent?"),
            ["department"] = JevQuestion.Choice("Team?", "Billing", "Technical", "Sales"),
        });

        var response = await chat.GetResponseAsync("text", options);

        var answers = JsonNode.Parse(response.Text)!;
        answers["isUrgent"]!["noul"]!.GetValue<double>().Should().Be(0.91);
        answers["department"]!["choice"]!.GetValue<string>().Should().Be("Technical");
        JsonNode.Parse(handler.Requests.Single().Body!)!["model"]!.GetValue<string>().Should().Be("jev-preview");
    }

    [Fact]
    public async Task Streaming_YieldsSingleCompleteUpdate()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler().Respond(HttpStatusCode.OK, TriageResponse));
        using var chat = new JevChatClient(client);
        var options = new ChatOptions().WithJevQuestions(new Dictionary<string, JevQuestion> { ["isUrgent"] = JevQuestion.Noul("Urgent?") });

        var updates = await chat.GetStreamingResponseAsync("text", options).ToListAsync();

        updates.ToChatResponse().Text.Should().Contain("\"isUrgent\"");
        updates.Should().Contain(u => u.Contents.OfType<UsageContent>().Any());
    }

    [Fact]
    public void BuildState_ConversationBecomesMessagesObject()
    {
        var state = JevChatClient.BuildState(
            [new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Assistant, "hello")],
            instructions: "Support chat");

        state.ToJsonString().Should().Be(
            """{"messages":[{"from":"system","text":"Support chat"},{"from":"user","text":"hi"},{"from":"assistant","text":"hello"}]}""");
    }

    [Fact]
    public void BuildState_RejectsNonTextContent()
    {
        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")]);

        FluentActions.Invoking(() => JevChatClient.BuildState([message], null)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task NoQuestions_ThrowsHelpfulError()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler());
        using var chat = new JevChatClient(client);

        await FluentActions.Awaiting(() => chat.GetResponseAsync("text")).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WithJevQuestions*");
    }

    [Fact]
    public async Task UnsupportedSchemaProperty_Throws()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler());
        using var chat = new JevChatClient(client);

        await FluentActions.Awaiting(() => chat.GetResponseAsync<Unsupported>("text")).Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*'summary'*");
    }

    [Fact]
    public void GetService_ReturnsClientAndMetadata()
    {
        using var client = FakeHttpHandler.CreateClient(new FakeHttpHandler());
        using var chat = new JevChatClient(client);

        chat.GetService<JevClient>().Should().BeSameAs(client);
        chat.GetService<ChatClientMetadata>()!.ProviderName.Should().Be("typesafe");
        chat.GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("jev-latest");
    }

    [Fact]
    public void AddJevChatClient_RegistersClients()
    {
        var services = new ServiceCollection().AddJevChatClient(o => o.ApiKey = "k", modelId: "jev-preview");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("jev-preview");
        provider.GetRequiredService<IChatClient>().GetService<JevClient>().Should().BeSameAs(provider.GetRequiredService<JevClient>());
    }

    public sealed record Unsupported(bool Flag, string Summary);
}
