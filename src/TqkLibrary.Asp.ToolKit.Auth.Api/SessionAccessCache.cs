using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Short cache (singleton) of the per-request session lookup, keyed by session id, so the API does not query the
    /// session, roles and permissions on EVERY authenticated request. A dead session (null) is never cached.
    /// <para>
    /// Invalidate right after the write succeeds instead of waiting for
    /// <see cref="ApiAuthOptions.SessionAccessCacheLifetime"/>:
    /// <list type="bullet">
    ///   <item><see cref="InvalidateSession"/>: one session revoked (sign-out).</item>
    ///   <item><see cref="InvalidateUser"/>: every session of a user (password change, account deleted/locked, role or
    ///   name/email change).</item>
    ///   <item><see cref="InvalidateAll"/>: permissions of a role changed, role renamed/deleted.</item>
    /// </list>
    /// All three increment ONE generation counter (a field: monotonic, never evicted) and every entry stores the
    /// generation read before its load started, so a load in flight during an invalidation writes an entry that is
    /// already stale. Writes are rare, so dropping a few seconds of cache on each one costs almost nothing, and no
    /// evicted version number can ever "match again". The three names stay separate to state intent at call sites
    /// (and allow narrowing the scope later).
    /// </para>
    /// <para>
    /// Uses its OWN <see cref="MemoryCache"/>, not the app's shared <see cref="IMemoryCache"/>: if the shared cache had a
    /// <c>SizeLimit</c>, a <c>Set</c> without <c>Size</c> would throw and break authentication of every request.
    /// </para>
    /// <para>
    /// Per process: with several API instances an invalidation only reaches the instance that handled the write;
    /// others lag up to the lifetime (or add a shared channel such as Redis pub/sub).
    /// </para>
    /// </summary>
    public sealed class SessionAccessCache : IDisposable
    {
        private sealed record Entry(SessionAccessRecord Access, DateTimeOffset ExpiresAt, long Generation);

        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly TimeProvider _clock;
        private readonly IOptions<ApiAuthOptions> _options;
        private long _generation;

        public SessionAccessCache(TimeProvider clock, IOptions<ApiAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentNullException.ThrowIfNull(options);
            _clock = clock;
            _options = options;
        }

        /// <summary>The cached record of <paramref name="sessionId"/> when still valid, otherwise the result of <paramref name="load"/> (cached when not null).</summary>
        public async Task<SessionAccessRecord?> GetAsync(string sessionId, string userId,
            Func<CancellationToken, Task<SessionAccessRecord?>> load, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentException.ThrowIfNullOrEmpty(userId);
            ArgumentNullException.ThrowIfNull(load);

            // Read the generation BEFORE loading: an invalidation during the load makes the stored entry stale at once.
            var generation = Interlocked.Read(ref _generation);

            if (_cache.TryGetValue(sessionId, out Entry? hit) && hit is not null
                && string.Equals(hit.Access.UserId, userId, StringComparison.Ordinal)
                && hit.Generation == generation
                && hit.ExpiresAt > _clock.GetUtcNow())
            {
                return hit.Access;
            }

            var access = await load(cancellationToken).ConfigureAwait(false);
            if (access is null || !string.Equals(access.UserId, userId, StringComparison.Ordinal))
            {
                _cache.Remove(sessionId);
                // A record of another user is treated as a dead session.
                return null;
            }

            var lifetime = _options.Value.SessionAccessCacheLifetime;
            if (lifetime > TimeSpan.Zero)
                _cache.Set(sessionId, new Entry(access, _clock.GetUtcNow().Add(lifetime), generation), lifetime);
            return access;
        }

        /// <summary>Drops the result of one session (session just revoked).</summary>
        public void InvalidateSession(string sessionId) => InvalidateAll();

        /// <summary>Drops the results of every session of a user.</summary>
        public void InvalidateUser(string userId) => InvalidateAll();

        /// <summary>Drops every result.</summary>
        public void InvalidateAll() => Interlocked.Increment(ref _generation);

        /// <inheritdoc />
        public void Dispose() => _cache.Dispose();
    }
}
