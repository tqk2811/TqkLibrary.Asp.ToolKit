using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

public sealed class StaleWhileRefreshCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Get_WithinTtl_ReturnsCachedWithoutLoading()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        int calls = 0;
        Task<int> Load(CancellationToken _) => Task.FromResult(++calls);

        Assert.Equal(1, await cache.GetAsync(Load));
        time.Advance(Ttl - TimeSpan.FromSeconds(1));
        Assert.Equal(1, await cache.GetAsync(Load));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_AfterExpiry_Reloads()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        int calls = 0;
        Task<int> Load(CancellationToken _) => Task.FromResult(++calls);

        await cache.GetAsync(Load);
        time.Advance(Ttl);
        Assert.Equal(2, await cache.GetAsync(Load));
    }

    [Fact]
    public async Task Get_ConcurrentFirstLoad_LoadsOnce()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        var gate = new TaskCompletionSource();
        int calls = 0;
        async Task<int> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 7;
        }

        var tasks = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(Load)).ToArray();
        gate.SetResult();
        int[] results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(7, r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_ExpiredWhileRefreshing_ReturnsStaleImmediately()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        time.Advance(Ttl);

        var gate = new TaskCompletionSource();
        var refresh = cache.GetAsync(async _ => { await gate.Task; return 2; });
        int stale = await cache.GetAsync(_ => Task.FromResult(99));

        Assert.Equal(1, stale);
        gate.SetResult();
        Assert.Equal(2, await refresh);
        Assert.Equal(2, await cache.GetAsync(_ => Task.FromResult(99)));
    }

    [Fact]
    public async Task Get_AfterInvalidate_WaitsForFreshValue()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        cache.Invalidate();

        var gate = new TaskCompletionSource();
        var first = cache.GetAsync(async _ => { await gate.Task; return 2; });
        var second = cache.GetAsync(_ => Task.FromResult(3));

        await Task.Delay(50);
        Assert.False(second.IsCompleted); // must not return the old value 1
        gate.SetResult();
        Assert.Equal(2, await first);
        Assert.Equal(2, await second); // the waiter sees the value the first caller just loaded
    }

    [Fact]
    public async Task Invalidate_DuringInFlightLoad_DoesNotOverwrite()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        var gate = new TaskCompletionSource();
        var inFlight = cache.GetAsync(async _ => { await gate.Task; return 1; });

        cache.Invalidate();
        gate.SetResult();
        Assert.Equal(1, await inFlight);

        // The result of a load started before Invalidate is not remembered: the next call loads again.
        Assert.Equal(2, await cache.GetAsync(_ => Task.FromResult(2)));
    }

    [Fact]
    public async Task Get_LoadFails_ReturnsStaleAndLogsWarning()
    {
        var time = new FakeTimeProvider();
        var logger = new ListLogger();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl, logger);
        await cache.GetAsync(_ => Task.FromResult(1));
        time.Advance(Ttl);

        int value = await cache.GetAsync(_ => throw new InvalidOperationException("boom"));

        Assert.Equal(1, value);
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public async Task Get_FirstLoadFails_ThrowsAndDoesNotCacheFailure()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(5, await cache.GetAsync(_ => Task.FromResult(5)));
    }

    [Fact]
    public async Task Get_LoadFailsAfterInvalidate_Throws()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        cache.Invalidate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync(_ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Get_CancelledWhileLoading_NextCallLoadsNormally()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var first = cache.GetAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return 0;
        }, cts.Token);
        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Equal(5, await cache.GetAsync(_ => Task.FromResult(5)));
    }

    [Fact]
    public async Task Get_LoadFailsWithQueuedWaiters_LoadsOnceAndAllGetTheError()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        var gate = new TaskCompletionSource();
        int calls = 0;
        async Task<int> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            throw new InvalidOperationException("boom");
        }

        var tasks = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(Load)).ToArray();
        gate.SetResult();

        foreach (var t in tasks)
            await Assert.ThrowsAsync<InvalidOperationException>(() => t);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_LoadFailsWithStale_ExtendsStaleSoLoadIsNotRetriedForBackoff()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        time.Advance(Ttl);
        int calls = 0;
        Task<int> Fail(CancellationToken _) { calls++; throw new InvalidOperationException("boom"); }

        Assert.Equal(1, await cache.GetAsync(Fail));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, await cache.GetAsync(Fail));
        Assert.Equal(1, calls); // still inside the 10 s backoff

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await cache.GetAsync(Fail));
        Assert.Equal(2, calls); // backoff over, retried
    }

    [Fact]
    public async Task Get_RefreshWithStale_IgnoresCallerCancellation()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        time.Advance(Ttl);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CancellationToken seen = new(true);

        await cache.GetAsync(ct => { seen = ct; return Task.FromResult(2); }, cts.Token);

        Assert.False(seen.CanBeCanceled);
    }

    [Fact]
    public async Task Get_RefreshWithStaleFailsAfterCallerCancelled_ReturnsStaleAndBacksOff()
    {
        var time = new FakeTimeProvider();
        var cache = new StaleWhileRefreshCache<int>(time, Ttl);
        await cache.GetAsync(_ => Task.FromResult(1));
        time.Advance(Ttl);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = 0;
        Task<int> Fail(CancellationToken _) { calls++; throw new InvalidOperationException("boom"); }

        Assert.Equal(1, await cache.GetAsync(Fail, cts.Token));
        Assert.Equal(1, await cache.GetAsync(Fail));
        Assert.Equal(1, calls); // a failure after the caller cancelled still extends the old value
    }

    [Fact]
    public async Task GetWithKeep_NullKeepFor_IsNotRemembered()
    {
        var cache = new StaleWhileRefreshCache<int>(new FakeTimeProvider(), Ttl);
        int calls = 0;
        Task<(int Value, TimeSpan? KeepFor)> Load(CancellationToken _) => Task.FromResult((++calls, (TimeSpan?)null));

        Assert.Equal(1, await cache.GetWithKeepAsync(Load));
        Assert.Equal(2, await cache.GetWithKeepAsync(Load));
        Assert.False(cache.TryPeek(out _));
    }
}
