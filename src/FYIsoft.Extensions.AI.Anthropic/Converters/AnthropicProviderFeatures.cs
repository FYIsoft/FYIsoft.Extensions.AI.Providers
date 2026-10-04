using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using P = Microsoft.Extensions.AI.Anthropic.AnthropicProviderMetadata;
using static Microsoft.Extensions.AI.Anthropic.AnthropicContentEnvelope;

namespace Microsoft.Extensions.AI.Anthropic;

internal static class AnthropicProviderFeatures
{
    internal static void Cache(JsonObject target, IEnumerable<KeyValuePair<string, object?>>? properties)
    {
        var item = properties?.FirstOrDefault(p => p.Key == P.CacheControl);
        if (item?.Key is null) return;
        var cache = AsJson(item.Value.Value);
        if (cache.ValueKind != JsonValueKind.Object || String(cache, "type") != "ephemeral" ||
            (cache.TryGetProperty("ttl", out var ttl) && (ttl.ValueKind != JsonValueKind.String || ttl.GetString() is not ("5m" or "1h"))) ||
            cache.EnumerateObject().Any(p => p.Name is not ("type" or "ttl")))
            throw new ArgumentException("anthropic_cache_control must specify type ephemeral and an optional ttl of 5m or 1h.");
        if (target["type"]?.GetValue<string>() is "thinking" or "redacted_thinking")
            throw new ArgumentException("Thinking blocks cannot have explicit cache breakpoints.");
        target["cache_control"] = JsonNode.Parse(cache.GetRawText());
    }

    internal static ContentBlockParam Decorate(ContentBlockParam block, AIContent content)
    {
        var node = JsonNode.Parse(block.Json.GetRawText())!.AsObject();
        Cache(node, content.AdditionalProperties);
        return new ContentBlockParam(JsonSerializer.SerializeToElement(node));
    }

    internal static ContentBlockParam Document(AIContent content, JsonObject source)
    {
        var node = new JsonObject { ["type"] = "document", ["source"] = source };
        foreach (var (key, wire) in new[] { (P.DocumentTitle, "title"), (P.DocumentContext, "context") })
            if (AnthropicExecutionContext.ReadString(content.AdditionalProperties, key) is { } text) node[wire] = text;
        if (content.AdditionalProperties?.TryGetValue(P.CitationsEnabled, out var value) == true)
        {
            var enabled = AsJson(value);
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("anthropic_citations_enabled must be a boolean.");
            node["citations"] = new JsonObject { ["enabled"] = enabled.GetBoolean() };
        }
        return new ContentBlockParam(JsonSerializer.SerializeToElement(node));
    }

