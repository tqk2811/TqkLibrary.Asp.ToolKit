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
            => new()
            {
                UserId = userId, UserName = "user-" + version, Roles = ["Member"], Permissions = [version],
                SecretHash = "H", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            };

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

        private static async Task<SessionAccessRecord?> GetAccessAsync(SessionAccessCache cache, string sessionId,
            Func<CancellationToken, Task<SessionAccessRecord?>> load)
            => await cache.GetAsync(sessionId, load);

        [Fact]
        public async Task Hit_WithinLifetime_NoReload()
        {
            using var cache = NewCache();
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            loader.Value = () => Access("new");

            var next = await GetAccessAsync(cache, "s-1", loader.LoadAsync);

            Assert.Equal(1, loader.Calls);
            Assert.Equal(["old"], next!.Permissions);
        }

        [Fact]
        public async Task Expired_Reloads()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            loader.Value = () => Access("new");
            _clock.Advance(TimeSpan.FromSeconds(31));

            var next = await GetAccessAsync(cache, "s-1", loader.LoadAsync);

            Assert.Equal(2, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Fact]
        public async Task ZeroLifetime_NeverCaches()
        {
            using var cache = NewCache(TimeSpan.Zero);
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
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
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            loader.Value = () => Access("new");

            Invalidate(cache, kind);
            var next = await GetAccessAsync(cache, "s-1", loader.LoadAsync);

            Assert.Equal(2, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Theory]
        [MemberData(nameof(Invalidations))]
        public async Task InvalidateDuringLoad_InFlightResultIsNotServedLater(string kind)
        {
            using var cache = NewCache();
            var gate = new TaskCompletionSource<SessionAccessRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = GetAccessAsync(cache, "s-1", async _ => await gate.Task);

            Invalidate(cache, kind);
            gate.SetResult(Access("old"));
            Assert.Equal(["old"], (await inFlight)!.Permissions); // the caller that started the load still gets its result

            var loader = new Loader(() => Access("new"));
            var next = await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            Assert.Equal(1, loader.Calls);
            Assert.Equal(["new"], next!.Permissions);
        }

        [Fact]
        public async Task InvalidateSession_KeepsOtherSessionsCached()
        {
            using var cache = NewCache();
            await GetAccessAsync(cache, "s-1", new Loader(() => Access("a")).LoadAsync);
            await GetAccessAsync(cache, "s-2", new Loader(() => Access("b")).LoadAsync);

            cache.InvalidateSession("s-1");

            var other = new Loader(() => Access("reloaded"));
            var result = await GetAccessAsync(cache, "s-2", other.LoadAsync);
            Assert.Equal(0, other.Calls);
            Assert.Equal(["b"], result!.Permissions);

            var invalidated = new Loader(() => Access("new"));
            await GetAccessAsync(cache, "s-1", invalidated.LoadAsync);
            Assert.Equal(1, invalidated.Calls);
        }

        [Fact]
        public async Task InvalidateUser_KeepsOtherUsersCached_DropsEverySessionOfThatUser()
        {
            using var cache = NewCache();
            await GetAccessAsync(cache, "s-1", new Loader(() => Access("a")).LoadAsync);
            await GetAccessAsync(cache, "s-2", new Loader(() => Access("a")).LoadAsync);
            await GetAccessAsync(cache, "s-3", new Loader(() => Access("b", userId: "u-2")).LoadAsync);

            cache.InvalidateUser("u-1");

            var other = new Loader(() => Access("reloaded", userId: "u-2"));
            Assert.Equal(["b"], (await GetAccessAsync(cache, "s-3", other.LoadAsync))!.Permissions);
            Assert.Equal(0, other.Calls);

            var same = new Loader(() => Access("new"));
            await GetAccessAsync(cache, "s-1", same.LoadAsync);
            await GetAccessAsync(cache, "s-2", same.LoadAsync);
            Assert.Equal(2, same.Calls);
        }

        [Fact]
        public async Task LoadReturnsNull_NotFoundIsNotCached_NextCallSeesTheCreatedRecord()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var loader = new Loader(() => null);
            Assert.Null(await GetAccessAsync(cache, "s-1", loader.LoadAsync));

            loader.Value = () => Access("created");
            Assert.Equal(["created"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
            Assert.Equal(2, loader.Calls);
        }

        [Fact]
        public async Task InvalidateUser_DuringLoad_InFlightResultIsNotServedLater_OtherUserUnaffected()
        {
            using var cache = NewCache();
            await GetAccessAsync(cache, "s-9", new Loader(() => Access("b", userId: "u-2")).LoadAsync);
            var gate = new TaskCompletionSource<SessionAccessRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = GetAccessAsync(cache, "s-1", async _ => await gate.Task);

            cache.InvalidateUser("u-1");
            gate.SetResult(Access("old"));
            await inFlight;

            var loader = new Loader(() => Access("new"));
            Assert.Equal(["new"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
            var other = new Loader(() => Access("x", userId: "u-2"));
            Assert.Equal(["b"], (await GetAccessAsync(cache, "s-9", other.LoadAsync))!.Permissions);
            Assert.Equal(0, other.Calls);
        }

        [Fact]
        public async Task OldStamps_ArePruned_WithoutServingStaleEntries()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var gate = new TaskCompletionSource<SessionAccessRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = GetAccessAsync(cache, "s-1", async _ => await gate.Task);
            cache.InvalidateSession("s-1");

            // Much later another invalidation prunes the stamp of s-1; the hanging load then finishes.
            _clock.Advance(TimeSpan.FromMinutes(5));
            cache.InvalidateSession("s-other");
            gate.SetResult(Access("old"));
            await inFlight;

            var loader = new Loader(() => Access("new"));
            Assert.Equal(["new"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
            Assert.Equal(1, loader.Calls);
        }

        [Fact]
        public async Task TryUpdate_ReplacesCachedRecord_KeepsEntryExpiry()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            _clock.Advance(TimeSpan.FromSeconds(20));

            cache.TryUpdate("s-1", Access("touched"));
            Assert.Equal(["touched"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
            Assert.Equal(1, loader.Calls);

            _clock.Advance(TimeSpan.FromSeconds(11)); // 31 s after the load: the update did not extend the entry
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            Assert.Equal(2, loader.Calls);
        }

        [Theory]
        [MemberData(nameof(Invalidations))]
        public async Task TryUpdate_AfterInvalidation_DoesNotResurrectEntry(string kind)
        {
            using var cache = NewCache();
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);

            Invalidate(cache, kind);
            cache.TryUpdate("s-1", Access("touched"));
            loader.Value = () => Access("new");

            Assert.Equal(["new"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
            Assert.Equal(2, loader.Calls);
        }

        [Fact]
        public async Task TryUpdate_WithoutEntry_DoesNothing()
        {
            using var cache = NewCache();
            cache.TryUpdate("s-1", Access("touched"));
            var loader = new Loader(() => Access("loaded"));
            Assert.Equal(["loaded"], (await GetAccessAsync(cache, "s-1", loader.LoadAsync))!.Permissions);
        }

        [Fact]
        public void Invalidate_PrunesStampsOlderThanLifetime()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            cache.InvalidateSession("s-old");
            cache.InvalidateUser("u-old");
            Assert.Equal(2, cache.StampCount);

            _clock.Advance(TimeSpan.FromSeconds(40));
            cache.InvalidateSession("s-new");

            Assert.Equal(1, cache.StampCount);
        }

        [Fact]
        public async Task PrunedStamp_StillInvalidatesOlderEntry()
        {
            using var cache = NewCache(TimeSpan.FromSeconds(30));
            var loader = new Loader(() => Access("old"));
            await GetAccessAsync(cache, "s-1", loader.LoadAsync);
            cache.InvalidateSession("s-1");
            _clock.Advance(TimeSpan.FromSeconds(40));
            cache.InvalidateSession("s-other");          // prunes the s-1 stamp, raising the floor

            await GetAccessAsync(cache, "s-1", loader.LoadAsync);

            Assert.Equal(2, loader.Calls);
        }
    }
}
