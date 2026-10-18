using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Azure.Core;
using Microsoft.Extensions.AI.Decision1.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>A client for Microsoft-Decision-1 deployments in Microsoft Foundry.</summary>
public interface IDecision1Client
{
    /// <summary>
    /// Sends one state with one or more named questions and returns every answer.
    /// </summary>
    /// <param name="state">Text or a JSON object. Strings, <see cref="System.Text.Json.JsonElement"/> and
    /// <see cref="System.Text.Json.Nodes.JsonNode"/> convert implicitly; use <see cref="Decision1State.FromObject{T}(T)"/> for other objects.</param>
    /// <param name="questions">Questions keyed by a unique name; the response uses the same names.</param>
    /// <param name="cancellationToken">Cancels the request, token acquisition and retry delays.</param>
    /// <exception cref="ArgumentException">The state or questions are invalid. Nothing was sent.</exception>
    /// <exception cref="Decision1ApiException">The service returned an error status (see the subclasses).</exception>
    /// <exception cref="Decision1ConnectionException">The service could not be reached.</exception>
    /// <exception cref="Decision1TimeoutException">Every attempt timed out.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<Decision1Response> DecideAsync(
        Decision1State state,
        IEnumerable<KeyValuePair<string, Decision1Question>> questions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls <c>POST {endpoint}/providers/microsoft/v1/systemone</c> on a Foundry resource with Entra ID or
/// API key authentication, per-attempt timeouts and retries for transient failures.
/// </summary>
/// <remarks>
/// Instances are thread-safe. Reuse one client (or register it with
/// <see cref="Decision1ServiceCollectionExtensions.AddDecision1Client(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{Decision1ClientOptions}?)"/>);
/// the Entra ID token is cached per client, or per service provider when registered through DI.
/// </remarks>
public sealed partial class Decision1Client : IDecision1Client, IDisposable
{
    private static readonly ProductInfoHeaderValue UserAgent = new(
        "FYIsoft.Extensions.AI.Decision1",
        typeof(Decision1Client).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly Decision1ClientCore _core;
    private readonly ILogger _logger;

    /// <summary>Creates a client that owns its own <see cref="HttpClient"/>.</summary>
    /// <exception cref="ArgumentException">The options are incomplete or invalid.</exception>
    public Decision1Client(Decision1ClientOptions options, ILogger<Decision1Client>? logger = null)
        : this(new HttpClient(), Decision1ClientCore.Create(options), logger, disposeHttpClient: true)
    {
    }

    /// <summary>Creates a client that uses a caller-owned <see cref="HttpClient"/>. The client does not dispose it.</summary>
    /// <exception cref="ArgumentException">The options are incomplete or invalid.</exception>
    public Decision1Client(Decision1ClientOptions options, HttpClient httpClient, ILogger<Decision1Client>? logger = null)
        : this(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), Decision1ClientCore.Create(options), logger, disposeHttpClient: false)
    {
    }

    internal Decision1Client(HttpClient httpClient, Decision1ClientCore core, ILogger? logger, bool disposeHttpClient)
    {
        _httpClient = httpClient;
        _core = core;
        _logger = logger ?? NullLogger.Instance;
        _disposeHttpClient = disposeHttpClient;
    }

    /// <summary>The deployment name sent as <c>model</c>.</summary>
    public string Deployment => _core.Options.Deployment;

    /// <summary>The full request URI, ending in <c>/providers/microsoft/v1/systemone</c>.</summary>
    public Uri RequestUri => _core.Options.RequestUri;

    /// <inheritdoc />
    public Task<Decision1Response> DecideAsync(
        Decision1State state,
        IEnumerable<KeyValuePair<string, Decision1Question>> questions,
        CancellationToken cancellationToken = default)
    {
        // Validation and serialization run synchronously so ArgumentException is thrown before any I/O.
        var body = Decision1Wire.WriteRequest(_core.Options.Deployment, state, questions, _core.Options.SerializerOptions);
        return SendAsync(body, cancellationToken);
    }

    /// <summary>Sends a pre-serialized body without client-side validation. Used by tests that need a server-side 400.</summary>
    internal Task<Decision1Response> SendRawAsync(byte[] body, CancellationToken cancellationToken) => SendAsync(body, cancellationToken);

    private async Task<Decision1Response> SendAsync(byte[] body, CancellationToken cancellationToken)
    {
        var options = _core.Options;
        var retry = options.Retry;
        var maxAttempts = retry.MaxRetries + 1;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canRetry = attempt < maxAttempts;
            Exception? transportFailure = null;
            var timedOut = false;

            using var request = new HttpRequestMessage(HttpMethod.Post, options.RequestUri)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(UserAgent);
            await _core.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.Timeout != Timeout.InfiniteTimeSpan)
                attemptCts.CancelAfter(options.Timeout);

