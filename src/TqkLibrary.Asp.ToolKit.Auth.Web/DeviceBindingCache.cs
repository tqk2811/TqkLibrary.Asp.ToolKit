using Microsoft.Extensions.Caching.Memory;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// DBSC: which sessions are bound to which device key, as last learned from the API (singleton). The API is the single
    /// source of truth for the key; this cache only spares a <c>me</c> call and lets enforcement work while <c>me</c> is
    /// unavailable or skipped. Filled from <c>me</c> results and right after a successful registration.
    /// <list type="bullet">
    ///   <item>Bound entries are monotonic: never replaced or removed by the library (a key is registered once per session
    ///   and never changes), so once known, a session stays bound for this instance. An entry lives until the session's
    ///   absolute expiry (the cookie ticket's <c>ExpiresUtc</c>), at most <see cref="MaxLifetime"/>.</item>
    ///   <item>Unbound entries (<see cref="MarkUnbound"/>) are a short negative cache: a FRESH <c>me</c> answer "not bound"
    ///   is trusted only for <see cref="DeviceBoundSessionOptions.UnboundRecheckInterval"/>, then the binding is unknown
    ///   again and must be learned from <c>me</c>. A bound entry replaces an unbound one, never the other way round.</item>
    ///   <item>Private, bounded <see cref="MemoryCache"/> (<see cref="SizeLimit"/> entries), independent of the app's
    ///   <see cref="IMemoryCache"/> and of its <c>SizeLimit</c>. An evicted entry is learned again from <c>me</c>. Expiry is
    ///   checked against the injected <see cref="TimeProvider"/>.</item>
    /// </list>
    /// </summary>
    internal sealed class DeviceBindingCache : IDisposable
    {
        /// <summary>Upper bound of an entry's lifetime.</summary>
        internal static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

        /// <summary>Maximum number of sessions kept.</summary>
        internal const long SizeLimit = 100_000;

        /// <summary>One session: <see cref="Key"/> null = known NOT bound (negative entry).</summary>
        private sealed record Entry(DeviceKey? Key, DateTimeOffset ExpiresAt);

        private readonly MemoryCache _cache;
        private readonly TimeProvider _clock;
        private readonly object _setLock = new();

        public DeviceBindingCache(TimeProvider clock) : this(clock, SizeLimit)
        {
        }

        /// <summary>Tests: a smaller <paramref name="sizeLimit"/>.</summary>
        internal DeviceBindingCache(TimeProvider clock, long sizeLimit)
        {
            _clock = clock;
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimit });
        }

        /// <summary>The known device key of <paramref name="sessionId"/>; null = unknown or known not bound.</summary>
        public DeviceKey? Get(string sessionId) => GetEntry(sessionId)?.Key;

        /// <summary>
        /// True while a fresh <c>me</c> answer "not bound" for <paramref name="sessionId"/> is still trusted
        /// (<see cref="MarkUnbound"/>).
        /// </summary>
        public bool IsKnownUnbound(string sessionId) => GetEntry(sessionId) is { Key: null };

        /// <summary>
        /// Records that <paramref name="sessionId"/> is bound to <paramref name="key"/> until <paramref name="sessionExpiresUtc"/>
        /// (null = <see cref="MaxLifetime"/>; past = nothing is stored). Keeps an existing bound entry (monotonic) and
        /// replaces an unbound one. Returns the key now known for the session (the existing one when it was already
        /// known), also when the cache is full and nothing could be stored.
        /// </summary>
        public DeviceKey Set(string sessionId, DeviceKey key, DateTimeOffset? sessionExpiresUtc)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(key);
            var now = _clock.GetUtcNow();
            var lifetime = sessionExpiresUtc is { } expires ? expires - now : MaxLifetime;
            if (lifetime > MaxLifetime)
                lifetime = MaxLifetime;
            lock (_setLock)
            {
                if (Get(sessionId) is { } existing)
                    return existing;
                if (lifetime <= TimeSpan.Zero)
                    return key;
                Store(sessionId, new Entry(key, now + lifetime), lifetime);
                return key;
            }
        }

        /// <summary>
        /// Records that a FRESH <c>me</c> reported <paramref name="sessionId"/> as not bound, trusted for
        /// <paramref name="lifetime"/>. Does nothing when the session is known to be bound (a bound entry wins).
        /// </summary>
        public void MarkUnbound(string sessionId, TimeSpan lifetime)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            if (lifetime <= TimeSpan.Zero)
                return;
            lock (_setLock)
            {
                if (Get(sessionId) is not null)
                    return;
                Store(sessionId, new Entry(null, _clock.GetUtcNow() + lifetime), lifetime);
            }
        }

        private Entry? GetEntry(string sessionId)
            => _cache.TryGetValue((object)sessionId, out var value) && value is Entry entry && entry.ExpiresAt > _clock.GetUtcNow()
                ? entry
                : null;

        // The MemoryCache expiry (system clock) only evicts; validity is decided by Entry.ExpiresAt (TimeProvider).
        private void Store(string sessionId, Entry entry, TimeSpan lifetime)
            => _cache.Set(sessionId, entry, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = 1 });

        public void Dispose() => _cache.Dispose();
    }
}
