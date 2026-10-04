using System.Text.Json;
using Anthropic.Models.Messages;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>Immutable connection identity and request-local execution policy.</summary>
internal sealed record AnthropicExecutionContext(string Provider, string Scope, bool Enabled, bool ExplicitScope, string? ContainerId)
{
    public bool DisableRetries { get; init; }

    internal static bool HasBlock(ChatMessage message, Func<JsonElement, bool> predicate)
    {
        if (message.AdditionalProperties?.TryGetValue(M.ReplayBlocks, out var value) != true) return false;
        var envelopes = AnthropicContentEnvelope.AsJson(value);
        return envelopes.ValueKind == JsonValueKind.Array && envelopes.EnumerateArray().Any(e =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty("block", out var block) && block.ValueKind == JsonValueKind.Object && predicate(block));
    }
    public static (AnthropicExecutionContext Context, List<ChatMessage> Messages) Prepare(
        IEnumerable<ChatMessage> messages, ChatOptions? options, string provider, string defaultScope)
    {
        var snapshot = messages.Select(m => new ChatMessage(m.Role, m.Contents.ToList())
        {
            MessageId = m.MessageId,
            AdditionalProperties = m.AdditionalProperties is null ? null : new(m.AdditionalProperties)
        }).ToList();
        var hosted = options?.Tools?.OfType<HostedCodeInterpreterTool>().ToList() ?? [];
        if (hosted.Count > 1) throw new ArgumentException("Only one HostedCodeInterpreterTool is supported.", nameof(options));
        var scope = ReadString(options?.AdditionalProperties, M.ProviderScope);
        var container = ReadString(options?.AdditionalProperties, M.ContainerId);
        if (container is not null && scope is null)
            throw new ArgumentException("anthropic_container_id requires anthropic_provider_scope.", nameof(options));
        var history = snapshot.Any(m => m.AdditionalProperties?.ContainsKey(M.ExecutionState) == true ||
            m.Contents.Any(c => c is CodeInterpreterToolCallContent or CodeInterpreterToolResultContent or HostedFileContent) ||
            HasBlock(m, b => AnthropicContentEnvelope.String(b, "type") is "container_upload" or "code_execution_tool_result" or "bash_code_execution_tool_result" or "text_editor_code_execution_tool_result" ||
                (AnthropicContentEnvelope.String(b, "type") == "server_tool_use" && AnthropicCodeExecutionConverter.IsExecutionOperation(AnthropicContentEnvelope.String(b, "name")))));
        var enabled = hosted.Count != 0 || container is not null || history;
        var hosting = ReadString(options?.AdditionalProperties, M.Hosting);
        if (hosting is not null and not ("direct" or "foundry-anthropic" or "foundry-azure"))
            throw new ArgumentException("Invalid anthropic_hosting.", nameof(options));
        if (enabled && hosting == "foundry-azure")
            throw new NotSupportedException("Hosted execution is not supported for the declared foundry-azure hosting arrangement.");
        var context = new AnthropicExecutionContext(provider, scope ?? defaultScope, enabled, scope is not null, container)
        {
            DisableRetries = enabled || options?.Tools?.OfType<HostedMcpServerTool>().Any() == true ||
                snapshot.Any(m => m.Contents.Any(c => c is McpServerToolCallContent or McpServerToolResultContent) ||
                    HasBlock(m, b => AnthropicContentEnvelope.String(b, "type") is "mcp_tool_use" or "mcp_tool_result"))
        };
        if (hosted.SingleOrDefault()?.Inputs is { Count: > 0 } inputs)
        {
            var target = snapshot.LastOrDefault(m => m.Role == ChatRole.User)
                ?? throw new ArgumentException("Hosted tool inputs require a user message.", nameof(messages));
            var ids = target.Contents.OfType<HostedFileContent>().Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                if (input is not HostedFileContent file)
                    throw new NotSupportedException("Hosted tool inputs must be uploaded HostedFileContent references.");
                context.ValidateFile(file);
                if (ids.Add(file.FileId)) target.Contents.Add(file);
            }
        }
        // Validate even content hidden by authoritative message-level replay.
        foreach (var message in snapshot)
        {
            if (message.Role == ChatRole.System && (message.AdditionalProperties?.ContainsKey(M.ReplayBlocks) == true ||
                message.Contents.Any(c => c is CodeInterpreterToolCallContent or CodeInterpreterToolResultContent ||
                    c.AdditionalProperties?.ContainsKey(M.RawContentBlock) == true)))
                throw new ArgumentException("Execution history must appear in assistant messages.", nameof(messages));
            foreach (var file in message.Contents.OfType<HostedFileContent>())
            {
                if (message.Role != ChatRole.User && file.AdditionalProperties?.ContainsKey(M.RawContentBlock) != true)
                    throw new ArgumentException("Uploaded files must appear in user messages.", nameof(messages));
                context.ValidateFile(file);
            }
        }
        return (context, snapshot);
    }

    public void ValidateFile(HostedFileContent file)
    {
        if (string.IsNullOrWhiteSpace(file.FileId)) throw new ArgumentException("HostedFileContent requires a file ID.");
        var scope = ReadString(file.AdditionalProperties, M.ProviderScope);
        if (scope is null ? !ExplicitScope : scope != Scope)
            throw new ArgumentException("HostedFileContent has a missing or mismatched anthropic_provider_scope.");
        var provider = ReadString(file.AdditionalProperties, M.Provider);
        if (provider is not null && provider != Provider)
            throw new ArgumentException("HostedFileContent belongs to a different provider.");
    }

    public List<ContentBlockParam> ConvertMessage(ChatMessage message)
    {
        var state = ReadString(message.AdditionalProperties, M.ExecutionState) ??
            ReadString(message.AdditionalProperties, AnthropicProviderMetadata.ResponseState);
        if (state is not null and not ("complete" or "paused"))
            throw new ArgumentException("Incomplete execution history cannot be replayed.");
        var scope = ReadString(message.AdditionalProperties, M.ProviderScope);
        if (scope is not null && scope != Scope) throw new ArgumentException("Message provider scope does not match the request.");
        if (message.AdditionalProperties?.TryGetValue(M.ReplayBlocks, out var replay) == true)
        {
            if (state is null || scope is null) throw new ArgumentException("Replay history requires execution state and scope.");
            var array = AnthropicContentEnvelope.AsJson(replay);
            if (array.ValueKind != JsonValueKind.Array) throw new ArgumentException("Invalid anthropic_replay_blocks.");
            var blocks = new List<ContentBlockParam>();
            var previous = -1;
            string? messageId = null;
            foreach (var envelope in array.EnumerateArray())
                blocks.Add(AnthropicContentEnvelope.Replay(envelope, this, message.Role, ref previous, ref messageId));
            return blocks;
        }
        var result = new List<ContentBlockParam>();
        var index = -1;
        string? id = null;
        foreach (var content in message.Contents)
        {
            if (content.AdditionalProperties?.TryGetValue(M.RawContentBlock, out var envelope) == true)
                result.Add(AnthropicContentEnvelope.Replay(AnthropicContentEnvelope.AsJson(envelope), this, message.Role, ref index, ref id));
            else if (content is HostedFileContent file)
            {
                ValidateFile(file);
                result.Add(new ContentBlockParam(JsonSerializer.SerializeToElement(new { type = "container_upload", file_id = file.FileId })));
            }
            else if (content is CodeInterpreterToolCallContent or CodeInterpreterToolResultContent)
                throw new ArgumentException("Execution history requires a complete provider envelope.");
            else result.AddRange(AnthropicContentConverter.ToAnthropicContent([content])
                .Select(block => AnthropicProviderFeatures.Decorate(block, content)));
        }
        return result;
    }

    internal static string? ReadString(AdditionalPropertiesDictionary? properties, string key)
    {
        if (properties?.TryGetValue(key, out var value) != true) return null;
        var text = value is string s ? s : value is JsonElement { ValueKind: JsonValueKind.String } j ? j.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{key} must be a nonempty string.", key);
        return text;
    }
}
