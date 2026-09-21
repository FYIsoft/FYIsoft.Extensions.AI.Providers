using System.Runtime.CompilerServices;
using System.Text;
using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Converts Anthropic streaming events to Microsoft.Extensions.AI ChatResponseUpdate objects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>State Machine for Event Aggregation</strong>:
/// Anthropic's streaming API emits fine-grained events that must be aggregated:
/// <list type="table">
/// <listheader>
/// <term>Event Type</term>
/// <description>Action</description>
/// </listheader>
/// <item>
/// <term>message_start</term>
/// <description>Initialize response metadata (ID, model, role)</description>
/// </item>
/// <item>
/// <term>content_block_start</term>
/// <description>Begin new content block (text or tool_use)</description>
/// </item>
/// <item>
/// <term>content_block_delta</term>
/// <description>Accumulate text deltas or tool input</description>
/// </item>
/// <item>
/// <term>content_block_stop</term>
/// <description>Finalize content block and yield update</description>
/// </item>
/// <item>
/// <term>message_delta</term>
/// <description>Update stop reason and usage</description>
/// </item>
/// <item>
/// <term>message_stop</term>
/// <description>Yield final update with complete usage</description>
/// </item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Content Accumulation</strong>:
/// Text content is accumulated in a StringBuilder for efficiency.
/// Tool calls are tracked across multiple events.
/// Each content block yields a separate ChatResponseUpdate when complete.
/// </para>
/// </remarks>
internal static class AnthropicStreamingConverter
{
    /// <summary>
    /// Converts an Anthropic streaming event sequence to ChatResponseUpdate objects.
    /// </summary>
    /// <param name="streamingEvents">The streaming events from Anthropic.</param>
    /// <param name="metadata">The chat client metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of ChatResponseUpdate objects.</returns>
    public static async IAsyncEnumerable<ChatResponseUpdate> ConvertStreamAsync(
        IAsyncEnumerable<RawMessageStreamEvent> streamingEvents,
        ChatClientMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new StreamingState();

        await foreach (var streamEvent in streamingEvents.WithCancellation(cancellationToken))
        {
            var updates = ProcessEvent(streamEvent, state, metadata);

            foreach (var update in updates)
            {
                yield return update;
            }
        }

        // Yield final update if we have accumulated content
        if (state.HasPendingContent)
        {
            yield return CreateFinalUpdate(state, metadata);
        }
    }

    /// <summary>
    /// Processes a single streaming event and returns any ChatResponseUpdate objects to yield.
    /// </summary>
    private static IEnumerable<ChatResponseUpdate> ProcessEvent(
        RawMessageStreamEvent streamEvent,
        StreamingState state,
        ChatClientMetadata metadata)
    {
        var updates = new List<ChatResponseUpdate>();

        // Use TryPick methods for union type handling
        if (streamEvent.TryPickStart(out var messageStart))
        {
            HandleMessageStart(messageStart, state);
        }
        else if (streamEvent.TryPickContentBlockStart(out var blockStart))
        {
            HandleContentBlockStart(blockStart, state);
        }
        else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
        {
            var update = HandleContentBlockDelta(blockDelta, state, metadata);
            if (update is not null)
            {
                updates.Add(update);
            }
        }
        else if (streamEvent.TryPickContentBlockStop(out var blockStop))
        {
            var stopUpdate = HandleContentBlockStop(blockStop, state, metadata);
            if (stopUpdate is not null)
            {
                updates.Add(stopUpdate);
            }
        }
        else if (streamEvent.TryPickDelta(out var messageDelta))
        {
            HandleMessageDelta(messageDelta, state);
        }
        else if (streamEvent.TryPickStop(out var messageStop))
        {
            var finalUpdate = HandleMessageStop(messageStop, state, metadata);
            if (finalUpdate is not null)
            {
                updates.Add(finalUpdate);
            }
        }
        else
        {
            // Unknown event type - log but don't fail
            System.Diagnostics.Debug.WriteLine(
                $"Unknown streaming event type: {streamEvent.GetType().Name}");
        }

        return updates;
    }

    private static void HandleMessageStart(RawMessageStartEvent messageStart, StreamingState state)
    {
        state.MessageId = messageStart.Message.ID;
        state.Model = messageStart.Message.Model.ToString();

        // Anthropic reports input_tokens on message_start (output_tokens later arrive on message_delta).
        if (messageStart.Message.Usage is not null)
        {
            state.InputTokens = (int)messageStart.Message.Usage.InputTokens;
        }

        // Note: Role is always "assistant" for response messages
    }

    private static void HandleContentBlockStart(RawContentBlockStartEvent blockStart, StreamingState state)
    {
        // Start a new content block
        state.CurrentBlockIndex = (int)blockStart.Index;

        if (blockStart.ContentBlock.TryPickText(out _))
        {
            state.CurrentBlockType = ContentBlockType.Text;
            state.TextAccumulator = new StringBuilder();
        }
        else if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
        {
            state.CurrentBlockType = ContentBlockType.ToolUse;
            state.CurrentToolId = toolUse.ID;
            state.CurrentToolName = toolUse.Name;
            state.ToolInputAccumulator = new StringBuilder();
        }
    }

