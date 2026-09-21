using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Models.Messages;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicStreamingConverterUsageTests
{
    private static readonly ChatClientMetadata Metadata =
        new("anthropic", new Uri("https://api.anthropic.com"), "claude-sonnet-4-5");

    // Real-shape Anthropic SSE payloads: input_tokens arrive on message_start,
    // output_tokens arrive later on message_delta.
    private const string MessageStartJson = """
        {
          "type": "message_start",
          "message": {
            "id": "msg_01Test",
            "type": "message",
            "role": "assistant",
            "model": "claude-sonnet-4-5",
            "content": [],
            "stop_reason": null,
            "stop_sequence": null,
            "usage": { "input_tokens": 1234, "output_tokens": 1 }
          }
        }
        """;

    private const string ContentBlockStartJson = """
        {
          "type": "content_block_start",
          "index": 0,
          "content_block": { "type": "text", "text": "" }
        }
        """;

    private const string ContentBlockDeltaJson = """
        {
          "type": "content_block_delta",
          "index": 0,
          "delta": { "type": "text_delta", "text": "Hello" }
        }
        """;

    private const string ContentBlockStopJson = """
        { "type": "content_block_stop", "index": 0 }
        """;

    private const string MessageDeltaJson = """
        {
          "type": "message_delta",
          "delta": { "stop_reason": "end_turn", "stop_sequence": null },
          "usage": { "output_tokens": 567 }
        }
        """;

    private const string MessageStopJson = """
        { "type": "message_stop" }
        """;

    private static RawMessageStreamEvent Parse(string json) =>
        JsonSerializer.Deserialize<RawMessageStreamEvent>(json)!;

    private static async IAsyncEnumerable<RawMessageStreamEvent> EventsFrom(
        IEnumerable<string> jsonEvents,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var json in jsonEvents)
        {
            ct.ThrowIfCancellationRequested();
            yield return Parse(json);
            await Task.Yield();
        }
    }

    [Fact]
    public async Task ConvertStream_MessageStartCarriesInputTokens_UsageDetailsHasCorrectPromptCompletionSplit()
    {
        var stream = EventsFrom(
        [
            MessageStartJson,
            ContentBlockStartJson,
            ContentBlockDeltaJson,
            ContentBlockStopJson,
            MessageDeltaJson,
            MessageStopJson
        ]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in AnthropicStreamingConverter.ConvertStreamAsync(stream, Metadata))
        {
            updates.Add(update);
        }

        var usage = updates
            .SelectMany(u => u.Contents)
            .OfType<UsageContent>()
            .Should().ContainSingle().Subject.Details;

        usage.InputTokenCount.Should().Be(1234);
        usage.OutputTokenCount.Should().Be(567);
        usage.TotalTokenCount.Should().Be(1801);
    }

    [Fact]
    public async Task ConvertStream_MessageStartCarriesInputTokens_InputTokenCountIsNotZero()
    {
        var stream = EventsFrom([MessageStartJson, MessageDeltaJson, MessageStopJson]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in AnthropicStreamingConverter.ConvertStreamAsync(stream, Metadata))
        {
            updates.Add(update);
        }

        var usage = updates
            .SelectMany(u => u.Contents)
            .OfType<UsageContent>()
            .Should().ContainSingle().Subject.Details;

        usage.InputTokenCount.Should().NotBe(0);
        usage.InputTokenCount.Should().Be(1234);
    }
}