    internal static MessageCreateParams Apply(MessageCreateParams request, ChatOptions? options, IEnumerable<ChatMessage>? originals)
    {
        var body = JsonSerializer.SerializeToNode(request.RawBodyData)!.AsObject();
        Cache(body, options?.AdditionalProperties);
        var budget = options?.AdditionalProperties?.TryGetValue(P.ThinkingBudgetTokens, out var budgetValue) == true ? AsJson(budgetValue) : (JsonElement?)null;
        if (options?.Reasoning is not null || budget is not null)
        {
            var effort = options?.Reasoning?.Effort;
            var output = options?.Reasoning?.Output;
            if (output == ReasoningOutput.Full)
                throw new NotSupportedException("Claude exposes summarized thinking, not full raw reasoning. Use ReasoningOutput.Summary.");
            var thinking = new JsonObject { ["type"] = effort == ReasoningEffort.None ? "disabled" : budget is null ? "adaptive" : "enabled" };
            if (budget is { } b)
            {
                if (effort == ReasoningEffort.None || b.ValueKind != JsonValueKind.Number || !b.TryGetInt64(out var tokens) || tokens < 1024 || tokens >= request.MaxTokens)
                    throw new ArgumentException("Thinking budget must be at least 1024 and less than MaxOutputTokens, with thinking enabled.");
                thinking["budget_tokens"] = tokens;
            }
            if (effort != ReasoningEffort.None)
                thinking["display"] = output == ReasoningOutput.None ? "omitted" : "summarized";
            body["thinking"] = thinking;
            if (effort is not null and not ReasoningEffort.None && budget is null)
                body["output_config"] = new JsonObject { ["effort"] = effort switch
                {
                    ReasoningEffort.Low => "low", ReasoningEffort.Medium => "medium",
                    ReasoningEffort.High => "high", ReasoningEffort.ExtraHigh => "xhigh",
                    _ => throw new NotSupportedException("Unsupported reasoning effort.")
                } };
        }
        // Preserve per-content cache breakpoints in system messages instead of flattening them.
        var systems = originals?.Where(m => m.Role == ChatRole.System).ToList();
        if (systems?.Any(m => m.Contents.Any(c => c.AdditionalProperties?.ContainsKey(P.CacheControl) == true)) == true)
        {
            var system = new JsonArray();
            if (!string.IsNullOrEmpty(options?.Instructions)) system.Add(new JsonObject { ["type"] = "text", ["text"] = options.Instructions });
            foreach (var message in systems)
                foreach (var content in message.Contents)
                    foreach (var block in AnthropicContentConverter.ToAnthropicContent([content]))
                        system.Add(JsonNode.Parse(Decorate(block, content).Json.GetRawText()));
            body["system"] = system;
        }
        var headers = new Dictionary<string, JsonElement>(request.RawHeaderData);
        var servers = options?.Tools?.OfType<HostedMcpServerTool>().ToList();
        if (servers is { Count: > 0 })
        {
            if (servers.Select(s => s.ServerName).Distinct(StringComparer.Ordinal).Count() != servers.Count)
                throw new ArgumentException("MCP server names must be unique.");
            body["mcp_servers"] = new JsonArray(servers.Select(Server).Cast<JsonNode?>().ToArray());
        }
        if (servers is { Count: > 0 } || originals?.Any(m => AnthropicExecutionContext.HasBlock(m,
            b => String(b, "type") is "mcp_tool_use" or "mcp_tool_result") || m.Contents.Any(c => c is McpServerToolCallContent or McpServerToolResultContent)) == true)
            headers["anthropic-beta"] = JsonSerializer.SerializeToElement("mcp-client-2025-11-20");
        return MessageCreateParams.FromRawUnchecked(headers, request.RawQueryData,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.ToJsonString())!);
    }

    private static JsonObject Server(HostedMcpServerTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ServerName) || !Uri.TryCreate(tool.ServerAddress, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("Hosted MCP requires a server name and an absolute HTTPS server address.");
        if (tool.ApprovalMode is not null && tool.ApprovalMode != HostedMcpServerToolApprovalMode.NeverRequire)
            throw new NotSupportedException("Claude's hosted MCP connector cannot perform approval round trips. Use NeverRequire with an appropriate AllowedTools list, or a client-side MCP integration.");
        if (!string.IsNullOrEmpty(tool.ServerDescription))
            throw new NotSupportedException("Claude's hosted MCP connector does not accept a server description.");
        var result = new JsonObject { ["type"] = "url", ["name"] = tool.ServerName, ["url"] = tool.ServerAddress };
        if (tool.Headers is { Count: > 0 })
        {
            if (tool.Headers.Count != 1 || !string.Equals(tool.Headers.First().Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                !tool.Headers.First().Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tool.Headers.First().Value[7..]))
                throw new NotSupportedException("Hosted MCP supports only an Authorization Bearer header.");
            result["authorization_token"] = tool.Headers.First().Value[7..];
        }
        return result;
    }

    internal static ToolUnion HostedTool(AITool tool)
    {
        JsonObject node;
        if (tool is HostedWebSearchTool)
        {
            node = new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search" };
            if (tool.AdditionalProperties?.TryGetValue(P.WebSearchOptions, out var settings) == true)
            {
                var json = AsJson(settings);
                if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("Web search options must be an object.");
                foreach (var field in json.EnumerateObject())
                {
                    if (field.Name is not ("max_uses" or "allowed_domains" or "blocked_domains" or "user_location"))
                        throw new ArgumentException("Unsupported web search setting.");
                    node[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                }
                if (node.ContainsKey("allowed_domains") && node.ContainsKey("blocked_domains"))
                    throw new ArgumentException("Web search accepts either allowed_domains or blocked_domains, not both.");
            }
        }
        else if (tool is HostedMcpServerTool mcp)
        {
            var configs = new JsonObject();
            foreach (var name in mcp.AllowedTools ?? [])
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("MCP allowed tool names must be nonempty.");
                configs[name] = new JsonObject { ["enabled"] = true };
            }
            node = new JsonObject { ["type"] = "mcp_toolset", ["mcp_server_name"] = mcp.ServerName,
                ["default_config"] = new JsonObject { ["enabled"] = mcp.AllowedTools is null }, ["configs"] = configs };
        }
        else throw new NotSupportedException($"Unsupported hosted tool: {tool.GetType().Name}.");
        Cache(node, tool.AdditionalProperties);
        return new ToolUnion(JsonSerializer.SerializeToElement(node));
    }

    internal static bool Rich(JsonElement block) => String(block, "type") is not ("text" or "tool_use") ||
        (block.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array && citations.GetArrayLength() > 0);

    internal static AIContent? Project(JsonElement block)
    {
        var type = String(block, "type");
        if (type == "text")
        {
            var text = new TextContent(String(block, "text") ?? "");
            if (block.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                text.Annotations = citations.EnumerateArray().Select(c => (AIAnnotation)Citation(c)).ToList();
            return text;
        }
        if (type is "thinking" or "redacted_thinking")
            return new TextReasoningContent(String(block, "thinking") ?? "")
            {
                ProtectedData = String(block, type == "thinking" ? "signature" : "data"),
                AdditionalProperties = new() { ["anthropic_thinking_type"] = type }
            };
        if (type == "image" && block.TryGetProperty("source", out var source))
        {
            if (String(source, "type") == "base64")
                return new DataContent(Convert.FromBase64String(source.GetProperty("data").GetString()!), source.GetProperty("media_type").GetString()!);
            if (String(source, "type") == "url" && Uri.TryCreate(String(source, "url"), UriKind.Absolute, out var imageUri))
                return new UriContent(imageUri, "image/*");
        }
        if (type == "server_tool_use" && String(block, "name") == "web_search")
            return new WebSearchToolCallContent(block.GetProperty("id").GetString()!)
            { Queries = String(block.GetProperty("input"), "query") is { } query ? [query] : [] };
        if (type == "web_search_tool_result")
        {
            var result = new WebSearchToolResultContent(block.GetProperty("tool_use_id").GetString()!) { Outputs = [] };
            var body = block.GetProperty("content");
            if (body.ValueKind == JsonValueKind.Array)
                foreach (var entry in body.EnumerateArray())
                    result.Outputs.Add(new TextContent(String(entry, "title") ?? String(entry, "url") ?? "")
                    { Annotations = [Citation(entry)], AdditionalProperties = new() { ["anthropic_search_result"] = entry.Clone() } });
            else result.AdditionalProperties = new() { [P.IsError] = true, ["anthropic_error"] = body.Clone() };
            return result;
        }
        if (type == "mcp_tool_use")
            return new McpServerToolCallContent(block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, block.GetProperty("server_name").GetString()!)
            { Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(block.GetProperty("input")) };
        if (type == "mcp_tool_result")
        {
            var result = new McpServerToolResultContent(block.GetProperty("tool_use_id").GetString()!)
            { Outputs = [], AdditionalProperties = new() { [P.IsError] = block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True } };
            if (block.TryGetProperty("content", out var contents))
            {
                if (contents.ValueKind == JsonValueKind.String) result.Outputs.Add(new TextContent(contents.GetString()!));
                else if (contents.ValueKind == JsonValueKind.Array)
                    foreach (var item in contents.EnumerateArray())
                        result.Outputs.Add(Project(item) ?? new AIContent { AdditionalProperties = new() { ["anthropic_mcp_content"] = item.Clone() } });
            }
            return result;
        }
        return null;
    }

    internal static CitationAnnotation Citation(JsonElement citation)
    {
        var url = String(citation, "url");
        return new CitationAnnotation
        {
            Title = String(citation, "title") ?? String(citation, "document_title"),
            Url = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null,
            FileId = String(citation, "file_id"), Snippet = String(citation, "cited_text"),
            ToolName = String(citation, "type") is "web_search_result_location" or "web_search_result" ? "web_search" : null,
            // Source offsets refer to the source document, not the generated answer. Keep them intact.
            AdditionalProperties = new() { [P.Citation] = citation.Clone() }, RawRepresentation = citation.Clone()
        };
    }

    internal static UsageDetails Usage(JsonElement usage)
    {
        static long Count(JsonElement json, string key) => json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        var uncached = Count(usage, "input_tokens");
        var read = Count(usage, "cache_read_input_tokens");
        var creation = Count(usage, "cache_creation_input_tokens");
        var input = uncached + read + creation;
        var output = Count(usage, "output_tokens");
        var details = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output,
            CachedInputTokenCount = usage.TryGetProperty("cache_read_input_tokens", out _) ? read : null,
            AdditionalCounts = new() { ["anthropic_uncached_input_tokens"] = uncached, ["anthropic_cache_creation_input_tokens"] = creation } };
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails) && outputDetails.ValueKind == JsonValueKind.Object)
            details.ReasoningTokenCount = Count(outputDetails, "thinking_tokens");
        if (usage.TryGetProperty("cache_creation", out var cache) && cache.ValueKind == JsonValueKind.Object)
            foreach (var item in cache.EnumerateObject())
                if (item.Value.TryGetInt64(out var count)) details.AdditionalCounts["anthropic_" + item.Name] = count;
        return details;
    }
}
