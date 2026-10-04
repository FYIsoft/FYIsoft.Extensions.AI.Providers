using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using P = Microsoft.Extensions.AI.Anthropic.AnthropicProviderMetadata;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicProviderFeaturesTests
{
    private const string Citation = """{"type":"page_location","cited_text":"Revenue was 42.","document_index":0,"document_title":"Report","start_page_number":1,"end_page_number":2}""";
    private static readonly string[] RichBlocks =
    [
        """{"type":"thinking","thinking":"Checking the report.","signature":"signed-test"}""",
        """{"type":"redacted_thinking","data":"opaque-test"}""",
        """{"type":"server_tool_use","id":"search_1","name":"web_search","input":{"query":"report"}}""",
        """{"type":"web_search_tool_result","tool_use_id":"search_1","content":[{"type":"web_search_result","url":"https://example.com/report","title":"Report","encrypted_content":"encrypted-test"}]}""",
        """{"type":"mcp_tool_use","id":"mcp_1","name":"lookup","server_name":"docs","input":{"query":"report"}}""",
        """{"type":"mcp_tool_result","tool_use_id":"mcp_1","is_error":false,"content":[{"type":"text","text":"Revenue was 42."}]}""",
        $$"""{"type":"text","text":"Revenue was 42.","citations":[{{Citation}}]}"""
    ];
    private static ChatOptions Options() => new() { AdditionalProperties = new() { [M.ProviderScope] = "rich-tests", [M.Hosting] = "foundry-azure" } };

    [Fact]
    public async Task Request_TranslatesReasoningPdfCacheWebSearchAndMcp_ThroughServiceOnlyConstructor()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk().Messages, "claude-test");
        var cache = JsonSerializer.SerializeToElement(new { type = "ephemeral", ttl = "1h" });
        var pdf = new DataContent(Encoding.ASCII.GetBytes("%PDF-test"), "application/pdf")
        { AdditionalProperties = new() { [P.CitationsEnabled] = true, [P.DocumentTitle] = "Report", [P.CacheControl] = cache } };
        var system = new TextContent("System instructions") { AdditionalProperties = new() { [P.CacheControl] = cache } };
        var options = Options();
        options.Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary };
        options.AdditionalProperties![P.CacheControl] = new { type = "ephemeral" };
        options.Tools =
        [
            new HostedWebSearchTool(new Dictionary<string, object?> { [P.WebSearchOptions] = new { max_uses = 2, allowed_domains = new[] { "example.com" } } }),
            new HostedMcpServerTool("docs", "https://example.com/mcp")
            { AllowedTools = ["lookup"], ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire, Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer synthetic-mcp-token" } },
            AIFunctionFactory.Create(() => "done", "local_function")
        ];
        await client.GetResponseAsync([new(ChatRole.System, [system]), new(ChatRole.User, [pdf, new TextContent("Summarize")])], options);
        var request = handler.Requests.Single();
        request.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        request.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");
        request.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        request.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString().Should().Be("1h");
        var document = request.GetProperty("messages")[0].GetProperty("content")[0];
        document.GetProperty("type").GetString().Should().Be("document");
        document.GetProperty("source").GetProperty("data").GetString().Should().Be(Convert.ToBase64String(pdf.Data.Span));
        document.GetProperty("citations").GetProperty("enabled").GetBoolean().Should().BeTrue();
        document.GetProperty("cache_control").GetProperty("ttl").GetString().Should().Be("1h");
        request.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("web_search_20250305");
        request.GetProperty("tools")[0].GetProperty("max_uses").GetInt32().Should().Be(2);
        var toolset = request.GetProperty("tools")[1];
        toolset.GetProperty("default_config").GetProperty("enabled").GetBoolean().Should().BeFalse();
        toolset.GetProperty("configs").GetProperty("lookup").GetProperty("enabled").GetBoolean().Should().BeTrue();
        request.GetProperty("mcp_servers")[0].GetProperty("authorization_token").GetString().Should().Be("synthetic-mcp-token");
        handler.BetaHeaders.Single().Should().Contain("mcp-client-2025-11-20");
        pdf.AdditionalProperties.Should().HaveCount(3, "request translation must not mutate caller content");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RichResponses_ProjectStandardTypes_AndReplayAfterJsonRoundTrip(bool streaming)
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => streaming
            ? FakeAnthropicHttpHandler.Sse(RichEvents()) : FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message(RichBlocks)) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "Summarize") };
        var response = streaming ? await client.GetStreamingResponseAsync(history, options).ToChatResponseAsync() : await client.GetResponseAsync(history, options);
        var message = response.Messages.Single();
        message.Text.Should().Be("Revenue was 42.");
        string.Concat(message.Contents.OfType<TextReasoningContent>().Select(c => c.Text)).Should().Be("Checking the report.");
        message.Contents.OfType<TextReasoningContent>().Should().Contain(c => c.ProtectedData == "signed-test");
        message.Contents.OfType<TextReasoningContent>().Should().Contain(c => c.ProtectedData == "opaque-test");
        message.Contents.OfType<WebSearchToolCallContent>().Single().Queries.Should().Equal("report");
        var search = message.Contents.OfType<WebSearchToolResultContent>().Single();
        search.Outputs!.Single().Annotations!.OfType<CitationAnnotation>().Single().Url!.Host.Should().Be("example.com");
        var mcp = message.Contents.OfType<McpServerToolCallContent>().Single();
        mcp.Name.Should().Be("lookup");
        mcp.ServerName.Should().Be("docs");
        mcp.Arguments!["query"]!.ToString().Should().Be("report");
        message.Contents.OfType<McpServerToolResultContent>().Single().Outputs!.OfType<TextContent>().Single().Text.Should().Be("Revenue was 42.");
        var citation = message.Contents.OfType<TextContent>().SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>().Single();
        citation.Title.Should().Be("Report");
        citation.Snippet.Should().Be("Revenue was 42.");
        ((JsonElement)citation.AdditionalProperties![P.Citation]!).GetProperty("start_page_number").GetInt32().Should().Be(1);
        message.AdditionalProperties![P.ResponseState].Should().Be("complete");
        message.AdditionalProperties.Should().NotContainKey(M.ExecutionState);
        history.Add(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!);
        history.Add(new(ChatRole.User, "Continue"));
        handler.Respond = _ => FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message(["""{"type":"text","text":"Done"}"""]));
        await client.GetResponseAsync(history, options);
        var replay = handler.Requests[1].GetProperty("messages")[1].GetProperty("content");
        handler.BetaHeaders[1].Should().Contain("mcp-client-2025-11-20", "MCP replay requires the beta header even without a new tool declaration");
        replay.GetArrayLength().Should().Be(RichBlocks.Length);
        for (var i = 0; i < RichBlocks.Length; i++)
            JsonNode.DeepEquals(JsonNode.Parse(replay[i].GetRawText()), JsonNode.Parse(RichBlocks[i])).Should().BeTrue();
    }

    private static IEnumerable<string> RichEvents()
    {
        yield return ExecutionFixtures.Start;
        for (var i = 0; i < RichBlocks.Length; i++)
        {
            if (i == 0)
            {
                yield return ExecutionFixtures.BlockStart(i, """{"type":"thinking","thinking":"","signature":""}""");
                yield return ExecutionFixtures.Delta(i, "thinking_delta", "thinking", "Checking the report.");
                yield return ExecutionFixtures.Delta(i, "signature_delta", "signature", "signed-test");
            }
            else if (i == 4)
            {
                yield return ExecutionFixtures.BlockStart(i, """{"type":"mcp_tool_use","id":"mcp_1","name":"lookup","server_name":"docs","input":{}}""");
                yield return ExecutionFixtures.Delta(i, "input_json_delta", "partial_json", "{\"query\":");
                yield return ExecutionFixtures.Delta(i, "input_json_delta", "partial_json", "\"report\"}");
            }
            else if (i == 6)
            {
                yield return ExecutionFixtures.BlockStart(i, """{"type":"text","text":""}""");
                yield return ExecutionFixtures.Delta(i, "text_delta", "text", "Revenue was 42.");
                yield return $$$"""{"type":"content_block_delta","index":6,"delta":{"type":"citations_delta","citation":{{{Citation}}}}}""";
            }
            else yield return ExecutionFixtures.BlockStart(i, RichBlocks[i]);
            yield return ExecutionFixtures.BlockStop(i);
        }
        yield return ExecutionFixtures.StopDelta();
        yield return ExecutionFixtures.Stop;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_IncludesCacheReadsWritesAndThinking_WithoutDoubleCounting(bool streaming)
    {
        const string usage = """{"input_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":20,"output_tokens":5,"cache_creation":{"ephemeral_1h_input_tokens":20},"output_tokens_details":{"thinking_tokens":3}}""";
        var response = JsonNode.Parse(ExecutionFixtures.Message([]))!.AsObject();
        response["usage"] = JsonNode.Parse(usage);
        var start = JsonNode.Parse(ExecutionFixtures.Start)!.AsObject();
        start["message"]!["usage"] = JsonNode.Parse(usage);
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => streaming ? FakeAnthropicHttpHandler.Sse(
            [start.ToJsonString(), """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}""", ExecutionFixtures.Stop]) : FakeAnthropicHttpHandler.Json(response.ToJsonString()) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var result = streaming ? await client.GetStreamingResponseAsync([new(ChatRole.User, "Hello")]).ToChatResponseAsync() : await client.GetResponseAsync([new(ChatRole.User, "Hello")]);
        result.Usage!.InputTokenCount.Should().Be(130);
        result.Usage.CachedInputTokenCount.Should().Be(100);
        result.Usage.OutputTokenCount.Should().Be(streaming ? 7 : 5);
        result.Usage.TotalTokenCount.Should().Be(streaming ? 137 : 135);
        result.Usage.ReasoningTokenCount.Should().Be(3);
        result.Usage.AdditionalCounts!["anthropic_ephemeral_1h_input_tokens"].Should().Be(20);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("header")]
    [InlineData("http")]
    [InlineData("duplicates")]
    [InlineData("full-reasoning")]
    [InlineData("budget")]
    [InlineData("cache")]
    public async Task UnsupportedOptions_FailBeforeSending(string scenario)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = Options();
        var mcp = new HostedMcpServerTool("docs", scenario == "http" ? "http://example.com" : "https://example.com");
        options.Tools = [mcp];
        switch (scenario)
        {
            case "approval": mcp.ApprovalMode = HostedMcpServerToolApprovalMode.AlwaysRequire; break;
            case "header": mcp.Headers = new Dictionary<string, string> { ["X-Api-Key"] = "secret-test" }; break;
            case "duplicates": options.Tools.Add(mcp); break;
            case "full-reasoning": options.Reasoning = new() { Output = ReasoningOutput.Full }; break;
            case "budget": options.AdditionalProperties![P.ThinkingBudgetTokens] = 10; break;
            case "cache": options.AdditionalProperties![P.CacheControl] = new { type = "ephemeral", ttl = "2h" }; break;
        }
        var act = () => client.GetResponseAsync([new(ChatRole.User, "Hello")], options);
        var failure = await act.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeOfType(scenario is "approval" or "header" or "full-reasoning" ? typeof(NotSupportedException) : typeof(ArgumentException));
        failure.Which.Message.Should().NotContain("secret-test");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task McpRequests_DoNotRetryPotentialSideEffects()
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Json("{}", HttpStatusCode.InternalServerError) };
        using var client = new AnthropicChatClient(handler.Sdk(2), "claude-test");
        var act = () => client.GetResponseAsync([new(ChatRole.User, "Hello")], new() { Tools = [new HostedMcpServerTool("docs", "https://example.com")] });
        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task IncompleteThinkingStream_CannotBeReplayed()
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(RichEvents().Take(3)) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var history = new List<ChatMessage> { new(ChatRole.User, "Think") };
        var response = await client.GetStreamingResponseAsync(history, Options()).ToChatResponseAsync();
        response.Messages.Single().AdditionalProperties![P.ResponseState].Should().Be("incomplete");
        history.AddRange(response.Messages);
        history.Add(new(ChatRole.User, "Continue"));
        Func<Task> replay = () => client.GetResponseAsync(history, Options());
        await replay.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task PdfUrlAndTextDocuments_TranslateWithoutLocalNetworkFetches()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        await client.GetResponseAsync([new(ChatRole.User, [new UriContent(new Uri("https://example.com/report.pdf"), "application/pdf"), new DataContent(Encoding.UTF8.GetBytes("Report text"), "text/plain")])]);
        var blocks = handler.Requests.Single().GetProperty("messages")[0].GetProperty("content");
        blocks[0].GetProperty("source").GetProperty("type").GetString().Should().Be("url");
        blocks[1].GetProperty("source").GetProperty("data").GetString().Should().Be("Report text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reasoning_ManualBudgetAndOmittedOutput(bool disabled)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var options = new ChatOptions { MaxOutputTokens = 4096, Reasoning = new() { Effort = disabled ? ReasoningEffort.None : null, Output = ReasoningOutput.None } };
        if (!disabled) options.AdditionalProperties = new() { [P.ThinkingBudgetTokens] = JsonSerializer.SerializeToElement(2048) };
        await client.GetResponseAsync([new(ChatRole.User, "Think")], options);
        var thinking = handler.Requests.Single().GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be(disabled ? "disabled" : "enabled");
        if (disabled) thinking.TryGetProperty("display", out _).Should().BeFalse();
        else
        {
            thinking.GetProperty("display").GetString().Should().Be("omitted");
            thinking.GetProperty("budget_tokens").GetInt32().Should().Be(2048);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpAllowlist_NullMeansAll_EmptyMeansNone(bool empty)
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var tool = new HostedMcpServerTool("docs", "https://example.com") { AllowedTools = empty ? [] : null };
        await client.GetResponseAsync([new(ChatRole.User, "List tools")], new() { Tools = [tool] });
        var set = handler.Requests.Single().GetProperty("tools")[0];
        set.GetProperty("default_config").GetProperty("enabled").GetBoolean().Should().Be(!empty);
        set.GetProperty("configs").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task HostedToolErrors_StayDistinctFromSuccessfulMessageCompletion()
    {
        string[] blocks =
        [
            """{"type":"web_search_tool_result","tool_use_id":"web1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}""",
            """{"type":"mcp_tool_result","tool_use_id":"mcp1","is_error":true,"content":[{"type":"text","text":"Tool failed"}]}"""
        ];
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Json(ExecutionFixtures.Message(blocks)) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var result = await client.GetResponseAsync([new(ChatRole.User, "Search")]);
        var contents = result.Messages.Single().Contents;
        contents.OfType<WebSearchToolResultContent>().Single().AdditionalProperties![P.IsError].Should().Be(true);
        contents.OfType<McpServerToolResultContent>().Single().AdditionalProperties![P.IsError].Should().Be(true);
        result.Messages.Single().AdditionalProperties![P.ResponseState].Should().Be("complete");
    }

    [Fact]
    public async Task OrdinaryStreamedFunctions_DoNotRequireRichHistoryScope()
    {
        using var handler = new FakeAnthropicHttpHandler { Respond = _ => FakeAnthropicHttpHandler.Sse(
            [ExecutionFixtures.Start, ExecutionFixtures.BlockStart(0, """{"type":"tool_use","id":"f1","name":"lookup","input":{}}"""), ExecutionFixtures.BlockStop(0), ExecutionFixtures.StopDelta("tool_use"), ExecutionFixtures.Stop]) };
        using var client = new AnthropicChatClient(handler.Sdk(), "claude-test");
        var response = await client.GetStreamingResponseAsync([new(ChatRole.User, "Lookup")]).ToChatResponseAsync();
        response.Messages.Single().Contents.OfType<FunctionCallContent>().Single().AdditionalProperties?.ContainsKey(M.RawContentBlock).Should().NotBe(true);
        response.Messages.Single().AdditionalProperties!.Should().NotContainKey(M.ReplayBlocks);
    }
}
