using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;
using static Microsoft.Extensions.AI.Anthropic.AnthropicContentEnvelope;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>Assembles each indexed provider block independently and retains durable replay metadata.</summary>
internal static class AnthropicStreamingConverter
{
    public static async IAsyncEnumerable<ChatResponseUpdate> ConvertStreamAsync(
        IAsyncEnumerable<RawMessageStreamEvent> streamingEvents,
        ChatClientMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        AnthropicExecutionContext? execution = null,
        ILogger? logger = null)
    {
        var blocks = new SortedDictionary<int, Block>();
        string? id = null, model = null, stopReason = null;
        JsonElement? container = null;
        var usageData = new JsonObject();
        void MergeUsage(JsonElement usage)
        {
            foreach (var field in usage.EnumerateObject()) usageData[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        }
        bool valid = true;
        ChatResponseUpdate Update() => new()
        {
            MessageId = id, ResponseId = id, Role = ChatRole.Assistant, ModelId = model,
            AdditionalProperties = new() { ["anthropic_message_id"] = id }
        };
        ChatResponseUpdate Terminal(bool stopped)
        {
            var update = Update();
            var complete = stopped && valid && blocks.Values.All(b => b.Complete);
            if (execution is not null && (execution.Enabled || blocks.Values.Any(b => AnthropicProviderFeatures.Rich(b.Json()))))
                update.AdditionalProperties = AnthropicCodeExecutionConverter.Metadata(execution,
                    blocks.Where(b => b.Value.Complete).Select(b => Create(b.Value.Json(), execution, id!, b.Key)),
                    stopReason, stopped, container, complete);
            update.AdditionalProperties!["complete"] = complete;
            if (stopReason is not null) update.AdditionalProperties[M.StopReason] = stopReason;
            // EOF is never a successful finish, even if a message_delta arrived first.
            update.FinishReason = complete ? AnthropicCodeExecutionConverter.FinishReason(stopReason) :
                stopReason == "max_tokens" ? ChatFinishReason.Length : null;
            update.Contents.Add(new UsageContent(AnthropicProviderFeatures.Usage(JsonSerializer.SerializeToElement(usageData))));
            return update;
        }

        await foreach (var evt in streamingEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = evt.Json;
            switch (String(json, "type"))
            {
                case "message_start":
                    if (id is not null) throw new JsonException("Duplicate message_start.");
                    var message = json.GetProperty("message");
                    id = message.GetProperty("id").GetString();
                    model = String(message, "model");
                    if (message.TryGetProperty("container", out var initialContainer)) container = initialContainer.Clone();
                    if (message.TryGetProperty("usage", out var startUsage))
                    {
                        MergeUsage(startUsage);
                    }
                    break;
                case "content_block_start":
                    var index = json.GetProperty("index").GetInt32();
                    if (id is null || index < 0 || blocks.ContainsKey(index)) throw new JsonException("Invalid content block index or message order.");
                    var block = new Block(json.GetProperty("content_block"));
                    blocks.Add(index, block);
                    if (block.Type == "text" && String(block.Json(), "text") is { Length: > 0 } initialText)
                    {
                        var update = Update();
                        update.Contents.Add(new TextContent(initialText));
                        yield return update;
                    }
                    if (block.Type == "thinking" && String(block.Json(), "thinking") is { Length: > 0 } initialThinking)
                    {
                        var update = Update();
                        update.Contents.Add(new TextReasoningContent(initialThinking));
                        update.AdditionalProperties!["content_block_index"] = index;
                        yield return update;
                    }
                    break;
                case "content_block_delta":
                    var deltaIndex = json.GetProperty("index").GetInt32();
                    if (!blocks.TryGetValue(deltaIndex, out var target) || target.Stopped)
                        throw new JsonException("Delta references a missing or stopped content block.");
                    var delta = json.GetProperty("delta");
                    if (!target.Apply(delta))
                    {
                        valid = false;
                        var diagnostic = Update();
                        diagnostic.Contents.Add(new AIContent { RawRepresentation = json.Clone(),
                            AdditionalProperties = new() { ["anthropic_raw_stream_event"] = json.Clone() } });
                        yield return diagnostic;
                    }
                    else if (String(delta, "type") == "text_delta")
                    {
                        var update = Update();
                        update.Contents.Add(new TextContent(delta.GetProperty("text").GetString()!));
                        update.AdditionalProperties!["content_block_index"] = deltaIndex;
                        yield return update;
                    }
                    else if (String(delta, "type") == "thinking_delta")
                    {
                        var update = Update();
                        update.Contents.Add(new TextReasoningContent(delta.GetProperty("thinking").GetString()!));
                        update.AdditionalProperties!["content_block_index"] = deltaIndex;
                        yield return update;
                    }
                    else if (String(delta, "type") == "input_json_delta" && target.Type == "server_tool_use" && AnthropicCodeExecutionConverter.IsExecutionOperation(target.Operation))
                    {
                        var update = Update();
                        update.AdditionalProperties![M.ExecutionProgress] = JsonSerializer.SerializeToElement(new
                        {
                            messageId = id, blockIndex = deltaIndex, callId = target.Id, operation = target.Operation,
                            partialJson = delta.GetProperty("partial_json").GetString(), state = "incomplete"
                        });
                        yield return update;
                    }
                    break;
                case "content_block_stop":
                    var stopIndex = json.GetProperty("index").GetInt32();
                    if (!blocks.TryGetValue(stopIndex, out var finished) || finished.Stopped)
                        throw new JsonException("Stop references a missing or stopped content block.");
                    finished.Finish();
                    if (!finished.Complete)
                    {
                        valid = false;
                        // The block is not emitted and the response is marked incomplete; say why.
                        logger?.LogWarning(
                            "Anthropic stream content block {BlockIndex} ({BlockType} {ToolName}, id {BlockId}) in message {MessageId} is invalid and was not emitted: {Reason}. The response is marked incomplete.",
                            stopIndex, finished.Type, finished.Operation, finished.Id, id, finished.Error ?? "unsupported delta");
                    }
                    if (finished.Complete)
                    {
                        var converted = execution is not null && (execution.Enabled || blocks.Values.Any(b => AnthropicProviderFeatures.Rich(b.Json())))
                            ? AnthropicCodeExecutionConverter.Project(finished.Json(), execution, id!, stopIndex)
                            : AnthropicContentConverter.FromAnthropicContent(new ContentBlock(finished.Json()));
                        // Text/thinking have already streamed. Emit only annotations or protected data here.
                        if (converted is TextContent text)
                        {
                            if (text.Annotations is not { Count: > 0 }) break;
                            text.Text = "";
                        }
                        if (converted is TextReasoningContent thinking) thinking.Text = "";
                        if (converted is not null)
                        {
                            var update = Update();
                            update.Contents.Add(converted);
                            update.AdditionalProperties!["content_block_index"] = stopIndex;
                            yield return update;
                        }
                    }
                    break;
                case "message_delta":
                    var messageDelta = json.GetProperty("delta");
                    stopReason = String(messageDelta, "stop_reason") ?? stopReason;
                    if (messageDelta.TryGetProperty("container", out var updatedContainer)) container = updatedContainer.Clone();
                    if (json.TryGetProperty("usage", out var usage))
                    {
                        MergeUsage(usage);
                    }
                    break;
                case "message_stop":
                    if (id is null) throw new JsonException("message_stop preceded message_start.");
                    yield return Terminal(true);
                    yield break;
                case "ping":
                    break;
                default:
                    valid = false;
                    var unknown = Update();
                    unknown.Contents.Add(new AIContent { RawRepresentation = json.Clone(),
                        AdditionalProperties = new() { ["anthropic_raw_stream_event"] = json.Clone() } });
                    yield return unknown;
                    break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return Terminal(false);
    }

    private sealed class Block
    {
        private readonly JsonObject _body;
        private readonly Dictionary<string, StringBuilder> _strings = new();
        private readonly StringBuilder _input = new();
        private bool _hasInputDelta, _valid = true;
        private bool IsToolUse => Type is "tool_use" or "server_tool_use" or "mcp_tool_use";
        public Block(JsonElement json) => _body = JsonNode.Parse(json.GetRawText())!.AsObject();
        public string? Type => _body["type"]?.GetValue<string>();
        public string? Id => _body["id"]?.GetValue<string>();
        public string? Operation => _body["name"]?.GetValue<string>();
        public bool Stopped { get; private set; }
        public bool Complete { get; private set; }
        /// <summary>Why the block is invalid, when known. Never contains the tool input itself.</summary>
        public string? Error { get; private set; }
        public JsonElement Json() => JsonSerializer.SerializeToElement(_body);
        public bool Apply(JsonElement delta)
        {
            var type = String(delta, "type");
            string? key = type switch
            {
                "text_delta" when Type == "text" => "text",
                "thinking_delta" when Type == "thinking" => "thinking",
                "signature_delta" when Type == "thinking" => "signature",
                _ => null
            };
            if (key is not null && String(delta, key) is { } value)
            {
                if (!_strings.TryGetValue(key, out var builder))
                    _strings[key] = builder = new StringBuilder(_body[key]?.GetValue<string>() ?? "");
                builder.Append(value);
                return true;
            }
            if (type == "input_json_delta" && Type is "tool_use" or "server_tool_use" or "mcp_tool_use" && String(delta, "partial_json") is { } fragment)
            {
                _hasInputDelta = true;
                _input.Append(fragment);
                return true;
            }
            if (type == "citations_delta" && Type == "text" && delta.TryGetProperty("citation", out var citation))
            {
                if (_body["citations"] is not JsonArray) _body["citations"] = new JsonArray();
                _body["citations"]!.AsArray().Add(JsonNode.Parse(citation.GetRawText()));
                return true;
            }
            _valid = false;
            return false;
        }
        public void Finish()
        {
            Stopped = true;
            foreach (var (key, builder) in _strings) _body[key] = builder.ToString();
            // A call to a tool with no parameters streams no input fragments, or only empty ones
            // ("partial_json": ""). Empty input means no arguments: keep the input from
            // content_block_start (normally {}) instead of parsing an empty string.
            var buffered = _input.ToString();
            if (_hasInputDelta && !string.IsNullOrWhiteSpace(buffered))
            {
                try { _body["input"] = JsonNode.Parse(buffered); }
                catch (JsonException ex)
                {
                    _valid = false;
                    Error = $"tool input is not valid JSON ({buffered.Length} characters buffered): {ex.Message}";
                }
            }
            if (IsToolUse && _valid && _body["input"] is null) _body["input"] = new JsonObject();
            if (IsToolUse && _valid && _body["input"] is not JsonObject)
            {
                _valid = false;
                Error = $"tool input is a JSON {_body["input"]!.GetValueKind()}, not an object";
            }
            Complete = _valid;
        }
    }
}
