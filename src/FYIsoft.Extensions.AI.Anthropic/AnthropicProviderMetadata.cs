namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>Provider-specific options carried in AdditionalProperties. Values may be JSON elements.</summary>
public static class AnthropicProviderMetadata
{
    /// <summary>Cache configuration object: type = ephemeral, optional ttl = 5m or 1h. Supported on options, content, and tools.</summary>
    public const string CacheControl = "anthropic_cache_control";
    /// <summary>Optional manual thinking budget (at least 1024, less than MaxOutputTokens). Otherwise reasoning uses adaptive thinking.</summary>
    public const string ThinkingBudgetTokens = "anthropic_thinking_budget_tokens";
    /// <summary>Boolean enabling citations on a PDF or text document.</summary>
    public const string CitationsEnabled = "anthropic_citations_enabled";
    /// <summary>Optional document title.</summary>
    public const string DocumentTitle = "anthropic_document_title";
    /// <summary>Optional document context.</summary>
    public const string DocumentContext = "anthropic_document_context";
    /// <summary>Web search settings object: max_uses, allowed_domains, blocked_domains, user_location.</summary>
    public const string WebSearchOptions = "anthropic_web_search_options";
    /// <summary>Complete, paused, incomplete, or failed; incomplete provider history cannot be replayed.</summary>
    public const string ResponseState = "anthropic_response_state";
    /// <summary>Original citation with source-document offsets, page numbers, and provider reference data.</summary>
    public const string Citation = "anthropic_citation";
    /// <summary>Provider tool failure indicator.</summary>
    public const string IsError = "anthropic_is_error";
}
