namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// Configuration for <see cref="JevClient"/>.
/// </summary>
/// <remarks>
/// Values resolve in order: explicit property, environment variable, default.
/// Empty or whitespace environment values are ignored.
/// <list type="bullet">
/// <item><see cref="ApiKey"/> ← <c>TYPESAFE_API_KEY</c> (required)</item>
/// <item><see cref="BaseUrl"/> ← <c>TYPESAFE_BASE_URL</c> ← <c>https://api.typesafe.ai</c></item>
/// <item><see cref="DefaultModel"/> ← <c>TYPESAFE_DEFAULT_MODEL</c> ← <c>jev-latest</c></item>
/// </list>
/// </remarks>
public sealed class JevClientOptions
{
    /// <summary>Default API base URL.</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    /// <summary>Default model alias.</summary>
    public const string DefaultModelId = "jev-latest";

    /// <summary>Environment variable holding the API key.</summary>
    public const string ApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";

    /// <summary>Environment variable holding the base URL.</summary>
    public const string BaseUrlEnvironmentVariable = "TYPESAFE_BASE_URL";

    /// <summary>Environment variable holding the default model.</summary>
    public const string DefaultModelEnvironmentVariable = "TYPESAFE_DEFAULT_MODEL";

    /// <summary>The TypeSafe API key. Sent as <c>Authorization: Bearer</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The API base URL.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The model used when a request does not name one.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Timeout for a single attempt, including reading the response body. There is no total budget across retries.
    /// </summary>
    public TimeSpan Timeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>Retry policy applied to every request.</summary>
    public JevRetryPolicy Retry { get; set; } = new();

    /// <summary>Headers added to every request. Credential and SDK headers cannot be overridden.</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal string ResolveApiKey() =>
        Coalesce(ApiKey, Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable))
        ?? throw new JevException(
            $"The TypeSafe API key must be set via {nameof(JevClientOptions)}.{nameof(ApiKey)} or the {ApiKeyEnvironmentVariable} environment variable.");

    internal Uri ResolveBaseUrl()
    {
        var url = BaseUrl?.ToString()
            ?? Coalesce(Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable))
            ?? DefaultBaseUrl;
        return new Uri(url.TrimEnd('/'), UriKind.Absolute);
    }

    internal string ResolveDefaultModel() =>
        Coalesce(DefaultModel, Environment.GetEnvironmentVariable(DefaultModelEnvironmentVariable)) ?? DefaultModelId;

    private static string? Coalesce(params ReadOnlySpan<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// Per-call overrides for a <see cref="JevClient"/> request.
/// </summary>
public sealed class JevRequestOptions
{
    /// <summary>Overrides <see cref="JevClientOptions.Timeout"/> for this call.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Overrides <see cref="JevClientOptions.Retry"/> for this call.</summary>
    public JevRetryPolicy? Retry { get; set; }

    /// <summary>Headers merged over <see cref="JevClientOptions.DefaultHeaders"/> for this call.</summary>
    public IDictionary<string, string>? Headers { get; set; }
}
