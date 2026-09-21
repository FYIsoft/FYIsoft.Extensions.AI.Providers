namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// Retry behavior for <see cref="JevClient"/>. Defaults match the TypeSafe JavaScript SDK.
/// </summary>
/// <remarks>
/// Backoff for retry <c>n</c> (0-based) is <c>min(InitialBackoff * 2^n, MaxBackoff) * (1 - random * Jitter)</c>.
/// A server-supplied <c>retry-after-ms</c> or <c>Retry-After</c> delay wins when it is no longer than <see cref="MaxRetryAfter"/>.
/// </remarks>
public sealed record JevRetryPolicy
{
    private static readonly IReadOnlySet<int> DefaultStatusCodes =
        Enumerable.Range(500, 100).Append(408).Append(429).ToHashSet();

    /// <summary>A policy that never retries.</summary>
    public static JevRetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>Maximum retries after the first attempt. Default 2.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Delay before the first retry. Default 500 ms.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on the computed backoff. Default 5 s.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction of the backoff that is randomly removed, 0 to 1. Default 0.25.</summary>
    public double Jitter { get; init; } = 0.25;

    /// <summary>HTTP status codes that are retried. Default 408, 429 and 500–599.</summary>
    public IReadOnlySet<int> RetryableStatusCodes { get; init; } = DefaultStatusCodes;

    /// <summary>Whether to honor <c>retry-after-ms</c> / <c>Retry-After</c>. Default true.</summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Server delays longer than this fall back to normal backoff. Default 60 s.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether connection failures are retried. Default true.</summary>
    public bool RetryConnectionErrors { get; init; } = true;

    /// <summary>Whether per-attempt timeouts are retried. Default true.</summary>
    public bool RetryTimeouts { get; init; } = true;

    internal TimeSpan ComputeBackoff(int retryIndex)
    {
        var exponential = InitialBackoff.TotalMilliseconds * Math.Pow(2, retryIndex);
        var capped = Math.Min(exponential, MaxBackoff.TotalMilliseconds);
        var jittered = capped * (1 - Random.Shared.NextDouble() * Math.Clamp(Jitter, 0, 1));
        return TimeSpan.FromMilliseconds(Math.Round(jittered));
    }
}
