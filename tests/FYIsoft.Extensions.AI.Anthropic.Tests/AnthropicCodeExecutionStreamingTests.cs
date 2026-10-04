using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicCodeExecutionStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryFunctionStreaming_PreservesInitialAndFragmentedArguments(bool fragmented)
    {
        var events = new List<string> { ExecutionFixtures.Start,
            ExecutionFixtures.BlockStart(0, fragmented
                ? """{"type":"tool_use","id":"tool_ordinary","name":"lookup","input":{}}"""
                : """{"type":"tool_use","id":"tool_ordinary","name":"lookup","input":{"id":7}}""") };
        if (fragmented) events.Add(ExecutionFixtures.Delta(0, "input_json_delta", "partial_json", "{\"id\":7}"));
        events.AddRange([ExecutionFixtures.BlockStop(0), ExecutionFixtures.StopDelta("tool_use"), ExecutionFixtures.Stop]);
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(events) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = await client.GetStreamingResponseAsync(ExecutionFixtures.History()).ToChatResponseAsync();
        var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
        call.Name.Should().Be("lookup");
        ((JsonElement)call.Arguments!["id"]!).GetInt32().Should().Be(7);
        response.FinishReason.Should().Be(ChatFinishReason.ToolCalls);
        response.Messages.Single().AdditionalProperties!.Should().NotContainKey(M.ExecutionState);
    }

    private static async Task<List<ChatResponseUpdate>> Read(AnthropicChatClient client, CancellationToken ct = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options(), ct)) updates.Add(update);
        return updates;
    }

    [Fact]
    public async Task StreamAggregation_MatchesCompletedContent_AndSurvivesSerializedReplay()
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(ExecutionFixtures.Events()) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var updates = await Read(client);
        var response = updates.ToChatResponse();
        response.Text.Should().Be("Calculating.");
        response.Usage!.InputTokenCount.Should().Be(12);
        response.Usage.OutputTokenCount.Should().Be(34);
        var assistant = response.Messages.Single();
        assistant.MessageId.Should().Be("msg_test");
        assistant.AdditionalProperties![M.ExecutionState].Should().Be("complete");
        assistant.Contents.OfType<CodeInterpreterToolCallContent>().Should().ContainSingle();
        assistant.Contents.OfType<FunctionCallContent>().Single().Name.Should().Be("lookup");
        var result = assistant.Contents.OfType<CodeInterpreterToolResultContent>().Single();
        result.Outputs!.OfType<DataContent>().Select(c => c.Name).Should().Equal("stdout", "stderr");
        result.AdditionalProperties![M.Stderr].Should().Be("");
        var replay = (JsonElement)assistant.AdditionalProperties[M.ReplayBlocks]!;
        for (var i = 0; i < ExecutionFixtures.Blocks.Length; i++)
            JsonNode.DeepEquals(JsonNode.Parse(replay[i].GetProperty("block").GetRawText()), JsonNode.Parse(ExecutionFixtures.Blocks[i])).Should().BeTrue();
        handler.Respond = _ => FakeAnthropicHttpHandler.Json(ExecutionFixtures.Response);
        var history = ExecutionFixtures.History();
        history.Add(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(assistant))!);
        history.Add(new(ChatRole.User, "continue"));
        await client.GetResponseAsync(history, ExecutionFixtures.Options());
        handler.Requests[1].GetProperty("messages")[1].GetProperty("content").GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task IndexedBlocks_AccumulateSplitEscapesAndSignatures_EmitCallOnlyAtStop()
    {
        var input = JsonSerializer.Serialize(new { command = "printf \"a\nb\"" });
        string[] events = [ExecutionFixtures.Start,
            ExecutionFixtures.BlockStart(0, """{"type":"text","text":"A"}"""),
            ExecutionFixtures.BlockStart(1, """{"type":"server_tool_use","id":"srv_split","name":"bash_code_execution","input":{}}"""),
            ExecutionFixtures.Delta(1, "input_json_delta", "partial_json", input[..^3]),
            ExecutionFixtures.Delta(0, "text_delta", "text", "B"),
            ExecutionFixtures.Delta(1, "input_json_delta", "partial_json", input[^3..]),
            ExecutionFixtures.BlockStop(1), ExecutionFixtures.BlockStop(0),
            ExecutionFixtures.BlockStart(2, """{"type":"thinking","thinking":"","signature":""}"""),
            ExecutionFixtures.Delta(2, "thinking_delta", "thinking", "thought"),
            ExecutionFixtures.Delta(2, "signature_delta", "signature", "sig"),
            ExecutionFixtures.BlockStop(2), ExecutionFixtures.StopDelta(), ExecutionFixtures.Stop];
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(events) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var updates = await Read(client);
        updates.Where(u => u.AdditionalProperties?.ContainsKey(M.ExecutionProgress) == true).Should().HaveCount(2)
            .And.OnlyContain(u => u.Contents.Count == 0);
        var response = updates.ToChatResponse();
        response.Text.Should().Be("AB");
        var message = response.Messages.Single();
        var call = message.Contents.OfType<CodeInterpreterToolCallContent>().Single();
        ((JsonElement)call.AdditionalProperties![M.Input]!).GetRawText().Should().Be(input);
        var replay = (JsonElement)message.AdditionalProperties![M.ReplayBlocks]!;
        replay[0].GetProperty("block").GetProperty("text").GetString().Should().Be("AB");
        replay[2].GetProperty("block").GetProperty("signature").GetString().Should().Be("sig");
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("malformed")]
    [InlineData("unknown_delta")]
    [InlineData("unclosed")]
    [InlineData("length")]
    public async Task IncompleteStreams_DoNotInventCallsOrSuccessfulReplay(string mode)
    {
        var events = new List<string> { ExecutionFixtures.Start, ExecutionFixtures.BlockStart(0, """{"type":"text","text":""}"""),
            ExecutionFixtures.Delta(0, "text_delta", "text", "once") };
        if (mode != "unclosed") events.Add(ExecutionFixtures.BlockStop(0));
        if (mode is "malformed" or "unknown_delta")
        {
            events.Add(ExecutionFixtures.BlockStart(1, """{"type":"server_tool_use","id":"srv_bad","name":"code_execution","input":{}}"""));
            events.Add(ExecutionFixtures.Delta(1, mode == "malformed" ? "input_json_delta" : "future_delta", "partial_json", "{bad"));
            events.Add(ExecutionFixtures.BlockStop(1));
        }
        events.Add(ExecutionFixtures.StopDelta(mode == "length" ? "max_tokens" : "end_turn"));
        if (mode != "eof") events.Add(ExecutionFixtures.Stop);
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(events) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = (await Read(client)).ToChatResponse();
        response.Text.Should().Be("once");
        var assistant = response.Messages.Single();
        assistant.Contents.OfType<CodeInterpreterToolCallContent>().Should().BeEmpty();
        assistant.AdditionalProperties![M.ExecutionState].Should().Be("incomplete");
        var history = ExecutionFixtures.History(); history.Add(assistant); history.Add(new(ChatRole.User, "continue"));
        await FluentActions.Awaiting(() => client.GetResponseAsync(history, ExecutionFixtures.Options())).Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().HaveCount(1);
        if (mode == "unknown_delta") assistant.Contents.Should().Contain(c => c.AdditionalProperties != null && c.AdditionalProperties.ContainsKey("anthropic_raw_stream_event"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseTurn_RemainsPausedAndDoesNotAutoContinue(bool streaming)
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => streaming ? FakeAnthropicHttpHandler.Sse(ExecutionFixtures.Events("pause_turn")) :
            FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message([ExecutionFixtures.Call], "pause_turn")) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = streaming ? (await Read(client)).ToChatResponse() : await client.GetResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options());
        response.FinishReason.Should().Be(new ChatFinishReason("pause_turn"));
        response.Messages.Single().AdditionalProperties![M.ExecutionState].Should().Be("paused");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task CancellationAfterProgress_PropagatesWithoutTerminalSuccess()
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(ExecutionFixtures.Events()) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        using var cts = new CancellationTokenSource();
        var updates = new List<ChatResponseUpdate>();
        var action = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options(), cts.Token))
            { updates.Add(update); cts.Cancel(); }
        };
        await action.Should().ThrowAsync<OperationCanceledException>();
        updates.Should().NotContain(u => u.AdditionalProperties!.ContainsKey(M.ExecutionState));
        handler.Requests.Should().HaveCount(1);
    }
}

public class AnthropicCodeExecutionLifecycleTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task ProviderFailures_KeepStatusAndReason_WithoutFallback(int status)
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Json(
            """{"type":"error","error":{"type":"invalid_request_error","message":"container_expired"}}""", (HttpStatusCode)status) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        options.AdditionalProperties![M.ContainerId] = "expired_container";
        var failure = await FluentActions.Awaiting(() => client.GetResponseAsync(ExecutionFixtures.History(), options))
            .Should().ThrowAsync<global::Anthropic.Exceptions.AnthropicApiException>();
        ((int)failure.Which.StatusCode).Should().Be(status);
        failure.Which.ToString().Should().Contain("container_expired");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task TransportFailureAfterExecutionProgress_DoesNotRestartOrEmitSuccess()
    {
        string[] events = [ExecutionFixtures.Start,
            ExecutionFixtures.BlockStart(0, """{"type":"server_tool_use","id":"srv_partial","name":"code_execution","input":{}}"""),
            ExecutionFixtures.Delta(0, "input_json_delta", "partial_json", "{\"code\":")];
        using var sse = FakeAnthropicHttpHandler.Sse(events);
        var bytes = await sse.Content.ReadAsByteArrayAsync();
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(bytes)) { Headers = { ContentType = new("text/event-stream") } }
        } };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var updates = new List<ChatResponseUpdate>();
        var action = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options())) updates.Add(update);
        };
        await action.Should().ThrowAsync<Exception>();
        updates.Should().Contain(u => u.AdditionalProperties != null && u.AdditionalProperties.ContainsKey(M.ExecutionProgress));
        updates.Should().NotContain(u => u.AdditionalProperties != null && u.AdditionalProperties.ContainsKey(M.ExecutionState));
        handler.Requests.Should().HaveCount(1);
    }

    private sealed class FailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position >= Length ? ValueTask.FromException<int>(new IOException("synthetic stream interruption")) : base.ReadAsync(buffer, cancellationToken);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Execution_DisablesBothRetryLayers(bool streaming, bool serviceOnly, bool ioFailure)
    {
        using var handler = new FakeAnthropicHttpHandler
        {
            Respond = _ => ioFailure ? throw new HttpRequestException("secret-code-marker") :
                FakeAnthropicHttpHandler.Json("""{"type":"error","error":{"type":"api_error","message":"secret-code-marker"}}""", HttpStatusCode.InternalServerError)
        };
        using var sdk = handler.Sdk(retries: 3);
        var logger = new RecordingLogger();
        using var client = serviceOnly ? new AnthropicChatClient(sdk.Messages, "claude-test", logger: logger) : new AnthropicChatClient(sdk, "claude-test", logger);
        var action = async () =>
        {
            if (streaming) await client.GetStreamingResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options()).ToChatResponseAsync();
            else await client.GetResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options());
        };
        await action.Should().ThrowAsync<Exception>();
        handler.Requests.Should().HaveCount(1);
        sdk.MaxRetries.Should().Be(3, "the per-request service must not mutate the original SDK client");
        string.Join(' ', logger.Entries).Should().NotContain("secret-code-marker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationWithoutToolDeclaration_StillDisablesRetries(bool useHistory)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(retries: 3), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        options.Tools = null;
        if (useHistory) history.Add(response.Messages.Single());
        else options.AdditionalProperties![M.ContainerId] = "container_test";
        handler.Respond = _ => FakeAnthropicHttpHandler.Json("""{"type":"error","error":{"type":"api_error","message":"error"}}""", HttpStatusCode.InternalServerError);
        await FluentActions.Awaiting(() => client.GetResponseAsync(history, options)).Should().ThrowAsync<Exception>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task OrdinaryChat_RetainsRetryWithoutLoggingExceptionContentOrAddingExecution()
    {
        var logger = new RecordingLogger();
        using var handler = new FakeAnthropicHttpHandler { Respond = count => count == 1
            ? FakeAnthropicHttpHandler.Json("""{"type":"error","error":{"type":"api_error","message":"secret-output-marker"}}""", HttpStatusCode.InternalServerError)
            : FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message(["""{"type":"text","text":"ok"}"""])) };
        using var client = new AnthropicChatClient(handler.Sdk(retries: 0), "claude-test", logger);
        var response = await client.GetResponseAsync(ExecutionFixtures.History());
        handler.Requests.Should().HaveCount(2);
        response.Messages.Single().AdditionalProperties!.Should().NotContainKey(M.ExecutionState);
        string.Join(' ', logger.Entries).Should().NotContain("secret-output-marker");
        logger.Entries.Should().ContainSingle();
        var request = handler.Requests.Last();
        (request.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null).Should().BeFalse();
    }
}

