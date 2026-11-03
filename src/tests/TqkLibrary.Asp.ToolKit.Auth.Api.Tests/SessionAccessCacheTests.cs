using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    public class SessionAccessCacheTests
    {
        private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        private static SessionAccessRecord Access(string version, string userId = "u-1")
            => new() { UserId = userId, UserName = "user-" + version, Roles = ["Member"], Permissions = [version] };

        private SessionAccessCache NewCache(TimeSpan? lifetime = null)
            => new(_clock, Options.Create(new ApiAuthOptions { SessionAccessCacheLifetime = lifetime ?? TimeSpan.FromSeconds(30) }));

        private sealed class Loader(Func<SessionAccessRecord?> value)
        {
            public int Calls { get; private set; }
            public Func<SessionAccessRecord?> Value { get; set; } = value;

            public Task<SessionAccessRecord?> LoadAsync(CancellationToken _)
            {
                Calls++;
                return Task.FromResult(Value());
            }
        }

        [Fact]
        public async Task Hit_WithinLifetime_NoReload()
        {
            using var cache = NewCache();
            var loader = new Loader(() => Access("old"));
            await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            loader.Value = () => Access("new");

            var next = await cache.GetAsync("s-1", "u-1", loader.LoadAsync);

            Assert.Equal(1, loader.Calls);
            Assert.Equal(["old"], next!.Permissions);
        }

        [Fact]
        public async Task Expired_Reloads()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var loader = new Loader(() => Access("old"));
            await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            loader.Value = () => Access("new");
            _clock.Advance(TimeSpan.FromSeconds(31));

            var next = await cache.GetAsync("s-1", "u-1", loader.LoadAsync);

            Assert.Equal(2, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Fact]
        public async Task ZeroLifetime_NeverCaches()
        {
            using var cache = NewCache(TimeSpan.Zero);
            var loader = new Loader(() => Access("old"));
            await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            Assert.Equal(2, loader.Calls);
        }

        public static TheoryData<string> Invalidations => ["session", "user", "all"];

        private static void Invalidate(SessionAccessCache cache, string kind)
        {
            switch (kind)
            {
                case "session": cache.InvalidateSession("s-1"); break;
                case "user": cache.InvalidateUser("u-1"); break;
                default: cache.InvalidateAll(); break;
            }
        }

        [Theory]
        [MemberData(nameof(Invalidations))]
        public async Task Invalidate_NextReadReloads(string kind)
        {
            using var cache = NewCache();
            var loader = new Loader(() => Access("old"));
            await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            loader.Value = () => Access("new");

            Invalidate(cache, kind);
            var next = await cache.GetAsync("s-1", "u-1", loader.LoadAsync);

            Assert.Equal(2, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Theory]
        [MemberData(nameof(Invalidations))]
        public async Task InvalidateDuringLoad_InFlightResultIsNotServedLater(string kind)
        {
            using var cache = NewCache();
            var gate = new TaskCompletionSource<SessionAccessRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = cache.GetAsync("s-1", "u-1", _ => gate.Task);

            Invalidate(cache, kind);
            gate.SetResult(Access("old"));
            Assert.Equal(["old"], (await inFlight)!.Permissions); // the caller that started the load still gets its result

            var loader = new Loader(() => Access("new"));
            var next = await cache.GetAsync("s-1", "u-1", loader.LoadAsync);
            Assert.Equal(1, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Fact]
        public async Task LoadReturnsNull_ReturnsNull_AndIsNotCached()
        {
            using var cache = NewCache();
            var loader = new Loader(() => null);
            Assert.Null(await cache.GetAsync("s-1", "u-1", loader.LoadAsync));
            Assert.Null(await cache.GetAsync("s-1", "u-1", loader.LoadAsync));
            Assert.Equal(2, loader.Calls);
        }

        [Fact]
        public async Task RecordOfAnotherUser_TreatedAsDeadSession()
        {
            using var cache = NewCache();
            var loader = new Loader(() => Access("x", userId: "u-2"));
            Assert.Null(await cache.GetAsync("s-1", "u-1", loader.LoadAsync));
        }

        [Fact]
        public async Task CachedEntry_AskedForAnotherUser_Reloads()
        {
            using var cache = NewCache();
            await cache.GetAsync("s-1", "u-1", new Loader(() => Access("a")).LoadAsync);

            var other = new Loader(() => Access("b", userId: "u-2"));
            var result = await cache.GetAsync("s-1", "u-2", other.LoadAsync);

            Assert.Equal(1, other.Calls);
            Assert.Equal("u-2", result!.UserId);
        }
    }
}
