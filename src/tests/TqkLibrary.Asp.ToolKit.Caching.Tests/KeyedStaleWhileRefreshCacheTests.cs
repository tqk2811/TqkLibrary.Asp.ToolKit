using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

public sealed class KeyedStaleWhileRefreshCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private static KeyedStaleWhileRefreshCache<string, string> NewCache(int maxEntries, IEqualityComparer<string>? comparer = null)
        => new(new FakeTimeProvider(), Ttl, maxEntries, comparer: comparer);

    [Fact]
    public async Task NewKey_WhileCacheIsFull_ConcurrentCallersShareOneLoad()
    {
        using var cache = NewCache(1);
        await cache.GetAsync("a", _ => Task.FromResult("a"));

        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        // Every call is issued before the load ends, so all of them are already waiting on the pending slot.
        var calls = Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync("b", _ => { Interlocked.Increment(ref loads); return gate.Task; }))
            .ToArray();
        gate.SetResult("b");

        var results = await Task.WhenAll(calls).WaitAsync(Timeout);
        Assert.All(results, r => Assert.Equal("b", r));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task FullCache_NewKeyIsAdmittedAfterCompaction()
    {
        using var cache = NewCache(5);
        for (var i = 0; i < 5; i++)
        {
            var key = i.ToString();
            await cache.GetAsync(key, _ => Task.FromResult(key));
        }

        // The first store of "new" is refused (cache full) and triggers a background compaction; retry until it is held.
        var deadline = DateTime.UtcNow + Timeout;
        var held = false;
        while (!held && DateTime.UtcNow < deadline)
        {
            await cache.GetAsync("new", _ => Task.FromResult("new"));
            held = await cache.GetAsync("new", _ => Task.FromResult("reloaded")) == "new";
            if (!held)
                await Task.Delay(20);
        }

        Assert.True(held, "a new key was never admitted into a full cache");
        Assert.True(cache.Count <= 5, $"Count = {cache.Count}");
    }

    [Fact]
    public async Task Invalidate_Key_DuringFirstLoad_ResultIsNotKept()
    {
        using var cache = NewCache(10);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = cache.GetAsync("a", _ => gate.Task);

        cache.Invalidate("a");
        gate.SetResult("old");
        Assert.Equal("old", await inFlight.WaitAsync(Timeout));

        Assert.Equal("new", await cache.GetAsync("a", _ => Task.FromResult("new")));
    }

    [Fact]
    public async Task HeldKey_IsServedWithoutLoading()
    {
        using var cache = NewCache(10);
        await cache.GetAsync("a", _ => Task.FromResult("first"));

        var value = await cache.GetAsync("a", _ => throw new InvalidOperationException("must not load"));

        Assert.Equal("first", value);
    }

    [Fact]
    public async Task Invalidate_Key_NextCallLoadsAgain()
    {
        using var cache = NewCache(10);
        await cache.GetAsync("a", _ => Task.FromResult("old"));

        cache.Invalidate("a");
        var value = await cache.GetAsync("a", _ => Task.FromResult("new"));

        Assert.Equal("new", value);
    }

    [Fact]
    public async Task InvalidateAll_DuringFirstLoad_ResultIsNotKept()
    {
        using var cache = NewCache(10);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = cache.GetAsync("a", _ => { started.SetResult(); return gate.Task; });
        await started.Task.WaitAsync(Timeout);

        cache.InvalidateAll();
        gate.SetResult("old");
        Assert.Equal("old", await inFlight.WaitAsync(Timeout));

        var value = await cache.GetAsync("a", _ => Task.FromResult("new"));
        Assert.Equal("new", value);
    }

    [Fact]
    public async Task InvalidateAll_HeldKeys_NextCallLoadsAgain()
    {
        using var cache = NewCache(10);
        await cache.GetAsync("a", _ => Task.FromResult("old-a"));
        await cache.GetAsync("b", _ => Task.FromResult("old-b"));

        cache.InvalidateAll();

        Assert.Equal("new-a", await cache.GetAsync("a", _ => Task.FromResult("new-a")));
        Assert.Equal("new-b", await cache.GetAsync("b", _ => Task.FromResult("new-b")));
    }

    [Fact]
    public async Task Comparer_IsUsedForHeldKeys()
    {
        using var cache = NewCache(10, StringComparer.OrdinalIgnoreCase);
        await cache.GetAsync("a", _ => Task.FromResult("lower"));

        var value = await cache.GetAsync("A", _ => throw new InvalidOperationException("must not load"));

        Assert.Equal("lower", value);
    }

    [Fact]
    public async Task Registry_Dispose_DisposesKeyedCaches()
    {
        var registry = new CacheRegistry<TestGroup>(new FakeTimeProvider());
        var cache = registry.CreateKeyed<string, string>(Ttl, 10, TestGroup.A);
        await cache.GetAsync("a", _ => Task.FromResult("a"));

        registry.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync("a", _ => Task.FromResult("a")));
    }

    public enum TestGroup { A }
}
