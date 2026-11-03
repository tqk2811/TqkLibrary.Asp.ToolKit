using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>
    /// Keyed cache: one <see cref="StaleWhileRefreshCache{T}"/> per key (two different keys never wait on each other).
    /// <list type="bullet">
    ///   <item>Keys live in a private <see cref="MemoryCache"/> with <c>SizeLimit = maxEntries</c> (each key counts 1):
    ///   when full, <see cref="MemoryCache"/> compacts in the background, dropping the least recently used keys.</item>
    ///   <item>A key that is not held yet goes through a side table of in-flight loads first, so concurrent callers of
    ///   the same new key share ONE load even while the cache is full. The key is moved into the cache once its first
    ///   load ends; when the cache is full that move can be refused until compaction frees room, and the next call
    ///   for that key loads again (the result is still returned, only not kept).</item>
    ///   <item><see cref="Invalidate(TKey)"/> and <see cref="InvalidateAll"/> drop the keys and stop in-flight loads
    ///   from writing back (each inner cache has its own version).</item>
    ///   <item>The <c>load</c> delegate must read its data inside the lambda for Invalidate to be safe.</item>
    /// </list>
    /// Owns its <see cref="MemoryCache"/>: dispose this cache to release it.
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="T">Cached value type.</typeparam>
    public sealed class KeyedStaleWhileRefreshCache<TKey, T> : IInvalidatableCache, IDisposable
        where TKey : notnull
    {
        /// <summary>Wraps the key so <see cref="MemoryCache"/> compares it with the configured comparer.</summary>
        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            public CacheKey(TKey key, IEqualityComparer<TKey> comparer)
            {
                Key = key;
                Comparer = comparer;
            }

            public TKey Key { get; }
            public IEqualityComparer<TKey> Comparer { get; }

            public bool Equals(CacheKey other) => Comparer.Equals(Key, other.Key);
            public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);
            public override int GetHashCode() => Comparer.GetHashCode(Key);
        }

        private readonly MemoryCache _cache;
        private readonly ConcurrentDictionary<TKey, StaleWhileRefreshCache<T>> _pending;
        private readonly IEqualityComparer<TKey> _comparer;
        private readonly bool _customComparer;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _ttl;
        private readonly ILogger? _logger;
        private readonly TimeSpan? _failureHoldFor;
        private readonly TimeSpan? _maxStaleBackoff;
        private CancellationTokenSource _generation = new();

        /// <param name="timeProvider">Clock.</param>
        /// <param name="ttl">How long each key's value is kept.</param>
        /// <param name="maxEntries">Maximum number of keys held (at least 1).</param>
        /// <param name="logger">Optional logger passed to each inner cache.</param>
        /// <param name="comparer">Optional key comparer (for example <see cref="StringComparer.OrdinalIgnoreCase"/>).</param>
        /// <param name="failureHoldFor">Passed to each inner cache (default <see cref="StaleWhileRefreshCache{T}.FailureHoldFor"/>).</param>
        /// <param name="maxStaleBackoff">Passed to each inner cache (default <see cref="StaleWhileRefreshCache{T}.MaxStaleBackoff"/>).</param>
        public KeyedStaleWhileRefreshCache(TimeProvider timeProvider, TimeSpan ttl, int maxEntries, ILogger? logger = null,
            IEqualityComparer<TKey>? comparer = null, TimeSpan? failureHoldFor = null, TimeSpan? maxStaleBackoff = null)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            _customComparer = !ReferenceEquals(_comparer, EqualityComparer<TKey>.Default);
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = maxEntries,
                // Compaction drops (long)(count * percentage) entries: the default 5% is 0 below 20 keys and a full
                // cache would never take a new key again. 1.5 / maxEntries guarantees at least one.
                CompactionPercentage = Math.Clamp(1.5 / maxEntries, 0.05, 1.0),
            });
            _pending = new ConcurrentDictionary<TKey, StaleWhileRefreshCache<T>>(_comparer);
            _timeProvider = timeProvider;
            _ttl = ttl;
            _logger = logger;
            _failureHoldFor = failureHoldFor;
            _maxStaleBackoff = maxStaleBackoff;
        }

        /// <summary>Number of keys currently held (keys with a first load still in flight are not counted).</summary>
        public int Count => _cache.Count;

        /// <summary>The value of <paramref name="key"/>, fresh or loaded through <paramref name="load"/>.</summary>
        public Task<T> GetAsync(TKey key, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(load);
            return _cache.TryGetValue(ToCacheKey(key), out StaleWhileRefreshCache<T>? slot)
                ? slot!.GetAsync(load, cancellationToken)
                : GetNewAsync(key, slot => slot.GetAsync(load, cancellationToken));
        }

        /// <summary>Same as <see cref="GetAsync"/> but <paramref name="load"/> decides how long to keep the value (null = do not keep).</summary>
        public Task<T> GetWithKeepAsync(TKey key, Func<CancellationToken, Task<(T Value, TimeSpan? KeepFor)>> load,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(load);
            return _cache.TryGetValue(ToCacheKey(key), out StaleWhileRefreshCache<T>? slot)
                ? slot!.GetWithKeepAsync(load, cancellationToken)
                : GetNewAsync(key, slot => slot.GetWithKeepAsync(load, cancellationToken));
        }

        /// <summary>Drops one key (ignored when the key is not held); a load in flight for it cannot write back.</summary>
        public void Invalidate(TKey key)
        {
            // Pending first: Promote stores in the cache BEFORE leaving _pending, so a slot missed here is already in the cache.
            if (_pending.TryGetValue(key, out var pending))
                pending.Invalidate();
            var cacheKey = ToCacheKey(key);
            if (_cache.TryGetValue(cacheKey, out StaleWhileRefreshCache<T>? slot))
                slot!.Invalidate();
            _cache.Remove(cacheKey);
        }

        /// <summary>Drops every held key; loads in flight cannot write back.</summary>
        public void InvalidateAll()
        {
            // Every entry carries the token of the generation it was stored in: cancelling it expires them all at once.
            // The old source is not disposed: an entry being stored right now may still read its token.
            var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
            old.Cancel();
            foreach (var pending in _pending.Values)
                pending.Invalidate();
        }

        void IInvalidatableCache.Invalidate() => InvalidateAll();

        /// <inheritdoc />
        public void Dispose() => _cache.Dispose();

        /// <summary>The key itself with the default comparer (no wrapper to allocate); otherwise a <see cref="CacheKey"/>.</summary>
        private object ToCacheKey(TKey key) => _customComparer ? new CacheKey(key, _comparer) : key;

        /// <summary>
        /// Key not held: every caller of the same key meets on one slot in <see cref="_pending"/>, so the first load
        /// runs once. When it ends (value, error or cancel) the slot moves into the cache, keeping a remembered error
        /// for callers that come right after.
        /// </summary>
        private async Task<T> GetNewAsync(TKey key, Func<StaleWhileRefreshCache<T>, Task<T>> get)
        {
            var slot = _pending.GetOrAdd(key, _ => new StaleWhileRefreshCache<T>(_timeProvider, _ttl, _logger, _failureHoldFor, _maxStaleBackoff));
            try
            {
                return await get(slot).ConfigureAwait(false);
            }
            finally
            {
                Promote(key, slot);
            }
        }

        /// <summary>Stores the slot in the cache BEFORE removing it from <see cref="_pending"/>, so no caller sees neither.</summary>
        private void Promote(TKey key, StaleWhileRefreshCache<T> slot)
        {
            // Another caller of the same slot already moved it.
            if (!_pending.TryGetValue(key, out var current) || !ReferenceEquals(current, slot))
                return;
            var options = new MemoryCacheEntryOptions { Size = 1 };
            options.ExpirationTokens.Add(new CancellationChangeToken(Volatile.Read(ref _generation).Token));
            // Refused when full: the slot is dropped and the next call loads again.
            _cache.Set(ToCacheKey(key), slot, options);
            _pending.TryRemove(new KeyValuePair<TKey, StaleWhileRefreshCache<T>>(key, slot));
        }
    }
}
