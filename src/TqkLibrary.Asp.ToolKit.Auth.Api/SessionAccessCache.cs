using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Short cache (singleton, <see cref="IMemoryCache"/>) of the per-request session lookup, keyed by session id.
    /// Invalidate right after the write succeeds instead of waiting for the TTL:
    /// <list type="bullet">
    ///   <item>sign-out / revoke one session → <see cref="InvalidateSession"/>;</item>
    ///   <item>password change, lock/delete account, role change of one user → <see cref="InvalidateUser"/>;</item>
    ///   <item>permissions of a role changed → <see cref="InvalidateAll"/>.</item>
    /// </list>
    /// Invalidation uses version numbers read BEFORE loading, so a lookup already in flight cannot store a stale
    /// result. A dead session (null) is never cached. With several API instances the cache is per instance: accept a
    /// delay up to the TTL or broadcast invalidations. Entries are set without <c>Size</c>: do not share a
    /// <see cref="MemoryCache"/> that has a <c>SizeLimit</c>.
    /// </summary>
    public sealed class SessionAccessCache
    {
        private const string SessionKeyPrefix = "tqk-auth:session:";
        private const string SessionVersionKeyPrefix = "tqk-auth:session-version:";
        private const string UserVersionKeyPrefix = "tqk-auth:user-version:";

        private sealed record Entry(SessionAccessRecord Access, long GlobalVersion, long UserVersion, long SessionVersion);

        private readonly IMemoryCache _cache;
        private readonly IOptions<ApiAuthOptions> _options;
        private readonly object _versionLock = new();
        private long _globalVersion;

        public SessionAccessCache(IMemoryCache cache, IOptions<ApiAuthOptions> options)
        {
            _cache = cache;
            _options = options;
        }

        /// <summary>The cached record of <paramref name="sessionId"/>, or the result of <paramref name="load"/> (cached when not null).</summary>
        /// <exception cref="InvalidOperationException"><paramref name="load"/> returned a record of another user.</exception>
        public async Task<SessionAccessRecord?> GetAsync(string sessionId, string userId,
            Func<CancellationToken, Task<SessionAccessRecord?>> load, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentException.ThrowIfNullOrEmpty(userId);
            ArgumentNullException.ThrowIfNull(load);

            var global = Interlocked.Read(ref _globalVersion);
            var user = ReadVersion(UserVersionKey(userId));
            var session = ReadVersion(SessionVersionKey(sessionId));
            if (_cache.TryGetValue(SessionKey(sessionId), out Entry? hit) && hit is not null
                && hit.GlobalVersion == global && hit.UserVersion == user && hit.SessionVersion == session
                && string.Equals(hit.Access.UserId, userId, StringComparison.Ordinal))
                return hit.Access;

            var access = await load(cancellationToken).ConfigureAwait(false);
            if (access is null)
            {
                _cache.Remove(SessionKey(sessionId));
                return null;
            }
            if (!string.Equals(access.UserId, userId, StringComparison.Ordinal))
            {
                _cache.Remove(SessionKey(sessionId));
                throw new InvalidOperationException("ISessionAccessSource returned a record of another user than the token's user.");
            }

            _cache.Set(SessionKey(sessionId), new Entry(access, global, user, session), _options.Value.SessionAccessCacheLifetime);
            return access;
        }

        /// <summary>Makes the cached lookup of one session stale.</summary>
        public void InvalidateSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            BumpVersion(SessionVersionKey(sessionId));
            _cache.Remove(SessionKey(sessionId));
        }

        /// <summary>Makes every cached lookup of <paramref name="userId"/> stale.</summary>
        public void InvalidateUser(string userId)
        {
            if (!string.IsNullOrEmpty(userId))
                BumpVersion(UserVersionKey(userId));
        }

        /// <summary>Makes every cached lookup stale.</summary>
        public void InvalidateAll() => Interlocked.Increment(ref _globalVersion);

        private long ReadVersion(string key) => _cache.TryGetValue(key, out long version) ? version : 0;

        private void BumpVersion(string key)
        {
            lock (_versionLock)
            {
                // Outlives the entry TTL many times so an old entry cannot "come back" when the version falls out.
                var lifetime = _options.Value.SessionAccessCacheLifetime;
                var keep = lifetime > TimeSpan.Zero ? lifetime * 120 : TimeSpan.FromHours(1);
                _cache.Set(key, ReadVersion(key) + 1, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = keep,
                    Priority = CacheItemPriority.NeverRemove,
                });
            }
        }

        private static string SessionKey(string sessionId) => SessionKeyPrefix + sessionId;

        private static string SessionVersionKey(string sessionId) => SessionVersionKeyPrefix + sessionId;

        private static string UserVersionKey(string userId) => UserVersionKeyPrefix + userId;
    }
}
