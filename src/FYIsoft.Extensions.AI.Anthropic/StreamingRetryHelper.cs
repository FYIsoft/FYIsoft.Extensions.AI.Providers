using System.Runtime.CompilerServices;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Helper utilities for retrying transient failures from upstream chat providers.
/// Used by <see cref="AnthropicChatClient"/> to wrap streaming and non-streaming
/// calls with a single-retry policy for transient 5xx errors.
/// </summary>
internal static class StreamingRetryHelper
{
    /// <summary>
    /// Wraps an asynchronous stream factory with retry semantics. The stream factory is
    /// invoked up to <paramref name="maxAttempts"/> times. If <paramref name="shouldRetry"/>
    /// returns <see langword="true"/> for an exception thrown either by the factory itself
    /// or by <see cref="IAsyncEnumerator{T}.MoveNextAsync"/>, the enumeration is restarted
    /// after a <paramref name="retryDelay"/> pause — but only if no items have been yielded
    /// to the caller yet (a partial stream cannot be safely restarted).
    /// <see cref="OperationCanceledException"/> is never retried; it always propagates.
    /// </summary>
    internal static async IAsyncEnumerable<T> RetryStreamAsync<T>(
        Func<IAsyncEnumerable<T>> streamFactory,
        Func<Exception, bool> shouldRetry,
        Action<Exception, int> onRetry,
        TimeSpan retryDelay,
        int maxAttempts,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        ArgumentNullException.ThrowIfNull(onRetry);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var yieldedAny = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IAsyncEnumerable<T> stream;
            try
            {
                stream = streamFactory();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (shouldRetry(ex) && !yieldedAny && attempt < maxAttempts)
            {
                onRetry(ex, attempt);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var enumerator = stream.GetAsyncEnumerator(cancellationToken);
            var faulted = false;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (shouldRetry(ex) && !yieldedAny && attempt < maxAttempts)
                    {
                        onRetry(ex, attempt);
                        faulted = true;
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    yieldedAny = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (faulted)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield break;
        }
    }

    /// <summary>
    /// Invokes <paramref name="factory"/> up to <paramref name="maxAttempts"/> times,
    /// retrying after <paramref name="retryDelay"/> when <paramref name="shouldRetry"/>
    /// returns <see langword="true"/>. On the final attempt the original exception
    /// propagates naturally, preserving its stack trace.
    /// <see cref="OperationCanceledException"/> always propagates without retry.
    /// </summary>
    internal static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        Func<Exception, bool> shouldRetry,
        Action<Exception, int> onRetry,
        TimeSpan retryDelay,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        ArgumentNullException.ThrowIfNull(onRetry);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await factory(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (shouldRetry(ex) && attempt < maxAttempts)
            {
                onRetry(ex, attempt);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
