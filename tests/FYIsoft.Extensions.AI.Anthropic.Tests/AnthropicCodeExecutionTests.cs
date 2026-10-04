using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicCodeExecutionTests
{
    [Fact]
    public async Task CompletedResponse_ProjectsExecutionFilesAndUnknowns_AndPersistsLosslessHistory()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        var message = response.Messages.Single();
        response.Text.Should().Be("Calculating.");
        message.Contents.OfType<FunctionCallContent>().Single().Name.Should().Be("lookup");
        message.MessageId.Should().Be("msg_test");
        message.AdditionalProperties![M.ExecutionState].Should().Be("complete");
        message.AdditionalProperties[M.ContainerId].Should().Be("container_test");
        var call = message.Contents.OfType<CodeInterpreterToolCallContent>().Single();
        call.CallId.Should().Be("srv_1");
        call.Inputs!.OfType<DataContent>().Single().MediaType.Should().Be("text/x-shellscript");
        call.AdditionalProperties![M.Operation].Should().Be("bash_code_execution");
        var result = message.Contents.OfType<CodeInterpreterToolResultContent>().Single();
        result.CallId.Should().Be(call.CallId);
        result.AdditionalProperties![M.Outcome].Should().Be("succeeded");
        result.AdditionalProperties[M.Stderr].Should().Be("");
        result.Outputs!.OfType<DataContent>().Select(c => c.Name).Should().Equal("stdout", "stderr");
        var file = result.Outputs!.OfType<HostedFileContent>().Single();
        file.FileId.Should().Be("file_chart");
        file.AdditionalProperties![M.ProviderScope].Should().Be("test-connection");
        handler.Requests.Should().HaveCount(1, "file projection must not download or fetch metadata");
        history.Add(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("tool_1", "lookup result")]));
        options.AdditionalProperties![M.ContainerId] = JsonSerializer.SerializeToElement("container_test");
        await client.GetResponseAsync(history, options);
        var request = handler.Requests[1];
        request.GetProperty("container").GetString().Should().Be("container_test");
        var replay = request.GetProperty("messages")[1].GetProperty("content");
        replay.GetArrayLength().Should().Be(ExecutionFixtures.Blocks.Length);
        for (int i = 0; i < ExecutionFixtures.Blocks.Length; i++)
            JsonNode.DeepEquals(JsonNode.Parse(replay[i].GetRawText()), JsonNode.Parse(ExecutionFixtures.Blocks[i])).Should().BeTrue();
    }

    [Theory]
    [InlineData("code_execution", "code", "text/x-python")]
    [InlineData("bash_code_execution", "command", "text/x-shellscript")]
    [InlineData("text_editor_code_execution", "command", "application/json")]
    public async Task Operations_PreserveTheirInputAndLanguage(string operation, string key, string mediaType)
    {
        var block = JsonSerializer.Serialize(new { type = "server_tool_use", id = "srv_test", name = operation, input = new Dictionary<string, object> { [key] = "test", ["extra"] = 1 } });
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message([block])) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = await client.GetResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options());
        var call = response.Messages.Single().Contents.OfType<CodeInterpreterToolCallContent>().Single();
        call.Inputs!.OfType<DataContent>().Single().MediaType.Should().Be(mediaType);
        ((JsonElement)call.AdditionalProperties![M.Input]!).GetProperty("extra").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("{\"type\":\"code_execution_result\",\"return_code\":1,\"stdout\":\"\",\"stderr\":\"bad\"}", "failed")]
    [InlineData("{\"type\":\"code_execution_tool_result_error\",\"error_code\":\"execution_time_exceeded\"}", "failed")]
    [InlineData("{\"type\":\"future_result\"}", "unknown")]
    [InlineData("{\"type\":\"text_editor_code_execution_create_result\",\"is_file_update\":false}", "succeeded")]
    public async Task ResultOutcomes_AreIndependentOfMessageCompletion(string body, string outcome)
    {
        var block = $$"""{"type":"code_execution_tool_result","tool_use_id":"previous_turn_call","content":{{body}}}""";
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message([block])) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = await client.GetResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options());
        var message = response.Messages.Single();
        message.Contents.OfType<CodeInterpreterToolResultContent>().Single().AdditionalProperties![M.Outcome].Should().Be(outcome);
        message.AdditionalProperties![M.ExecutionState].Should().Be("complete");
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("none", "none")]
    [InlineData("required", "any")]
    [InlineData("lookup", "tool")]
    public async Task HostedAndFunctionTools_PreserveToolSelection(string mode, string expected)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        options.Tools!.Add(AIFunctionFactory.Create(() => "value", "lookup"));
        options.ToolMode = mode switch { "none" => ChatToolMode.None, "required" => ChatToolMode.RequireAny, "lookup" => ChatToolMode.RequireSpecific("lookup"), _ => ChatToolMode.Auto };
        await client.GetResponseAsync(ExecutionFixtures.History(), options);
        var request = handler.Requests.Single();
        request.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be(expected);
        request.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("code_execution_20250825");
        if (mode == "lookup") request.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("lookup");
    }

    [Fact]
    public async Task ToolFiles_AreScopedDeduplicatedAndDoNotMutateCallerHistory()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new HostedFileContent("file_csv"), new HostedFileContent("file_csv"), new HostedFileContent("file_other")];
        var history = ExecutionFixtures.History();
        history[0].Contents.Add(new HostedFileContent("file_csv"));
        await client.GetResponseAsync(history, options);
        history[0].Contents.Should().HaveCount(2);
        var blocks = handler.Requests.Single().GetProperty("messages")[0].GetProperty("content");
        blocks.GetArrayLength().Should().Be(3);
        blocks[1].GetProperty("file_id").GetString().Should().Be("file_csv");
        blocks[2].GetProperty("file_id").GetString().Should().Be("file_other");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("input")]
    [InlineData("file_scope")]
    [InlineData("file_provider")]
    [InlineData("unscoped")]
    [InlineData("container")]
    [InlineData("hosting")]
    [InlineData("scope_type")]
    [InlineData("history")]
    [InlineData("missing_user")]
    public async Task InvalidRequests_FailBeforeHttp(string kind)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        switch (kind)
        {
            case "duplicate": options.Tools!.Add(new HostedCodeInterpreterTool()); break;
            case "input": ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new TextContent("code")]; break;
            case "file_scope": history[0].Contents.Add(new HostedFileContent("file") { AdditionalProperties = new() { [M.ProviderScope] = "another" } }); break;
            case "file_provider": history[0].Contents.Add(new HostedFileContent("file") { AdditionalProperties = new() { [M.Provider] = "other-provider" } }); break;
            case "unscoped": options.AdditionalProperties!.Clear(); history[0].Contents.Add(new HostedFileContent("file")); break;
            case "container": options.AdditionalProperties!.Clear(); options.AdditionalProperties[M.ContainerId] = "container"; break;
            case "hosting": options.AdditionalProperties![M.Hosting] = "foundry-azure"; break;
            case "scope_type": options.AdditionalProperties![M.ProviderScope] = 5; break;
            case "history": history.Add(new(ChatRole.Assistant, [new CodeInterpreterToolCallContent("srv")])); break;
            case "missing_user": history.Clear(); history.Add(new(ChatRole.System, "system")); ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new HostedFileContent("file")]; break;
        }
        var action = () => client.GetResponseAsync(history, options);
        await action.Should().ThrowAsync<Exception>().Where(e => e is ArgumentException || e is NotSupportedException);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("provider")]
    [InlineData("version")]
    [InlineData("role")]
    [InlineData("complete")]
    [InlineData("blockIndex")]
    [InlineData("messageId")]
    [InlineData("message_state")]
    public async Task InvalidReplay_IsRejected(string field)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        var message = response.Messages.Single();
        if (field == "message_state") message.AdditionalProperties![M.ExecutionState] = "incomplete";
        else
        {
            var replay = JsonNode.Parse(((JsonElement)message.AdditionalProperties![M.ReplayBlocks]!).GetRawText())!.AsArray();
            replay[1]![field] = field switch { "version" => JsonValue.Create(99), "complete" => JsonValue.Create(false), "blockIndex" => JsonValue.Create(0), _ => JsonValue.Create("invalid") };
            message.AdditionalProperties[M.ReplayBlocks] = JsonSerializer.SerializeToElement(replay);
        }
        history.Add(message);
        history.Add(new(ChatRole.User, "continue"));
        await FluentActions.Awaiting(() => client.GetResponseAsync(history, options)).Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task InterleavedConversations_DoNotShareContainerState()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var a = ExecutionFixtures.Options();
        a.AdditionalProperties![M.ContainerId] = "container_A";
        await client.GetResponseAsync(ExecutionFixtures.History(), a);
        await client.GetResponseAsync(ExecutionFixtures.History(), ExecutionFixtures.Options());
        handler.Requests[0].GetProperty("container").GetString().Should().Be("container_A");
        (handler.Requests[1].TryGetProperty("container", out var container) && container.ValueKind != JsonValueKind.Null).Should().BeFalse();
    }

    [Fact]
    public async Task PerContentEnvelopes_ReplayExactlyOnce_WhenMessageCollectionIsAbsent()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        var assistant = response.Messages.Single();
        assistant.AdditionalProperties!.Remove(M.ReplayBlocks);
        history.Add(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(assistant))!);
        history.Add(new(ChatRole.User, "continue"));
        await client.GetResponseAsync(history, options);
        handler.Requests[1].GetProperty("messages")[1].GetProperty("content").GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task DefaultScope_IsStableWithinClient_AndRejectedAcrossClients()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        history.Add(response.Messages.Single());
        history.Add(new(ChatRole.User, "continue"));
        await client.GetResponseAsync(history, options);
        using var other = new AnthropicChatClient(handler.Sdk(), "claude-test");
        await FluentActions.Awaiting(() => other.GetResponseAsync(history, options)).Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task CanonicalReplay_WinsOverMutatedDisplayProjection()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = ExecutionFixtures.Options();
        var history = ExecutionFixtures.History();
        var response = await client.GetResponseAsync(history, options);
        var assistant = response.Messages.Single();
        assistant.Contents.OfType<TextContent>().Single().Text = "display edit";
        assistant.Contents.OfType<CodeInterpreterToolResultContent>().Single().Outputs!.Clear();
        history.Add(assistant);
        history.Add(new(ChatRole.User, "continue"));
        await client.GetResponseAsync(history, options);
        var replay = handler.Requests[1].GetProperty("messages")[1].GetProperty("content");
        replay[0].GetProperty("text").GetString().Should().Be("Calculating.");
        replay[3].GetProperty("content").GetProperty("content")[0].GetProperty("file_id").GetString().Should().Be("file_chart");
    }
}

