using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// Client for the TypeSafe AI API. A .NET port of <c>TypeSafeClient</c> from <c>@typesafe-ai/sdk</c> v0.6.0.
/// </summary>
/// <remarks>
/// <para>
/// Jev is a "System One" model: it does not generate text. Send a state (text or JSON) and a set of typed
/// questions (<see cref="JevNoulQuestion"/>, <see cref="JevChoiceQuestion"/>, <see cref="JevScoreQuestion"/>);
/// it returns probabilities for each. See <see cref="JevChatClient"/> for the <see cref="IChatClient"/> adapter.
/// </para>
/// <para>This type is thread-safe; create one and reuse it.</para>
/// </remarks>
public sealed class JevClient : IDisposable
{
    internal const string RequestIdHeader = "x-typesafe-request-id";
    private const string SystemOnePath = "v1/systemone";
    private const string ModelsPath = "v1/models";

    private static readonly string SdkVersion =
        typeof(JevClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static readonly string SdkIdentifier = $"dotnet-jev/{SdkVersion}";

    private static readonly string RuntimeIdentifier =
        $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture})";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _apiKey;
    private readonly JevClientOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="options">Client options. Null reads everything from environment variables.</param>
    /// <param name="httpClient">
    /// Optional HTTP client (e.g. from <c>IHttpClientFactory</c>). It is not disposed by this client.
    /// Its <see cref="HttpClient.BaseAddress"/> and <see cref="HttpClient.Timeout"/> are ignored.
    /// </param>
    /// <param name="logger">Optional logger for retries and request diagnostics.</param>
    /// <exception cref="JevException">No API key is configured.</exception>
    public JevClient(JevClientOptions? options = null, HttpClient? httpClient = null, ILogger? logger = null)
    {
        _options = options ?? new JevClientOptions();
        _apiKey = _options.ResolveApiKey();
        BaseUrl = _options.ResolveBaseUrl();
        DefaultModel = _options.ResolveDefaultModel();
        _logger = logger ?? NullLogger.Instance;

        if (httpClient is null)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
            _disposeHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    /// <summary>The resolved API base URL.</summary>
    public Uri BaseUrl { get; }

    /// <summary>The model used when a request does not name one.</summary>
    public string DefaultModel { get; }

    /// <summary>
    /// Evaluates a state against a set of questions (<c>POST /v1/systemone</c>).
    /// </summary>
    /// <param name="request">The request. A null <see cref="SystemOneRequest.Model"/> sends <see cref="DefaultModel"/>.</param>
    /// <param name="requestOptions">Per-call overrides.</param>
    /// <param name="cancellationToken">Cancels the call, including pending retries.</param>
    /// <exception cref="ArgumentException">No questions, or a question breaks API limits.</exception>
    /// <exception cref="JevApiException">The API returned an error status.</exception>
    /// <exception cref="JevConnectionException">The API could not be reached or timed out.</exception>
    public async Task<SystemOneResponse> SystemOneAsync(
        SystemOneRequest request,
        JevRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Questions is null || request.Questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required.", nameof(request));
        }

        foreach (var (key, question) in request.Questions)
        {
            ArgumentNullException.ThrowIfNull(question, $"{nameof(request.Questions)}[{key}]");
            question.Validate(key);
        }

        var payload = string.IsNullOrWhiteSpace(request.Model)
            ? new SystemOneRequest
            {
                State = request.State,
                Model = DefaultModel,
                Questions = request.Questions,
                AdditionalProperties = request.AdditionalProperties,
            }
            : request;

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JevJson.SerializerOptions);
        var (response, requestId) = await SendAsync<SystemOneResponse>(HttpMethod.Post, SystemOnePath, body, requestOptions, cancellationToken)
            .ConfigureAwait(false);

        response.RequestId = requestId;
        return response;
    }

    /// <summary>
    /// Evaluates a state against a set of questions.
    /// </summary>
    /// <param name="state">A string, or any object serializable to JSON (a <c>JsonNode</c> is sent as-is).</param>
    /// <param name="questions">Questions keyed by caller-chosen IDs.</param>
    /// <param name="model">Model alias or versioned ID. Null uses <see cref="DefaultModel"/>.</param>
    /// <param name="requestOptions">Per-call overrides.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<SystemOneResponse> SystemOneAsync(
        object? state,
        IDictionary<string, JevQuestion> questions,
        string? model = null,
        JevRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) =>
        SystemOneAsync(
            new SystemOneRequest
            {
                State = state is null ? null : JsonSerializer.SerializeToNode(state, state.GetType(), JevJson.SerializerOptions),
                Model = model,
                Questions = questions,
            },
            requestOptions,
            cancellationToken);

    /// <summary>
    /// Lists available models and aliases (<c>GET /v1/models</c>).
    /// </summary>
    public async Task<IReadOnlyList<JevModelCard>> ListModelsAsync(
        JevRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var (list, _) = await SendAsync<JevModelList>(HttpMethod.Get, ModelsPath, body: null, requestOptions, cancellationToken)
            .ConfigureAwait(false);

        return list.Models ?? throw new JevException("Unexpected response from /v1/models: 'models' is not an array.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<(T Value, string? RequestId)> SendAsync<T>(
        HttpMethod method,
        string path,
        byte[]? body,
        JevRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        var policy = requestOptions?.Retry ?? _options.Retry;
        var timeout = requestOptions?.Timeout ?? _options.Timeout;
        var uri = new Uri($"{BaseUrl.AbsoluteUri.TrimEnd('/')}/{path}");

        for (var attempt = 0; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);
            TimeSpan delay;

            try
            {
                using var request = CreateRequest(method, uri, body, requestOptions, attempt);
                _logger.LogDebug("Jev {Method} {Uri} (attempt {Attempt})", method, uri, attempt + 1);

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);
                var content = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
                var requestId = response.Headers.TryGetValues(RequestIdHeader, out var ids) ? ids.FirstOrDefault() : null;

                if (response.IsSuccessStatusCode)
                {
                    return (Deserialize<T>(content, path), requestId);
                }

                var error = JevApiException.Create(response, content, requestId);
                if (attempt >= policy.MaxRetries || !policy.RetryableStatusCodes.Contains((int)response.StatusCode))
                {
                    throw error;
                }

                delay = ServerDelay(policy, response) ?? policy.ComputeBackoff(attempt);
                _logger.LogWarning("Jev request failed with {Status} (request {RequestId}); retrying in {Delay}ms.",
                    (int)response.StatusCode, requestId, delay.TotalMilliseconds);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= policy.MaxRetries || !policy.RetryTimeouts)
                {
                    throw new JevTimeoutException(timeout, ex);
                }

                delay = policy.ComputeBackoff(attempt);
                _logger.LogWarning("Jev request timed out after {Timeout}ms; retrying in {Delay}ms.",
                    timeout.TotalMilliseconds, delay.TotalMilliseconds);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= policy.MaxRetries || !policy.RetryConnectionErrors)
                {
                    throw new JevConnectionException(innerException: ex);
                }

                delay = policy.ComputeBackoff(attempt);
                _logger.LogWarning(ex, "Jev connection error; retrying in {Delay}ms.", delay.TotalMilliseconds);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, byte[]? body, JevRequestOptions? requestOptions, int attempt)
    {
        var request = new HttpRequestMessage(method, uri);

        // User headers first so the SDK headers below always win.
        var userHeaders = new Dictionary<string, string>(_options.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        if (requestOptions?.Headers is { } overrides)
        {
            foreach (var (name, value) in overrides)
            {
                userHeaders[name] = value;
            }
        }

        foreach (var (name, value) in userHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        SetHeader(request, "Authorization", $"Bearer {_apiKey}");
        SetHeader(request, "Accept", "application/json");
        SetHeader(request, "User-Agent", SdkIdentifier);
        SetHeader(request, "X-TypeSafe-SDK", SdkIdentifier);
        SetHeader(request, "X-TypeSafe-Runtime", RuntimeIdentifier);

        if (attempt > 0)
        {
            SetHeader(request, "X-TypeSafe-Retry-Count", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            };
        }

        return request;
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static T Deserialize<T>(byte[] content, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, JevJson.SerializerOptions)
                ?? throw new JevException($"Empty response from /{path}.");
        }
        catch (JsonException ex)
        {
            throw new JevException($"Unexpected response from /{path}: {ex.Message}", ex);
        }
    }

    private static TimeSpan? ServerDelay(JevRetryPolicy policy, HttpResponseMessage response)
    {
        if (!policy.RespectRetryAfter)
        {
            return null;
        }

        var delay = ReadRetryAfter(response);
        return delay is { } value && value >= TimeSpan.Zero && value <= policy.MaxRetryAfter ? value : null;
    }

    /// <summary>Reads <c>retry-after-ms</c>, then <c>Retry-After</c> (seconds or HTTP date).</summary>
    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
    }
}
