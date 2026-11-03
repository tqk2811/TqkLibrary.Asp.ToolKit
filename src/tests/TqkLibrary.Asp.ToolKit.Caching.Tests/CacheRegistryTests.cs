using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

public sealed class CacheRegistryTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task Keyed_InvalidateAll_ForcesReloadOfEveryKeyAndBlocksInFlight()
    {
        using var cache = new KeyedStaleWhileRefreshCache<string, int>(new FakeTimeProvider(), Ttl, 10);
        await cache.GetAsync("a", _ => Task.FromResult(1));
        var gate = new TaskCompletionSource();
        var inFlight = cache.GetAsync("b", async _ => { await gate.Task; return 10; });

        cache.InvalidateAll();
        gate.SetResult();
        await inFlight;

        Assert.Equal(2, await cache.GetAsync("a", _ => Task.FromResult(2)));
        Assert.Equal(20, await cache.GetAsync("b", _ => Task.FromResult(20)));
    }

    [Fact]
    public async Task Keyed_InvalidateKey_OnlyAffectsThatKey()
    {
        using var cache = new KeyedStaleWhileRefreshCache<string, int>(new FakeTimeProvider(), Ttl, 10);
        await cache.GetAsync("a", _ => Task.FromResult(1));
        await cache.GetAsync("b", _ => Task.FromResult(1));

        cache.Invalidate("a");

        Assert.Equal(2, await cache.GetAsync("a", _ => Task.FromResult(2)));
        Assert.Equal(1, await cache.GetAsync("b", _ => Task.FromResult(2)));
    }

    [Fact]
    public async Task Registry_InvalidateGroup_OnlyTouchesThatGroup()
    {
        using var registry = new CacheRegistry<TestGroup>(new FakeTimeProvider());
        var a = registry.Create<int>(Ttl, TestGroup.A);
        var keyedA = registry.CreateKeyed<string, int>(Ttl, 5, TestGroup.A);
        var b = registry.Create<int>(Ttl, TestGroup.B);
        await a.GetAsync(_ => Task.FromResult(1));
        await keyedA.GetAsync("k", _ => Task.FromResult(1));
        await b.GetAsync(_ => Task.FromResult(1));

        registry.Invalidate(TestGroup.A);

        Assert.Equal(2, await a.GetAsync(_ => Task.FromResult(2)));
        Assert.Equal(2, await keyedA.GetAsync("k", _ => Task.FromResult(2)));
        Assert.Equal(1, await b.GetAsync(_ => Task.FromResult(2)));
    }

    [Fact]
    public async Task Registry_InvalidateAll_TouchesEveryGroup()
    {
        using var registry = new CacheRegistry<TestGroup>(new FakeTimeProvider());
        var a = registry.Create<int>(Ttl, TestGroup.A);
        var c = registry.Create<int>(Ttl, TestGroup.C);
        await a.GetAsync(_ => Task.FromResult(1));
        await c.GetAsync(_ => Task.FromResult(1));

        registry.InvalidateAll();

        Assert.Equal(2, await a.GetAsync(_ => Task.FromResult(2)));
        Assert.Equal(2, await c.GetAsync(_ => Task.FromResult(2)));
    }

    [Fact]
    public async Task Create_MultiGroup_InvalidatingAnyOfItsGroupsMarksItStale()
    {
        foreach (var group in new[] { TestGroup.D, TestGroup.B, TestGroup.C })
        {
            using var registry = new CacheRegistry<TestGroup>(TimeProvider.System);
            var cache = registry.Create<int>(LongTtl, TestGroup.D, TestGroup.B, TestGroup.C);
            var keyed = registry.CreateKeyed<string, int>(LongTtl, 5, TestGroup.D, TestGroup.B, TestGroup.C);
            await cache.GetAsync(_ => Task.FromResult(1));
            await keyed.GetAsync("k", _ => Task.FromResult(1));

            registry.Invalidate(group);

            Assert.Equal(2, await cache.GetAsync(_ => Task.FromResult(2)));
            Assert.Equal(2, await keyed.GetAsync("k", _ => Task.FromResult(2)));
        }
    }

    [Fact]
    public async Task Create_MultiGroup_UnrelatedGroupDoesNotTouchIt()
    {
        using var registry = new CacheRegistry<TestGroup>(TimeProvider.System);
        var cache = registry.Create<int>(LongTtl, TestGroup.D, TestGroup.B);
        await cache.GetAsync(_ => Task.FromResult(1));

        registry.Invalidate(TestGroup.A);

        Assert.Equal(1, await cache.GetAsync(_ => Task.FromResult(2)));
    }

    [Fact]
    public void Create_NoGroups_Throws()
    {
        using var registry = new CacheRegistry<TestGroup>(TimeProvider.System);

        Assert.Throws<ArgumentException>(() => registry.Create<int>(LongTtl));
        Assert.Throws<ArgumentException>(() => registry.CreateKeyed<string, int>(LongTtl, 5));
    }
}