    private static ChatResponseUpdate? HandleContentBlockDelta(
        RawContentBlockDeltaEvent blockDelta,
        StreamingState state,
        ChatClientMetadata metadata)
    {
        if (blockDelta.Delta.TryPickText(out var textDelta))
        {
            if (state.CurrentBlockType == ContentBlockType.Text && state.TextAccumulator is not null)
            {
                state.TextAccumulator.Append(textDelta.Text);

                // Yield incremental text update
                return new ChatResponseUpdate
                {
                    Contents = [new TextContent(textDelta.Text)],
                    Role = ChatRole.Assistant,
                    ModelId = state.Model,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["anthropic_message_id"] = state.MessageId,
                        ["content_block_index"] = state.CurrentBlockIndex
                    }
                };
            }
        }
        else if (blockDelta.Delta.TryPickInputJson(out var inputDelta))
        {
            if (state.CurrentBlockType == ContentBlockType.ToolUse && state.ToolInputAccumulator is not null)
            {
                state.ToolInputAccumulator.Append(inputDelta.PartialJson);
                // Don't yield until tool use is complete
            }
        }

        return null;
    }

    private static ChatResponseUpdate? HandleContentBlockStop(
        RawContentBlockStopEvent blockStop,
        StreamingState state,
        ChatClientMetadata metadata)
    {
        ChatResponseUpdate? update = null;

        if (state.CurrentBlockType == ContentBlockType.ToolUse &&
            !string.IsNullOrEmpty(state.CurrentToolId) &&
            !string.IsNullOrEmpty(state.CurrentToolName))
        {
            // Parse accumulated tool input JSON
            var toolInput = ParseToolInput(state.ToolInputAccumulator?.ToString());

            var functionCall = new FunctionCallContent(
                callId: state.CurrentToolId,
                name: state.CurrentToolName,
                arguments: toolInput);

            update = new ChatResponseUpdate
            {
                Contents = [functionCall],
                Role = ChatRole.Assistant,
                ModelId = state.Model,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["anthropic_message_id"] = state.MessageId,
                    ["content_block_index"] = state.CurrentBlockIndex
                }
            };
        }

        // Reset current block state
        state.CurrentBlockType = ContentBlockType.None;
        state.TextAccumulator = null;
        state.ToolInputAccumulator = null;
        state.CurrentToolId = null;
        state.CurrentToolName = null;

        return update;
    }

    private static void HandleMessageDelta(RawMessageDeltaEvent messageDelta, StreamingState state)
    {
        // Update stop reason
        state.StopReason = messageDelta.Delta?.StopReason?.Raw();

        // Update usage if provided
        if (messageDelta.Usage is not null)
        {
            state.OutputTokens = (int)messageDelta.Usage.OutputTokens;
        }
    }

    private static ChatResponseUpdate? HandleMessageStop(
        RawMessageStopEvent messageStop,
        StreamingState state,
        ChatClientMetadata metadata)
    {
        // Create final update with usage information
        var contents = new List<AIContent>();

        if (state.InputTokens > 0 || state.OutputTokens > 0)
        {
            var usageDetails = new UsageDetails
            {
                InputTokenCount = state.InputTokens,
                OutputTokenCount = state.OutputTokens,
                TotalTokenCount = state.InputTokens + state.OutputTokens
            };
            contents.Add(new UsageContent(usageDetails));
        }

        var finishReason = state.StopReason switch
        {
            "end_turn" => ChatFinishReason.Stop,
            "max_tokens" => ChatFinishReason.Length,
            "stop_sequence" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            _ => (ChatFinishReason?)null
        };

        return new ChatResponseUpdate
        {
            Contents = contents,
            Role = ChatRole.Assistant,
            ModelId = state.Model,
            FinishReason = finishReason,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["anthropic_message_id"] = state.MessageId,
                ["anthropic_stop_reason"] = state.StopReason,
                ["complete"] = true
            }
        };
    }

    private static ChatResponseUpdate CreateFinalUpdate(StreamingState state, ChatClientMetadata metadata)
    {
        var contents = new List<AIContent>();

        // Add any remaining text
        if (state.TextAccumulator?.Length > 0)
        {
            contents.Add(new TextContent(state.TextAccumulator.ToString()));
        }

        // Add usage
        if (state.InputTokens > 0 || state.OutputTokens > 0)
        {
            var usageDetails = new UsageDetails
            {
                InputTokenCount = state.InputTokens,
                OutputTokenCount = state.OutputTokens,
                TotalTokenCount = state.InputTokens + state.OutputTokens
            };
            contents.Add(new UsageContent(usageDetails));
        }

        var finishReason = state.StopReason switch
        {
            "end_turn" => ChatFinishReason.Stop,
            "max_tokens" => ChatFinishReason.Length,
            "stop_sequence" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            _ => (ChatFinishReason?)null
        };

        return new ChatResponseUpdate
        {
            Contents = contents,
            Role = ChatRole.Assistant,
            ModelId = state.Model,
            FinishReason = finishReason
        };
    }

    /// <summary>
    /// Parses tool input JSON into a dictionary.
    /// </summary>
    private static IDictionary<string, object?>? ParseToolInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch
        {
            // If parsing fails, return empty dictionary
            System.Diagnostics.Debug.WriteLine($"Failed to parse tool input JSON: {json}");
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// State machine for tracking streaming event processing.
    /// </summary>
    private class StreamingState
    {
        public string? MessageId { get; set; }
        public string? Model { get; set; }
        public Role Role { get; set; }
        public string? StopReason { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        public int CurrentBlockIndex { get; set; }
        public ContentBlockType CurrentBlockType { get; set; }

        // Text content accumulation
        public StringBuilder? TextAccumulator { get; set; }

        // Tool use accumulation
        public string? CurrentToolId { get; set; }
        public string? CurrentToolName { get; set; }
        public StringBuilder? ToolInputAccumulator { get; set; }

        public bool HasPendingContent =>
            (TextAccumulator?.Length ?? 0) > 0 ||
            !string.IsNullOrEmpty(CurrentToolId);
    }

    private enum ContentBlockType
    {
        None,
        Text,
        ToolUse
    }
}
