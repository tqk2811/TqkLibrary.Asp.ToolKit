using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Short cache (singleton) of the per-request session lookup, keyed by session id, so the API does not query the
    /// session, roles and permissions on EVERY authenticated request. A rejection is never cached. Name, email, role
    /// and permission changes therefore apply after at most <see cref="ApiAuthOptions.SessionAccessCacheLifetime"/>
    /// unless the app invalidates.
    /// <para>
    /// Invalidate right after the write succeeds instead of waiting for
    /// <see cref="ApiAuthOptions.SessionAccessCacheLifetime"/>:
    /// <list type="bullet">
    ///   <item><see cref="InvalidateSession"/>: one session revoked (sign-out); other sessions stay cached.</item>
    ///   <item><see cref="InvalidateUser"/>: every session of a user (password change, account deleted/locked, role or
    ///   name/email change); other users stay cached.</item>
    ///   <item><see cref="InvalidateAll"/>: permissions of a role changed, role renamed/deleted.</item>
    /// </list>
    /// A monotonic sequence number (a field, never evicted) is read before every load and stored in the entry; each
    /// invalidation stamps its target (session id, user id, or everything) with a NEW sequence number. An entry is
    /// served only when its load started after every stamp that applies to it, so a load in flight during an
    /// invalidation writes an entry that is already stale.
    /// </para>
    /// <para>
    /// Stamps older than the cache lifetime are pruned on an invalidation (at most once per lifetime, minimum 1 second); pruning raises a floor sequence first,
    /// so only entries whose load started before the pruned stamps (already expired, or loads hanging longer than the
    /// lifetime) are dropped: an old stamp can never stop applying.
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
        private sealed record Entry(SessionAccessRecord Access, DateTimeOffset ExpiresAt, long Sequence);

        private readonly record struct Stamp(long Sequence, DateTimeOffset At);

        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly ConcurrentDictionary<string, Stamp> _sessionStamps = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Stamp> _userStamps = new(StringComparer.Ordinal);
        private readonly object _pruneLock = new();
        private readonly TimeProvider _clock;
        private readonly IOptions<ApiAuthOptions> _options;
        private long _sequence;
        private long _allStamp;
        private long _floor;
        private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

        public SessionAccessCache(TimeProvider clock, IOptions<ApiAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentNullException.ThrowIfNull(options);
            _clock = clock;
            _options = options;
        }

        /// <summary>
        /// The cached record of <paramref name="sessionId"/> when still valid, otherwise the result of
        /// <paramref name="load"/> (cached when granted). The caller still checks the secret, revocation and expiry of the
        /// record on every request.
        /// </summary>
        public async Task<SessionAccessResult> GetAsync(string sessionId,
            Func<CancellationToken, Task<SessionAccessResult>> load, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(load);

            // Read the sequence BEFORE loading: an invalidation during the load stamps a higher number.
            var sequence = Interlocked.Read(ref _sequence);

            if (_cache.TryGetValue(sessionId, out Entry? hit) && hit is not null
                && hit.ExpiresAt > _clock.GetUtcNow()
                && IsCurrent(hit, sessionId))
            {
                return SessionAccessResult.Grant(hit.Access);
            }

            var result = await load(cancellationToken).ConfigureAwait(false) ?? SessionAccessResult.Reject();
            if (!result.IsGranted)
            {
                _cache.Remove(sessionId);
                return result;
            }

            var lifetime = _options.Value.SessionAccessCacheLifetime;
            if (lifetime > TimeSpan.Zero)
                _cache.Set(sessionId, new Entry(result.Access, _clock.GetUtcNow().Add(lifetime), sequence), lifetime);
            return result;
        }

        /// <summary>
        /// Replaces the cached record of <paramref name="sessionId"/> with <paramref name="access"/> (same user) after
        /// the app persisted it (sliding expiry touched), keeping the entry's expiry and load sequence: an entry that is
        /// missing, expired or invalidated is not recreated, and an invalidation racing with this call still wins.
        /// </summary>
        internal void TryUpdate(string sessionId, SessionAccessRecord access)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(access);
            if (!_cache.TryGetValue(sessionId, out Entry? hit) || hit is null
                || !string.Equals(hit.Access.UserId, access.UserId, StringComparison.Ordinal)
                || !IsCurrent(hit, sessionId))
                return;
            var remaining = hit.ExpiresAt - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return;
            _cache.Set(sessionId, hit with { Access = access }, remaining);
        }

        /// <summary>Drops the cached result of one session (session just revoked); other sessions stay cached.</summary>
        public void InvalidateSession(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            AddStamp(_sessionStamps, sessionId);
            _cache.Remove(sessionId);
        }

        /// <summary>Drops the cached results of every session of a user; other users stay cached.</summary>
        public void InvalidateUser(string userId)
        {
            ArgumentException.ThrowIfNullOrEmpty(userId);
            AddStamp(_userStamps, userId);
        }

        /// <summary>Drops every result.</summary>
        public void InvalidateAll()
        {
            var sequence = Interlocked.Increment(ref _sequence);
            InterlockedMax(ref _allStamp, sequence);
        }

        /// <summary>Number of invalidation stamps currently kept (session + user); for tests.</summary>
        internal int StampCount => _sessionStamps.Count + _userStamps.Count;

        /// <inheritdoc />
        public void Dispose() => _cache.Dispose();

        /// <summary>
        /// True when the entry's load started after every invalidation that applies to it. Stamps are read BEFORE the
        /// floor: pruning raises the floor before removing a stamp, so a stamp removed meanwhile is covered by the floor.
        /// </summary>
        private bool IsCurrent(Entry entry, string sessionId)
        {
            if (_sessionStamps.TryGetValue(sessionId, out var sessionStamp) && entry.Sequence < sessionStamp.Sequence)
                return false;
            if (_userStamps.TryGetValue(entry.Access.UserId, out var userStamp) && entry.Sequence < userStamp.Sequence)
                return false;
            return entry.Sequence >= Interlocked.Read(ref _allStamp) && entry.Sequence >= Interlocked.Read(ref _floor);
        }

        private void AddStamp(ConcurrentDictionary<string, Stamp> stamps, string key)
        {
            var now = _clock.GetUtcNow();
            var sequence = Interlocked.Increment(ref _sequence);
            stamps.AddOrUpdate(key, new Stamp(sequence, now),
                (_, old) => old.Sequence > sequence ? old : new Stamp(sequence, now));
            Prune(now);
        }

        /// <summary>
        /// Removes stamps older than the cache lifetime (raising the floor first). Runs at most once per
        /// max(lifetime, 1 second) so a burst of invalidations does not scan the stamps every time.
        /// </summary>
        private void Prune(DateTimeOffset now)
        {
            var lifetime = _options.Value.SessionAccessCacheLifetime;
            var interval = lifetime > TimeSpan.FromSeconds(1) ? lifetime : TimeSpan.FromSeconds(1);
            var cutoff = now - (lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero) - TimeSpan.FromSeconds(1);
            lock (_pruneLock)
            {
                if (now - _lastPrune < interval)
                    return;
                _lastPrune = now;
                PruneLocked(_sessionStamps, cutoff);
                PruneLocked(_userStamps, cutoff);
            }
        }

        private void PruneLocked(ConcurrentDictionary<string, Stamp> stamps, DateTimeOffset cutoff)
        {
            foreach (var pair in stamps)
            {
                if (pair.Value.At >= cutoff)
                    continue;
                InterlockedMax(ref _floor, pair.Value.Sequence);
                // Only removes the exact stamp seen: a newer stamp written meanwhile stays.
                stamps.TryRemove(pair);
            }
        }

        private static void InterlockedMax(ref long target, long value)
        {
            var current = Interlocked.Read(ref target);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref target, value, current);
                if (previous == current)
                    return;
                current = previous;
            }
        }
    }
}
