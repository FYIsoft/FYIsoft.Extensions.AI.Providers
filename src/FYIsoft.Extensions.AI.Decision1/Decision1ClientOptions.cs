using System.Text.Json;
using Azure.Core;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>Configuration for <see cref="Decision1Client"/>.</summary>
/// <remarks>
/// <para>
/// Set exactly one of <see cref="Credential"/> (Microsoft Entra ID, recommended) or <see cref="ApiKey"/>.
/// </para>
/// <para>
/// <strong>Environment variables</strong> (used only for values left null, when
/// <see cref="UseEnvironmentVariables"/> is true):
/// <list type="bullet">
/// <item><c>DECISION1_ENDPOINT</c>: the Foundry resource endpoint.</item>
/// <item><c>DECISION1_DEPLOYMENT</c>: the deployment name.</item>
/// <item><c>DECISION1_API_KEY</c>: an API key, used only when neither <see cref="Credential"/> nor <see cref="ApiKey"/> is set.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class Decision1ClientOptions
{
    /// <summary>The configuration section name used by the <c>IConfiguration</c> registration overload.</summary>
    public const string SectionName = "Decision1";

    /// <summary>Environment variable read when <see cref="Endpoint"/> is null.</summary>
    public const string EndpointEnvironmentVariable = "DECISION1_ENDPOINT";

    /// <summary>Environment variable read when <see cref="Deployment"/> is null.</summary>
    public const string DeploymentEnvironmentVariable = "DECISION1_DEPLOYMENT";

    /// <summary>Environment variable read when neither <see cref="Credential"/> nor <see cref="ApiKey"/> is set.</summary>
    public const string ApiKeyEnvironmentVariable = "DECISION1_API_KEY";

    /// <summary>The Entra ID scope for Foundry resources.</summary>
    public const string DefaultTokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>The default per-attempt timeout: 10 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The Foundry resource endpoint, for example <c>https://{name}.services.ai.azure.com</c> or
    /// <c>https://{name}.cognitiveservices.azure.com</c>. The client appends
    /// <c>/providers/microsoft/v1/systemone</c>. Must be an absolute HTTPS URI without query or fragment.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The deployment name, sent as the request's <c>model</c> value (not the underlying model name).</summary>
    public string? Deployment { get; set; }

    /// <summary>A Microsoft Entra ID credential. Tokens are cached until shortly before they expire.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>An API key, sent in the <c>api-key</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Entra ID scope. Defaults to <see cref="DefaultTokenScope"/>.</summary>
    public string TokenScope { get; set; } = DefaultTokenScope;

    /// <summary>
    /// The timeout for each HTTP attempt, excluding token acquisition and retry delays. Defaults to
    /// 10 seconds. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to rely only on the caller's token.
    /// </summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Retry behavior for transient failures.</summary>
    public Decision1RetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Options used to serialize <see cref="Decision1State.FromObject{T}(T)"/> state.
    /// Defaults to <see cref="JsonSerializerOptions.Web"/> (camelCase names).
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>Whether null settings fall back to the <c>DECISION1_*</c> environment variables. Defaults to true.</summary>
    public bool UseEnvironmentVariables { get; set; } = true;

    /// <summary>Creates options from the <c>DECISION1_*</c> environment variables.</summary>
    public static Decision1ClientOptions FromEnvironment() => new();

    internal ResolvedDecision1Options Resolve()
    {
        const string param = "options";
        var env = UseEnvironmentVariables;

        var endpoint = Endpoint;
        if (endpoint is null && env)
        {
            var raw = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out endpoint))
                    throw new ArgumentException($"{EndpointEnvironmentVariable} is not an absolute URI.", param);
            }
        }
        if (endpoint is null)
            throw new ArgumentException($"Endpoint is required. Set Decision1ClientOptions.Endpoint or {EndpointEnvironmentVariable}.", param);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Endpoint must be an absolute HTTPS URI.", param);
        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint must not contain a query string or fragment. Supply the resource endpoint only.", param);

        var deployment = Deployment;
        if (deployment is null && env)
            deployment = Environment.GetEnvironmentVariable(DeploymentEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException($"Deployment is required. Set Decision1ClientOptions.Deployment or {DeploymentEnvironmentVariable}.", param);

        if (Credential is not null && ApiKey is not null)
            throw new ArgumentException("Set exactly one of Credential or ApiKey, not both.", param);
        if (ApiKey is not null && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("ApiKey must not be empty or whitespace.", param);

        var apiKey = ApiKey;
        if (Credential is null && apiKey is null && env)
        {
            var raw = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(raw))
                apiKey = raw.Trim();
        }
        if (Credential is null && apiKey is null)
            throw new ArgumentException(
                $"Authentication is required. Set Decision1ClientOptions.Credential (Entra ID) or ApiKey, or {ApiKeyEnvironmentVariable}.", param);
        if (Credential is not null && string.IsNullOrWhiteSpace(TokenScope))
            throw new ArgumentException("TokenScope must not be empty when Credential is set.", param);

        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan && Timeout <= TimeSpan.Zero)
            throw new ArgumentException("Timeout must be positive or Timeout.InfiniteTimeSpan.", param);

        var retry = Retry ?? throw new ArgumentException("Retry must not be null.", param);
        retry.Validate(param);

        var requestUri = new Uri(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/providers/microsoft/v1/systemone", UriKind.Absolute);

        return new ResolvedDecision1Options(
            requestUri,
            deployment.Trim(),
            Credential,
            apiKey,
            TokenScope,
            Timeout,
            retry.Clone(),
            JsonSerializerOptions ?? JsonSerializerOptions.Web);
    }
}

