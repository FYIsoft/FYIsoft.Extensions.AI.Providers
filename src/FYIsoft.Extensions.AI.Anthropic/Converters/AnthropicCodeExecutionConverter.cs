using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using M = Microsoft.Extensions.AI.Anthropic.AnthropicCodeExecutionMetadata;
using static Microsoft.Extensions.AI.Anthropic.AnthropicContentEnvelope;

namespace Microsoft.Extensions.AI.Anthropic;

internal static class AnthropicCodeExecutionConverter
{
    internal static bool IsExecutionOperation(string? operation) =>
        operation is "code_execution" or "bash_code_execution" or "text_editor_code_execution";

    public static AIContent Project(JsonElement block, AnthropicExecutionContext context, string messageId, int index)
    {
        AIContent content;
        var type = String(block, "type");
        if (type == "server_tool_use" && IsExecutionOperation(String(block, "name")))
        {
            var operation = String(block, "name")!;
            var input = block.GetProperty("input");
            if (input.ValueKind != JsonValueKind.Object) throw new JsonException("Execution input must be an object.");
            var call = new CodeInterpreterToolCallContent(block.GetProperty("id").GetString()!)
            {
                Inputs = [], AdditionalProperties = new() { [M.Operation] = operation, [M.Input] = input.Clone() }
            };
            var code = String(input, operation == "code_execution" ? "code" : "command");
            if (code is not null && operation != "text_editor_code_execution")
                call.Inputs.Add(new DataContent(Encoding.UTF8.GetBytes(code), operation == "code_execution" ? "text/x-python" : "text/x-shellscript"));
            else call.Inputs.Add(new DataContent(Encoding.UTF8.GetBytes(input.GetRawText()), "application/json"));
            content = call;
        }
        else if (type is "code_execution_tool_result" or "bash_code_execution_tool_result" or "text_editor_code_execution_tool_result")
        {
            var result = new CodeInterpreterToolResultContent(block.GetProperty("tool_use_id").GetString()!)
            {
                Outputs = [], AdditionalProperties = new() { [M.Outcome] = "unknown" }
            };
            if (block.TryGetProperty("content", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                var error = String(body, "error_code");
                if (error is not null)
                {
                    result.AdditionalProperties[M.ErrorCode] = error;
                    result.AdditionalProperties[M.Outcome] = "failed";
                }
                else if (body.TryGetProperty("return_code", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.TryGetInt32(out var code))
                {
                    result.AdditionalProperties[M.ReturnCode] = code;
                    result.AdditionalProperties[M.Outcome] = code == 0 ? "succeeded" : "failed";
                }
                else if (String(body, "type") is "text_editor_code_execution_view_result" or
                    "text_editor_code_execution_create_result" or "text_editor_code_execution_str_replace_result")
                    result.AdditionalProperties[M.Outcome] = "succeeded";
                foreach (var channel in new[] { "stdout", "stderr" })
                {
                    if (String(body, channel) is not { } text) continue;
                    result.AdditionalProperties[channel == "stdout" ? M.Stdout : M.Stderr] = text;
                    result.Outputs.Add(new DataContent(Encoding.UTF8.GetBytes(text), "text/plain")
                    {
                        Name = channel, AdditionalProperties = new() { [M.OutputChannel] = channel }
                    });
                }
                if (String(body, "type") == "text_editor_code_execution_view_result" && String(body, "content") is { } view)
                    result.Outputs.Add(new TextContent(view));
                if (body.TryGetProperty("content", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var file in files.EnumerateArray())
                        if (String(file, "file_id") is { Length: > 0 }) result.Outputs.Add(ProjectFile(file, context));
            }
            content = result;
        }
        else if (type == "container_upload") content = ProjectFile(block, context);
        else content = AnthropicContentConverter.FromAnthropicContent(new ContentBlock(block)) ?? new AIContent();
        content.AdditionalProperties ??= new();
        content.AdditionalProperties[M.RawContentBlock] = Create(block, context, messageId, index);
        content.AdditionalProperties[M.ProviderScope] = context.Scope;
        content.RawRepresentation = block.Clone();
        return content;
    }

    private static HostedFileContent ProjectFile(JsonElement file, AnthropicExecutionContext context) =>
        new(file.GetProperty("file_id").GetString()!)
        {
            Name = String(file, "filename") ?? String(file, "name"), MediaType = String(file, "mime_type"),
            AdditionalProperties = new() { [M.ProviderScope] = context.Scope, [M.Provider] = context.Provider }
        };

    public static AdditionalPropertiesDictionary Metadata(AnthropicExecutionContext context, IEnumerable<JsonElement> envelopes,
        string? stopReason, bool transportComplete, JsonElement? container, bool valid = true)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            [M.ProviderScope] = context.Scope,
            [M.ReplayBlocks] = JsonSerializer.SerializeToElement(envelopes),
            [context.Enabled ? M.ExecutionState : AnthropicProviderMetadata.ResponseState] = !transportComplete || !valid || stopReason == "max_tokens" ? "incomplete" :
                stopReason == "pause_turn" ? "paused" : stopReason is "end_turn" or "stop_sequence" or "tool_use" ? "complete" : "failed"
        };
        if (stopReason is not null) properties[M.StopReason] = stopReason;
        if (container is { ValueKind: JsonValueKind.Object } c)
        {
            if (String(c, "id") is { } id) properties[M.ContainerId] = id;
            if (String(c, "expires_at") is { } expiry) properties[M.ContainerExpiresAt] = expiry;
        }
        return properties;
    }

    public static ChatFinishReason? FinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        "pause_turn" => new ChatFinishReason("pause_turn"),
        _ => null
    };
}
