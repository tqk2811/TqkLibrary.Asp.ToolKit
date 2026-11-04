using Microsoft.Extensions.Caching.Memory;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// DBSC: which sessions are bound to which device key, as last learned from the API (singleton). The API is the single
    /// source of truth for the key; this cache only spares a <c>me</c> call and lets enforcement work while <c>me</c> is
    /// unavailable or skipped. Filled from <c>me</c> results and right after a successful registration.
    /// <list type="bullet">
    ///   <item>Monotonic: an entry is never replaced or removed by the library (a key is registered once per session and
    ///   never changes), so once known, a session stays bound for this instance.</item>
    ///   <item>An entry lives until the session's absolute expiry (the cookie ticket's <c>ExpiresUtc</c>), at most
    ///   <see cref="MaxLifetime"/>.</item>
    ///   <item>Private, bounded <see cref="MemoryCache"/> (<see cref="SizeLimit"/> entries), independent of the app's
    ///   <see cref="IMemoryCache"/> and of its <c>SizeLimit</c>. An evicted entry is learned again from <c>me</c>.</item>
    /// </list>
    /// </summary>
    internal sealed class DeviceBindingCache : IDisposable
    {
        /// <summary>Upper bound of an entry's lifetime.</summary>
        internal static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

        /// <summary>Maximum number of sessions kept.</summary>
        internal const long SizeLimit = 100_000;

        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = SizeLimit });
        private readonly TimeProvider _clock;
        private readonly object _setLock = new();

        public DeviceBindingCache(TimeProvider clock) => _clock = clock;

        /// <summary>The known device key of <paramref name="sessionId"/>; null = unknown (not necessarily unbound).</summary>
        public DeviceKey? Get(string sessionId)
            => _cache.TryGetValue((object)sessionId, out var value) ? value as DeviceKey : null;

        /// <summary>
        /// Records that <paramref name="sessionId"/> is bound to <paramref name="key"/> until <paramref name="sessionExpiresUtc"/>
        /// (null or past = <see cref="MaxLifetime"/> / nothing). Keeps an existing entry (monotonic). Returns the key now
        /// cached for the session (the existing one when it was already known).
        /// </summary>
        public DeviceKey Set(string sessionId, DeviceKey key, DateTimeOffset? sessionExpiresUtc)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(key);
            var lifetime = sessionExpiresUtc is { } expires ? expires - _clock.GetUtcNow() : MaxLifetime;
            if (lifetime > MaxLifetime)
                lifetime = MaxLifetime;
            lock (_setLock)
            {
                if (Get(sessionId) is { } existing)
                    return existing;
                if (lifetime <= TimeSpan.Zero)
                    return key;
                _cache.Set(sessionId, key, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = 1 });
                return key;
            }
        }

        public void Dispose() => _cache.Dispose();
    }
}
