using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Foundry;
using Anthropic.Models.Beta.Files;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public sealed class AnthropicLiveFactAttribute : FactAttribute
{
    public AnthropicLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANTHROPIC_LIVE_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_TEST_MODEL")))
            Skip = "Set ANTHROPIC_LIVE_TESTS=1, ANTHROPIC_API_KEY and ANTHROPIC_TEST_MODEL to run live tests.";
    }
}

[Trait("Category", "Live")]
public class AnthropicCodeExecutionLiveTests(ITestOutputHelper output)
{
    private AnthropicChatClient Client()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_TEST_MODEL")!;
        var resource = Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_RESOURCE");
        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; connection={(resource is null ? "direct" : "foundry (hosting arrangement unverified)")}; model={model}; tool=code_execution_20250825; Anthropic={typeof(AnthropicClient).Assembly.GetName().Version}; MEAI={typeof(IChatClient).Assembly.GetName().Version}");
        return resource is null ? new(new AnthropicClient { ApiKey = key }, model) : new(key, resource, model);
    }

    [AnthropicLiveFact]
    public async Task OrdinaryChat_CompletedAndStreamingResponsesReturnTextAndUsage()
    {
        using var chat = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var history = new List<ChatMessage> { new(ChatRole.User, "Reply with exactly SDK_OK.") };
        var completed = await chat.GetResponseAsync(history, cancellationToken: cts.Token);
        var streamed = await chat.GetStreamingResponseAsync(history, cancellationToken: cts.Token).ToChatResponseAsync(cts.Token);
        foreach (var response in new[] { completed, streamed })
        {
            response.Text.Should().Contain("SDK_OK");
            response.Usage!.InputTokenCount.Should().BePositive();
            response.Usage.OutputTokenCount.Should().BePositive();
            response.Messages.Single().AdditionalProperties!.Should().NotContainKey(M.ExecutionState);
        }
    }

    [AnthropicLiveFact]
    public async Task OrdinaryFunction_RequiredSelectionAndResultContinuation()
    {
        using var chat = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var history = new List<ChatMessage> { new(ChatRole.User, "Call lookup_order for order 123, then report its status.") };
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string orderId) => "shipped", "lookup_order")],
            ToolMode = ChatToolMode.RequireSpecific("lookup_order")
        };
        var response = await chat.GetResponseAsync(history, options, cts.Token);
        var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
        call.Name.Should().Be("lookup_order");
        call.Arguments!["orderId"]!.ToString().Should().Be("123");
        history.AddRange(response.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "shipped")]));
        options.ToolMode = ChatToolMode.None;
        var continued = await chat.GetStreamingResponseAsync(history, options, cts.Token).ToChatResponseAsync(cts.Token);
        continued.Text.ToLowerInvariant().Should().Contain("shipped");
        continued.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should().BeEmpty();
    }

    [AnthropicLiveFact]
    public async Task Calculation_StreamsActualExecutionAndCorrectOutput()
    {
        using var chat = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var response = await chat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Use code execution to calculate the sum of squares from 1 through 100. Print the integer answer to stdout.")],
            ExecutionFixtures.Options(), cts.Token).ToChatResponseAsync(cts.Token);
        var message = response.Messages.Single();
        message.Contents.OfType<CodeInterpreterToolCallContent>().Should().NotBeEmpty();
        var results = message.Contents.OfType<CodeInterpreterToolResultContent>().ToList();
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.AdditionalProperties != null && r.AdditionalProperties.ContainsKey(M.Stdout) &&
            r.AdditionalProperties[M.Stdout]!.ToString()!.Contains("338350"));
        message.AdditionalProperties![M.ExecutionState].Should().Be("complete");
    }

    [AnthropicLiveFact]
    public async Task UploadedCsv_GeneratedChartDownload_AndExplicitContainerContinuation()
    {
        using var chat = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var sdk = chat.GetService<IAnthropicClient>()!;
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("category,value\nA,10\nB,20\nC,30\n"));
        var upload = await sdk.Beta.Files.Upload(new FileUploadParams
        {
            File = new BinaryContent { Stream = csv, FileName = "sdk-smoke.csv", ContentType = new MediaTypeHeaderValue("text/csv") }
        }, cts.Token);
        try
        {
            var options = ExecutionFixtures.Options();
            ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new HostedFileContent(upload.ID)];
            var history = new List<ChatMessage> { new(ChatRole.User,
                "Use code execution to read the CSV. Print the sum of the value column to stdout. Create a PNG bar chart, copy it into $OUTPUT_DIR and list that directory in the same command. Also save the total to /tmp/sdk-total.txt for our next turn.") };
            var response = await chat.GetResponseAsync(history, options, cts.Token);
            var message = response.Messages.Single();
            message.AdditionalProperties![M.ExecutionState].Should().Be("complete");
            var results = message.Contents.OfType<CodeInterpreterToolResultContent>().ToList();
            results.Should().Contain(r => r.AdditionalProperties != null && r.AdditionalProperties.ContainsKey(M.Stdout) &&
                r.AdditionalProperties[M.Stdout]!.ToString()!.Contains("60"));
            var files = results.SelectMany(r => r.Outputs ?? []).OfType<HostedFileContent>().ToList();
            files.Should().NotBeEmpty();
            bool foundPng = false;
            foreach (var file in files)
            {
                var metadata = await sdk.Beta.Files.RetrieveMetadata(file.FileId, cancellationToken: cts.Token);
                if (metadata.MimeType != "image/png") continue;
                using var download = await sdk.Beta.Files.Download(file.FileId, cancellationToken: cts.Token);
                await using var stream = await download.ReadAsStream(cts.Token);
                var signature = new byte[8];
                await stream.ReadExactlyAsync(signature, cts.Token);
                signature.Should().Equal([137, 80, 78, 71, 13, 10, 26, 10]);
                foundPng = true;
            }
            foundPng.Should().BeTrue();
            // Persist and restore history as a real consumer would.
            history.Add(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!);
            history.Add(new(ChatRole.User, "Use code execution to print the contents of /tmp/sdk-total.txt to stdout."));
            options.AdditionalProperties![M.ContainerId] = message.AdditionalProperties[M.ContainerId];
            ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = null;
            var continued = await chat.GetResponseAsync(history, options, cts.Token);
            continued.Messages.Single().AdditionalProperties![M.ContainerId].Should().Be(message.AdditionalProperties[M.ContainerId]);
            continued.Messages.Single().Contents.OfType<CodeInterpreterToolResultContent>().Should().Contain(r =>
                r.AdditionalProperties != null && r.AdditionalProperties.ContainsKey(M.Stdout) && r.AdditionalProperties[M.Stdout]!.ToString()!.Contains("60"));
        }
        finally
        {
            // This opt-in test owns its synthetic upload; no caller files are deleted.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await sdk.Beta.Files.Delete(upload.ID, cancellationToken: cleanup.Token);
        }
    }
}
