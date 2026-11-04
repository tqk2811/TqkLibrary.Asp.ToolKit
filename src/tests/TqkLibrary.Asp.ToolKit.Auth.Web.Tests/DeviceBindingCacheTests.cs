using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>The per-instance DBSC binding cache: monotonic bound entries, short negative entries, bounded size.</summary>
    public class DeviceBindingCacheTests
    {
        private const string Sid = "s-1";

        private static DeviceKey NewKey()
        {
            using var device = DbscTestDevice.Es256();
            return DeviceKey.TryParse(device.Jwk().ToJsonString(), "ES256", 2048)!;
        }

        [Fact]
        public void Set_ExistingEntry_IsNotReplaced()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);
            var first = NewKey();
            var second = NewKey();

            Assert.Same(first, cache.Set(Sid, first, clock.GetUtcNow().AddDays(1)));
            Assert.Same(first, cache.Set(Sid, second, clock.GetUtcNow().AddDays(1)));
            Assert.Same(first, cache.Get(Sid));
        }

        [Fact]
        public void Entry_ExpiresAtTheSessionExpiry()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);
            var key = NewKey();
            cache.Set(Sid, key, clock.GetUtcNow().AddHours(1));

            clock.Advance(TimeSpan.FromMinutes(59));
            Assert.Same(key, cache.Get(Sid));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(cache.Get(Sid));
            // Gone: a new key can be learned.
            var other = NewKey();
            Assert.Same(other, cache.Set(Sid, other, clock.GetUtcNow().AddHours(1)));
        }

        [Fact]
        public void Entry_LongSessionExpiry_CappedAtMaxLifetime()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);
            cache.Set(Sid, NewKey(), clock.GetUtcNow().AddDays(365));

            clock.Advance(DeviceBindingCache.MaxLifetime - TimeSpan.FromMinutes(1));
            Assert.NotNull(cache.Get(Sid));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(cache.Get(Sid));
        }

        [Fact]
        public void Set_PastExpiry_StoresNothing_ReturnsTheKey()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);
            var key = NewKey();

            Assert.Same(key, cache.Set(Sid, key, clock.GetUtcNow()));
            Assert.Same(key, cache.Set(Sid, key, clock.GetUtcNow().AddSeconds(-1)));
            Assert.Null(cache.Get(Sid));
        }

        [Fact]
        public void Set_FullCache_StillReturnsTheKey()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock, sizeLimit: 1);
            var expires = clock.GetUtcNow().AddDays(1);
            cache.Set("s-0", NewKey(), expires);

            var key = NewKey();
            Assert.Same(key, cache.Set(Sid, key, expires));
        }

        [Fact]
        public void MarkUnbound_ExpiresAfterItsLifetime()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);

            cache.MarkUnbound(Sid, TimeSpan.FromSeconds(30));
            Assert.True(cache.IsKnownUnbound(Sid));
            Assert.Null(cache.Get(Sid));
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.False(cache.IsKnownUnbound(Sid));
        }

        [Fact]
        public void BoundEntry_ReplacesUnbound_ButNeverTheOtherWayRound()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cache = new DeviceBindingCache(clock);
            var key = NewKey();

            cache.MarkUnbound(Sid, TimeSpan.FromSeconds(30));
            Assert.Same(key, cache.Set(Sid, key, clock.GetUtcNow().AddDays(1)));
            Assert.False(cache.IsKnownUnbound(Sid));

            cache.MarkUnbound(Sid, TimeSpan.FromSeconds(30));
            Assert.Same(key, cache.Get(Sid));
            Assert.False(cache.IsKnownUnbound(Sid));
        }
    }
}
