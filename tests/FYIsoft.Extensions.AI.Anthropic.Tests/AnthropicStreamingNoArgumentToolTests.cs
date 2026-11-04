using System.Text.Json;
using FluentAssertions;
using Xunit;
using F = Microsoft.Extensions.AI.Anthropic.Tests.ExecutionFixtures;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

/// <summary>
/// Claude streams a call to a tool with no parameters as content_block_start with "input":{}
/// followed by an empty input_json_delta ("partial_json": "") or no delta at all.
/// </summary>
public class AnthropicStreamingNoArgumentToolTests
{
    private const string NoArgTool = """{"type":"tool_use","id":"toolu_noargs","name":"list_things","input":{}}""";
    private const string ArgTool = """{"type":"tool_use","id":"toolu_args","name":"get_analysis","input":{}}""";
    private const string Thinking = """{"type":"thinking","thinking":"","signature":""}""";

    private static IEnumerable<string> NoArgBlock(int index, bool emptyDelta)
    {
        yield return F.BlockStart(index, NoArgTool);
        if (emptyDelta) yield return F.Delta(index, "input_json_delta", "partial_json", "");
        yield return F.BlockStop(index);
    }

    private static IEnumerable<string> ArgBlock(int index)
    {
        yield return F.BlockStart(index, ArgTool);
        yield return F.Delta(index, "input_json_delta", "partial_json", "");
        yield return F.Delta(index, "input_json_delta", "partial_json", "{\"sessionId\": \"royal-rain-058\", ");
        yield return F.Delta(index, "input_json_delta", "partial_json", "\"skillName\": \"KPI\"}");
        yield return F.BlockStop(index);
    }

    private static IEnumerable<string> ThinkingBlock(int index)
    {
        yield return F.BlockStart(index, Thinking);
        yield return F.Delta(index, "thinking_delta", "thinking", "I should list the things.");
        yield return F.Delta(index, "signature_delta", "signature", "CAQSkQoKEAgTGAI4AUIIdGhpbmtpbmc=");
        yield return F.BlockStop(index);
    }

    private static IEnumerable<string> Message(params IEnumerable<string>[] blocks) =>
        [F.Start, .. blocks.SelectMany(b => b), F.StopDelta("tool_use"), F.Stop];

    private static IEnumerable<string> TextReply() =>
        [F.Start, F.BlockStart(0, """{"type":"text","text":""}"""), F.Delta(0, "text_delta", "text", "Done."), F.BlockStop(0), F.StopDelta(), F.Stop];

    private static async Task<(List<ChatResponseUpdate> Updates, RecordingLogger Logger)> StreamAsync(IEnumerable<string> events)
    {
        var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(events) };
        var logger = new RecordingLogger();
        using var client = new AnthropicChatClient(handler.Sdk(retries: 0), "claude-test", logger);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);
        return (updates, logger);
    }

    private static void AssertCompleteToolCalls(List<ChatResponseUpdate> updates)
    {
        updates.Last().FinishReason.Should().Be(ChatFinishReason.ToolCalls);
        updates.Last().AdditionalProperties!["complete"].Should().Be(true);
        var message = updates.ToChatResponse().Messages.Should().ContainSingle().Subject;
        if (message.AdditionalProperties?.TryGetValue(AnthropicProviderMetadata.ResponseState, out var state) == true)
            state.Should().Be("complete");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleNoArgumentToolCall_IsEmittedWithEmptyArguments(bool emptyDelta)
    {
        var (updates, logger) = await StreamAsync(Message(NoArgBlock(0, emptyDelta)));

        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        call.Name.Should().Be("list_things");
        call.CallId.Should().Be("toolu_noargs");
        call.Arguments.Should().BeEmpty();
        AssertCompleteToolCalls(updates);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task NoArgumentAndArgumentToolCalls_AreBothEmitted()
    {
        var (updates, _) = await StreamAsync(Message(ArgBlock(0), NoArgBlock(1, emptyDelta: true)));

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        calls.Select(c => c.Name).Should().Equal("get_analysis", "list_things");
        calls[0].Arguments!.Keys.Should().BeEquivalentTo("sessionId", "skillName");
        calls[0].Arguments!["sessionId"]!.ToString().Should().Be("royal-rain-058");
        calls[1].Arguments.Should().BeEmpty();
        AssertCompleteToolCalls(updates);
    }

    [Fact]
    public async Task NoArgumentToolCallAfterThinking_IsEmittedWithEmptyArguments()
    {
        var (updates, _) = await StreamAsync(Message(ThinkingBlock(0), NoArgBlock(1, emptyDelta: true)));

        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        call.Name.Should().Be("list_things");
        call.Arguments.Should().BeEmpty();
        AssertCompleteToolCalls(updates);
    }

    [Fact]
    public async Task NextRequestInSameTurn_ReplaysNoArgumentToolCallWithoutThrowing()
    {
        var handler = new FakeAnthropicHttpHandler
        {
            Respond = n => FakeAnthropicHttpHandler.Sse(n == 1
                ? Message(ThinkingBlock(0), ArgBlock(1), NoArgBlock(2, emptyDelta: true))
                : TextReply())
        };
        using var inner = new AnthropicChatClient(handler.Sdk(retries: 0), "claude-test");
        using var client = inner.AsBuilder().UseFunctionInvocation().Build();
        var listed = 0;
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((string sessionId, string skillName) => "{\"success\":true}", "get_analysis"),
                AIFunctionFactory.Create(() => { listed++; return "{\"items\":[]}"; }, "list_things")
            ]
        };

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
            text += update.Text;

        text.Should().Be("Done.");
        listed.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
        var messages = handler.Requests[1].GetProperty("messages").EnumerateArray().ToList();
        var assistant = messages.Single(m => m.GetProperty("role").GetString() == "assistant").GetProperty("content").EnumerateArray().ToList();
        var noArgUse = assistant.Single(b => b.GetProperty("type").GetString() == "tool_use" && b.GetProperty("name").GetString() == "list_things");
        noArgUse.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Object);
        noArgUse.GetProperty("input").EnumerateObject().Should().BeEmpty();
        assistant.Select(b => b.GetProperty("type").GetString()).Should().StartWith("thinking");
        messages.Last().GetProperty("content").EnumerateArray()
            .Count(b => b.GetProperty("type").GetString() == "tool_result").Should().Be(2);
    }

    [Fact]
    public async Task MalformedToolInput_StaysIncompleteAndIsLogged()
    {
        IEnumerable<string> Broken()
        {
            yield return F.BlockStart(0, NoArgTool);
            yield return F.Delta(0, "input_json_delta", "partial_json", "{\"secret\": ");
            yield return F.BlockStop(0);
        }

        var (updates, logger) = await StreamAsync(Message(Broken()));

        updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Should().BeEmpty();
        updates.Last().FinishReason.Should().BeNull();
        updates.Last().AdditionalProperties!["complete"].Should().Be(false);
        logger.Entries.Should().ContainSingle().Which.Should()
            .Contain("list_things").And.Contain("not valid JSON").And.NotContain("secret");
    }
}