            HttpResponseMessage? response = null;
            byte[] responseBody = [];
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
                responseBody = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // The per-attempt timeout, or HttpClient.Timeout, elapsed.
                response?.Dispose();
                response = null;
                timedOut = true;
                transportFailure = ex;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                response?.Dispose();
                response = null;
                transportFailure = ex;
            }

            if (response is null)
            {
                if (canRetry)
                {
                    var delay = _core.ComputeBackoff(attempt);
                    LogTransportRetry(_logger, timedOut ? "timeout" : "connection failure", attempt, maxAttempts, delay.TotalMilliseconds);
                    await _core.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (timedOut)
                    throw new Decision1TimeoutException(
                        $"The Decision1 request timed out after {options.Timeout.TotalSeconds:0.###} s on each of {attempt} attempt(s).",
                        options.Timeout, transportFailure);
                throw new Decision1ConnectionException(
                    $"The Decision1 service at {options.RequestUri.Host} could not be reached after {attempt} attempt(s): {transportFailure!.Message}",
                    transportFailure);
            }

            using (response)
            {
                var metadata = Decision1Wire.ReadMetadata(response, attempt);
                if (response.IsSuccessStatusCode)
                    return Decision1Wire.ParseResponse(responseBody, metadata);

                var status = (int)response.StatusCode;
                var retryAfter = Decision1Wire.ReadRetryAfter(response, _core.TimeProvider.GetUtcNow());
                if (IsRetryable(status) && canRetry)
                {
                    var delay = retryAfter is { } hint ? Min(hint, retry.MaxRetryAfter) : _core.ComputeBackoff(attempt);
                    LogStatusRetry(_logger, status, metadata.RequestId, attempt, maxAttempts, delay.TotalMilliseconds);
                    await _core.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    _core.InvalidateToken();

                var exception = Decision1Wire.CreateApiException(response.StatusCode, response.ReasonPhrase, responseBody, response.Headers, metadata, retryAfter);
                LogFailure(_logger, status, exception.ErrorCode, metadata.RequestId, attempt);
                throw exception;
            }
        }
    }

    internal static bool IsRetryable(int status) => status is 408 or 429 or (>= 500 and <= 599);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Decision1 request returned HTTP {Status} (request id {RequestId}); retrying attempt {Attempt} of {MaxAttempts} after {DelayMs} ms.")]
    private static partial void LogStatusRetry(ILogger logger, int status, string? requestId, int attempt, int maxAttempts, double delayMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Decision1 request failed with a {Failure}; retrying attempt {Attempt} of {MaxAttempts} after {DelayMs} ms.")]
    private static partial void LogTransportRetry(ILogger logger, string failure, int attempt, int maxAttempts, double delayMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Decision1 request failed with HTTP {Status} ({ErrorCode}, request id {RequestId}) after {Attempts} attempt(s).")]
    private static partial void LogFailure(ILogger logger, int status, string? errorCode, string? requestId, int attempts);
}

/// <summary>
/// State shared by every client built from the same options: resolved settings, the Entra ID token
/// cache, and the retry clock. DI registers one instance per service provider.
/// </summary>
internal sealed class Decision1ClientCore
{
    private readonly Decision1TokenCache? _tokenCache;

    internal Decision1ClientCore(ResolvedDecision1Options options, TimeProvider? timeProvider = null)
    {
        Options = options;
        TimeProvider = timeProvider ?? TimeProvider.System;
        if (options.Credential is TokenCredential credential)
            _tokenCache = new Decision1TokenCache(credential, options.TokenScope, TimeProvider);
    }

    public static Decision1ClientCore Create(Decision1ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.Resolve());
    }

    public ResolvedDecision1Options Options { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>Delay hook; tests replace it to observe retry delays without sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Jitter source returning a value in [0, 1); tests replace it for determinism.</summary>
    internal Func<double> NextJitter { get; set; } = Random.Shared.NextDouble;

    public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_tokenCache is not null)
        {
            var token = await _tokenCache.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("api-key", Options.ApiKey);
        }
    }

    public void InvalidateToken() => _tokenCache?.Invalidate();

    /// <summary>Exponential backoff for the retry after <paramref name="attempt"/>, capped, with downward jitter.</summary>
    public TimeSpan ComputeBackoff(int attempt)
    {
        var retry = Options.Retry;
        var exponent = Math.Min(attempt - 1, 30);
        var baseMs = Math.Min(retry.InitialDelay.TotalMilliseconds * Math.Pow(2, exponent), retry.MaxDelay.TotalMilliseconds);
        var jittered = baseMs * (1 - retry.JitterRatio * NextJitter());
        return TimeSpan.FromMilliseconds(Math.Max(0, jittered));
    }
}
