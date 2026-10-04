using System.Text.Json;
using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

internal static class AnthropicContentEnvelope
{
    public static JsonElement Create(JsonElement block, AnthropicExecutionContext context, string messageId, int index) =>
        JsonSerializer.SerializeToElement(new
        {
            version = 1, provider = context.Provider, scope = context.Scope, messageId,
            blockIndex = index, role = "assistant", complete = true, block = block.Clone()
        });

    public static JsonElement AsJson(object? value) => value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);

    public static ContentBlockParam Replay(JsonElement envelope, AnthropicExecutionContext context, ChatRole role,
        ref int previousIndex, ref string? messageId)
    {
        if (role != ChatRole.Assistant || envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
            String(envelope, "provider") != context.Provider || String(envelope, "scope") != context.Scope ||
            String(envelope, "role") != "assistant" ||
            !envelope.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            !envelope.TryGetProperty("blockIndex", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var i) || i <= previousIndex ||
            string.IsNullOrWhiteSpace(String(envelope, "messageId")) ||
            !envelope.TryGetProperty("block", out var block) || block.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(String(block, "type")))
            throw new ArgumentException("Invalid or incompatible anthropic_raw_content_block envelope.");
        var id = String(envelope, "messageId")!;
        if (messageId is not null && id != messageId) throw new ArgumentException("Replay blocks belong to different messages.");
        messageId = id;
        previousIndex = i;
        return new ContentBlockParam(block.Clone());
    }

    internal static string? String(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
