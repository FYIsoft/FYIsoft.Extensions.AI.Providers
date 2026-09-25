using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class StreamingRetryHelperTests
{
    private sealed class TransientException : Exception
    {
        public TransientException(string message) : base(message) { }
    }

    private sealed class PermanentException : Exception
    {
        public PermanentException(string message) : base(message) { }
    }

    private static readonly Func<Exception, bool> RetryTransientOnly = ex => ex is TransientException;
    private static readonly TimeSpan ZeroDelay = TimeSpan.Zero;

    private static async IAsyncEnumerable<T> AsyncEnumerableFrom<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<T> ThrowingStream<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
        #pragma warning disable CS0162 // Unreachable code
        yield break;
        #pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<T> StreamThatYieldsThenThrows<T>(IEnumerable<T> items, Exception ex)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
        throw ex;
    }

    [Fact]
    public async Task Stream_ThrowsThenSucceeds_DeliversAllItemsFromSecondAttempt()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                attempts++;
                if (attempts == 1)
                    return ThrowingStream<string>(new TransientException("first attempt fails"));
                return AsyncEnumerableFrom(new[] { "a", "b", "c" });
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var collected = new List<string>();
        await foreach (var item in stream)
            collected.Add(item);

        collected.Should().Equal("a", "b", "c");
        attempts.Should().Be(2);
        retryEvents.Should().HaveCount(1);
        retryEvents[0].ex.Should().BeOfType<TransientException>();
        retryEvents[0].attempt.Should().Be(1);
    }

    [Fact]
    public async Task Stream_ThrowsTwice_ThrowsOriginalException()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                attempts++;
                return ThrowingStream<string>(new TransientException($"attempt {attempts} fails"));
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var collected = new List<string>();
        var act = async () =>
        {
            await foreach (var item in stream)
                collected.Add(item);
        };

        await act.Should().ThrowAsync<TransientException>();
        collected.Should().BeEmpty();
        attempts.Should().Be(2);
        retryEvents.Should().HaveCount(1);
        retryEvents[0].attempt.Should().Be(1);
    }

    [Fact]
    public async Task Stream_ThrowsAfterYield_DoesNotRetry()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                attempts++;
                return StreamThatYieldsThenThrows(new[] { "a" }, new TransientException("after yield"));
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var collected = new List<string>();
        var act = async () =>
        {
            await foreach (var item in stream)
                collected.Add(item);
        };

        await act.Should().ThrowAsync<TransientException>();
        collected.Should().Equal("a");
        attempts.Should().Be(1);
        retryEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Stream_NonMatchingException_DoesNotRetry()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                attempts++;
                return ThrowingStream<string>(new PermanentException("nope"));
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var act = async () =>
        {
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<PermanentException>();
        attempts.Should().Be(1);
        retryEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Stream_HappyPath_NoRetry()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                attempts++;
                return AsyncEnumerableFrom(new[] { "x", "y" });
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var collected = new List<string>();
        await foreach (var item in stream)
            collected.Add(item);

        collected.Should().Equal("x", "y");
        attempts.Should().Be(1);
        retryEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Async_ThrowsThenSucceeds_ReturnsSecondResult()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var result = await StreamingRetryHelper.RetryAsync<string>(
            factory: ct =>
            {
                attempts++;
                if (attempts == 1) throw new TransientException("first");
                return Task.FromResult("ok");
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        result.Should().Be("ok");
        attempts.Should().Be(2);
        retryEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task Async_ThrowsTwice_RethrowsLastException()
    {
        var attempts = 0;
        var retryEvents = new List<(Exception ex, int attempt)>();

        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct =>
            {
                attempts++;
                throw new TransientException($"attempt {attempts}");
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => retryEvents.Add((ex, attempt)),
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<TransientException>();
        attempts.Should().Be(2);
        retryEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task Async_NonMatchingException_DoesNotRetry()
    {
        var attempts = 0;

        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct =>
            {
                attempts++;
                throw new PermanentException("nope");
            },
            shouldRetry: RetryTransientOnly,
            onRetry: (ex, attempt) => { },
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<PermanentException>();
        attempts.Should().Be(1);
    }

    // ---------- Cancellation propagation tests (#7) ----------

    [Fact]
    public async Task Stream_AlreadyCancelledToken_ThrowsOperationCanceledException_WithoutRetry()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var factoryCalls = 0;
        var retryEvents = 0;

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                factoryCalls++;
                return AsyncEnumerableFrom(new[] { "a" });
            },
            shouldRetry: _ => true,
            onRetry: (_, _) => retryEvents++,
            retryDelay: ZeroDelay,
            maxAttempts: 2,
            cancellationToken: cts.Token);

        var act = async () =>
        {
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(0);
        retryEvents.Should().Be(0);
    }

    [Fact]
    public async Task Stream_FactoryThrowsOperationCanceledException_DoesNotRetry()
    {
        var factoryCalls = 0;
        var retryEvents = 0;

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                factoryCalls++;
                throw new OperationCanceledException();
            },
            shouldRetry: _ => true,
            onRetry: (_, _) => retryEvents++,
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var act = async () =>
        {
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(1);
        retryEvents.Should().Be(0);
    }

    [Fact]
    public async Task Stream_MoveNextThrowsOperationCanceledException_DoesNotRetry()
    {
        var factoryCalls = 0;
        var retryEvents = 0;

        var stream = StreamingRetryHelper.RetryStreamAsync<string>(
            streamFactory: () =>
            {
                factoryCalls++;
                return ThrowingStream<string>(new OperationCanceledException());
            },
            shouldRetry: _ => true,
            onRetry: (_, _) => retryEvents++,
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        var act = async () =>
        {
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(1);
        retryEvents.Should().Be(0);
    }

    [Fact]
    public async Task Async_AlreadyCancelledToken_ThrowsOperationCanceledException_WithoutRetry()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var factoryCalls = 0;
        var retryEvents = 0;

        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct =>
            {
                factoryCalls++;
                return Task.FromResult("ok");
            },
            shouldRetry: _ => true,
            onRetry: (_, _) => retryEvents++,
            retryDelay: ZeroDelay,
            maxAttempts: 2,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(0);
        retryEvents.Should().Be(0);
    }

    [Fact]
    public async Task Async_FactoryThrowsOperationCanceledException_DoesNotRetry()
    {
        var factoryCalls = 0;
        var retryEvents = 0;

        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct =>
            {
                factoryCalls++;
                throw new OperationCanceledException();
            },
            shouldRetry: _ => true,
            onRetry: (_, _) => retryEvents++,
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(1);
        retryEvents.Should().Be(0);
    }

    // ---------- Argument validation tests (#8) ----------

    [Fact]
    public async Task Stream_NullStreamFactory_ThrowsArgumentNullException()
    {
        var act = async () =>
        {
            var stream = StreamingRetryHelper.RetryStreamAsync<string>(
                streamFactory: null!,
                shouldRetry: RetryTransientOnly,
                onRetry: (_, _) => { },
                retryDelay: ZeroDelay,
                maxAttempts: 2);
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "streamFactory");
    }

    [Fact]
    public async Task Stream_NullShouldRetry_ThrowsArgumentNullException()
    {
        var act = async () =>
        {
            var stream = StreamingRetryHelper.RetryStreamAsync<string>(
                streamFactory: () => AsyncEnumerableFrom(new[] { "a" }),
                shouldRetry: null!,
                onRetry: (_, _) => { },
                retryDelay: ZeroDelay,
                maxAttempts: 2);
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "shouldRetry");
    }

    [Fact]
    public async Task Stream_NullOnRetry_ThrowsArgumentNullException()
    {
        var act = async () =>
        {
            var stream = StreamingRetryHelper.RetryStreamAsync<string>(
                streamFactory: () => AsyncEnumerableFrom(new[] { "a" }),
                shouldRetry: RetryTransientOnly,
                onRetry: null!,
                retryDelay: ZeroDelay,
                maxAttempts: 2);
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "onRetry");
    }

    [Fact]
    public async Task Stream_MaxAttemptsZero_ThrowsArgumentOutOfRangeException()
    {
        var act = async () =>
        {
            var stream = StreamingRetryHelper.RetryStreamAsync<string>(
                streamFactory: () => AsyncEnumerableFrom(new[] { "a" }),
                shouldRetry: RetryTransientOnly,
                onRetry: (_, _) => { },
                retryDelay: ZeroDelay,
                maxAttempts: 0);
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(ex => ex.ParamName == "maxAttempts");
    }

    [Fact]
    public async Task Stream_MaxAttemptsNegative_ThrowsArgumentOutOfRangeException()
    {
        var act = async () =>
        {
            var stream = StreamingRetryHelper.RetryStreamAsync<string>(
                streamFactory: () => AsyncEnumerableFrom(new[] { "a" }),
                shouldRetry: RetryTransientOnly,
                onRetry: (_, _) => { },
                retryDelay: ZeroDelay,
                maxAttempts: -1);
            await foreach (var _ in stream) { }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(ex => ex.ParamName == "maxAttempts");
    }

    [Fact]
    public async Task Async_NullFactory_ThrowsArgumentNullException()
    {
        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: null!,
            shouldRetry: RetryTransientOnly,
            onRetry: (_, _) => { },
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "factory");
    }

    [Fact]
    public async Task Async_NullShouldRetry_ThrowsArgumentNullException()
    {
        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct => Task.FromResult("ok"),
            shouldRetry: null!,
            onRetry: (_, _) => { },
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "shouldRetry");
    }

    [Fact]
    public async Task Async_NullOnRetry_ThrowsArgumentNullException()
    {
        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct => Task.FromResult("ok"),
            shouldRetry: RetryTransientOnly,
            onRetry: null!,
            retryDelay: ZeroDelay,
            maxAttempts: 2);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "onRetry");
    }

    [Fact]
    public async Task Async_MaxAttemptsZero_ThrowsArgumentOutOfRangeException()
    {
        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct => Task.FromResult("ok"),
            shouldRetry: RetryTransientOnly,
            onRetry: (_, _) => { },
            retryDelay: ZeroDelay,
            maxAttempts: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(ex => ex.ParamName == "maxAttempts");
    }

    [Fact]
    public async Task Async_MaxAttemptsNegative_ThrowsArgumentOutOfRangeException()
    {
        var act = async () => await StreamingRetryHelper.RetryAsync<string>(
            factory: ct => Task.FromResult("ok"),
            shouldRetry: RetryTransientOnly,
            onRetry: (_, _) => { },
            retryDelay: ZeroDelay,
            maxAttempts: -1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(ex => ex.ParamName == "maxAttempts");
    }
}
