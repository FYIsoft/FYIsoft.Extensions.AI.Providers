using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using P = Microsoft.Extensions.AI.Anthropic.AnthropicProviderMetadata;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

[Trait("Category", "Live")]
public class AnthropicProviderFeaturesLiveTests(ITestOutputHelper output)
{
    private AnthropicChatClient Client()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_TEST_MODEL")!;
        var resource = Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_RESOURCE");
        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; route={(resource is null ? "direct" : "foundry")}; model={model}");
        return resource is null ? new(new AnthropicClient { ApiKey = key }, model) : new(key, resource, model);
    }

    [AnthropicLiveFact]
    public async Task Thinking_StreamsSummaryAndSignature_ThenReplaysSavedHistory()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var options = new ChatOptions { MaxOutputTokens = 4096, Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary } };
        var history = new List<ChatMessage> { new(ChatRole.User, "Find the smallest positive integer x such that x mod 7 = 3, x mod 11 = 5, and x mod 13 = 7. Verify your result. Keep the final answer short.") };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(history, options, cts.Token)) updates.Add(update);
        updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Should().Contain(c => !string.IsNullOrEmpty(c.Text));
        var response = updates.ToChatResponse();
        response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Should().Contain(c => !string.IsNullOrEmpty(c.ProtectedData));
        history.AddRange(JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(response.Messages))!);
        history.Add(new(ChatRole.User, "What were the three divisors? Reply only with the numbers."));
        var continued = await client.GetResponseAsync(history, options, cts.Token);
        continued.Text.Should().Contain("7").And.Contain("11").And.Contain("13");
        output.WriteLine($"Thinking updates={updates.Count(u => u.Contents.OfType<TextReasoningContent>().Any())}; replay succeeded.");
    }

    [AnthropicLiveFact]
    public async Task PdfDocument_ReturnsPageCitations_CompletedAndStreaming()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pdf = new DataContent(SyntheticPdf(), "application/pdf")
        { AdditionalProperties = new() { [P.CitationsEnabled] = true, [P.DocumentTitle] = "Synthetic quarterly report" } };
        var history = new List<ChatMessage> { new(ChatRole.User, [pdf, new TextContent("What was the quarterly revenue? Cite the document. Keep the answer to one sentence.")]) };
        var options = new ChatOptions { MaxOutputTokens = 2048 };
        var completed = await client.GetResponseAsync(history, options, cts.Token);
        var streamed = await client.GetStreamingResponseAsync(history, options, cts.Token).ToChatResponseAsync(cts.Token);
        foreach (var response in new[] { completed, streamed })
        {
            response.Text.Should().Contain("42");
            var citations = response.Messages.SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>().ToList();
            citations.Should().NotBeEmpty();
            citations.Should().Contain(c => ((JsonElement)c.AdditionalProperties![P.Citation]!).GetProperty("type").GetString() == "page_location");
        }
        output.WriteLine("PDF base64 accepted; page citations present in completed and streamed responses.");
    }

    [AnthropicLiveFact]
    public async Task PromptCache_ReportsCreationAndSubsequentRead()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var prefix = "Synthetic cache verification " + Guid.NewGuid().ToString("N") + "\n" + string.Join('\n', Enumerable.Range(1, 900)
            .Select(i => $"Record {i}: the fictional warehouse stores blue widgets in aisle seven; this is synthetic test data."));
        var system = new TextContent(prefix) { AdditionalProperties = new() { [P.CacheControl] = new { type = "ephemeral" } } };
        var history = new List<ChatMessage> { new(ChatRole.System, [system]), new(ChatRole.User, "Reply with just CACHE_OK.") };
        var options = new ChatOptions { MaxOutputTokens = 256 };
        var first = await client.GetResponseAsync(history, options, cts.Token);
        var second = await client.GetStreamingResponseAsync(history, options, cts.Token).ToChatResponseAsync(cts.Token);
        first.Usage!.AdditionalCounts!["anthropic_cache_creation_input_tokens"].Should().BePositive();
        second.Usage!.CachedInputTokenCount.Should().BePositive();
        output.WriteLine($"Cache creation={first.Usage.AdditionalCounts["anthropic_cache_creation_input_tokens"]}; cache read={second.Usage.CachedInputTokenCount}; total input={second.Usage.InputTokenCount}.");
    }

    [AnthropicLiveFact]
    public async Task WebSearch_ReturnsHostedCallsResultsAndCitationLinks()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var response = await client.GetStreamingResponseAsync(
            [new(ChatRole.User, "Use web search to find Microsoft's official documentation for .NET 10. Include a citation link and a one-sentence description.")],
            new() { MaxOutputTokens = 4096, Tools = [new HostedWebSearchTool(new Dictionary<string, object?> { [P.WebSearchOptions] = new { max_uses = 1 } })] }, cts.Token).ToChatResponseAsync(cts.Token);
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        contents.OfType<WebSearchToolCallContent>().Should().NotBeEmpty();
        contents.OfType<WebSearchToolResultContent>().Should().Contain(r => r.Outputs != null && r.Outputs.Count > 0);
        contents.SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>().Should().Contain(c => c.Url != null);
        output.WriteLine("Hosted web search calls, results, and answer citation links received.");
    }

    [AnthropicLiveFact]
    public async Task Mcp_ReadOnlyMicrosoftLearnSearch_ReturnsHostedCallsAndResults()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var tool = new HostedMcpServerTool("microsoft_learn", "https://learn.microsoft.com/api/mcp")
        { AllowedTools = ["microsoft_docs_search"], ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire };
        var response = await client.GetStreamingResponseAsync(
            [new(ChatRole.User, "Call microsoft_docs_search on microsoft_learn for '.NET 10 overview'. Return one documentation title from the tool result.")],
            new() { MaxOutputTokens = 4096, Tools = [tool] }, cts.Token).ToChatResponseAsync(cts.Token);
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        contents.OfType<McpServerToolCallContent>().Should().Contain(c => c.Name == "microsoft_docs_search" && c.ServerName == "microsoft_learn");
        contents.OfType<McpServerToolResultContent>().Should().Contain(r => r.Outputs != null && r.Outputs.Count > 0 && Equals(r.AdditionalProperties![P.IsError], false));
        output.WriteLine("Allowlisted public read-only MCP search returned a successful typed result.");
    }

    // A deterministic single-page fixture; no external PDF or private data is uploaded.
    private static byte[] SyntheticPdf()
    {
        const string text = "BT /F1 18 Tf 40 740 Td (Synthetic Quarterly Report) Tj 0 -30 Td (Quarterly revenue was 42 million dollars.) Tj 0 -30 Td (This report contains synthetic test data only.) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {text.Length} >>\nstream\n{text}\nendstream"
        ];
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