/// <summary>Retry behavior for transient failures.</summary>
/// <remarks>
/// The client retries HTTP 408, 429 and 5xx responses, connection failures and per-attempt timeouts.
/// It never retries other 4xx statuses. Delays grow exponentially with jitter; server hints
/// (<c>retry-after-ms</c>, <c>Retry-After</c>, and <c>x-ratelimit-reset-requests</c> on 429) take
/// precedence, capped at <see cref="MaxRetryAfter"/>.
/// </remarks>
public sealed class Decision1RetryOptions
{
    /// <summary>Retries after the first attempt. 0 disables retries. Defaults to 2; at most 10.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The delay before the first retry, doubled for each subsequent retry. Defaults to 500 ms.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The cap on computed backoff delays. Defaults to 8 seconds.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The cap on server-requested delays. Defaults to 30 seconds.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The fraction of each computed delay that is randomized (0 to 1). Defaults to 0.25, meaning a delay
    /// of 75 to 100 percent of the backoff value.
    /// </summary>
    public double JitterRatio { get; set; } = 0.25;

    internal void Validate(string param)
    {
        if (MaxRetries is < 0 or > 10)
            throw new ArgumentException("Retry.MaxRetries must be from 0 through 10.", param);
        if (InitialDelay < TimeSpan.Zero || MaxDelay < TimeSpan.Zero || MaxRetryAfter < TimeSpan.Zero)
            throw new ArgumentException("Retry delays must not be negative.", param);
        if (double.IsNaN(JitterRatio) || JitterRatio is < 0 or > 1)
            throw new ArgumentException("Retry.JitterRatio must be from 0 through 1.", param);
    }

    internal Decision1RetryOptions Clone() => new()
    {
        MaxRetries = MaxRetries,
        InitialDelay = InitialDelay,
        MaxDelay = MaxDelay,
        MaxRetryAfter = MaxRetryAfter,
        JitterRatio = JitterRatio,
    };
}

// A class, not a record: a record's generated ToString would print the API key.
internal sealed class ResolvedDecision1Options(
    Uri requestUri,
    string deployment,
    TokenCredential? credential,
    string? apiKey,
    string tokenScope,
    TimeSpan timeout,
    Decision1RetryOptions retry,
    JsonSerializerOptions serializerOptions)
{
    public Uri RequestUri { get; } = requestUri;
    public string Deployment { get; } = deployment;
    public TokenCredential? Credential { get; } = credential;
    public string? ApiKey { get; } = apiKey;
    public string TokenScope { get; } = tokenScope;
    public TimeSpan Timeout { get; } = timeout;
    public Decision1RetryOptions Retry { get; } = retry;
    public JsonSerializerOptions SerializerOptions { get; } = serializerOptions;
}
