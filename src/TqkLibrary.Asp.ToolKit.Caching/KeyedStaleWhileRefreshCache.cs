using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>
    /// Keyed cache: one <see cref="StaleWhileRefreshCache{T}"/> per key (two different keys never wait on each other).
    /// Holds at most <c>maxEntries</c> keys; beyond that the least recently used key is dropped.
    /// <see cref="InvalidateAll"/> also stops in-flight loads from writing back (each inner cache has its own version).
    /// The <c>load</c> delegate must read its data inside the lambda for Invalidate to be safe.
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="T">Cached value type.</typeparam>
    public sealed class KeyedStaleWhileRefreshCache<TKey, T> : IInvalidatableCache
        where TKey : notnull
    {
        private sealed class Slot
        {
            public Slot(StaleWhileRefreshCache<T> cache) => Cache = cache;
            public StaleWhileRefreshCache<T> Cache { get; }
            public long LastUsed;
        }

        private readonly ConcurrentDictionary<TKey, Slot> _slots;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _ttl;
        private readonly int _maxEntries;
        private readonly ILogger? _logger;
        private long _tick;

        /// <param name="timeProvider">Clock.</param>
        /// <param name="ttl">How long each key's value is kept.</param>
        /// <param name="maxEntries">Maximum number of keys held (at least 1).</param>
        /// <param name="logger">Optional logger passed to each inner cache.</param>
        /// <param name="comparer">Optional key comparer (for example <see cref="StringComparer.OrdinalIgnoreCase"/>).</param>
        public KeyedStaleWhileRefreshCache(TimeProvider timeProvider, TimeSpan ttl, int maxEntries, ILogger? logger = null,
            IEqualityComparer<TKey>? comparer = null)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
            _slots = new ConcurrentDictionary<TKey, Slot>(comparer);
            _timeProvider = timeProvider;
            _ttl = ttl;
            _maxEntries = maxEntries;
            _logger = logger;
        }

        /// <summary>Number of keys currently held.</summary>
        public int Count => _slots.Count;

        /// <summary>The value of <paramref name="key"/>, fresh or loaded through <paramref name="load"/>.</summary>
        public Task<T> GetAsync(TKey key, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken = default)
            => GetSlot(key).Cache.GetAsync(load, cancellationToken);

        /// <summary>Same as <see cref="GetAsync"/> but <paramref name="load"/> decides how long to keep the value (null = do not keep).</summary>
        public Task<T> GetWithKeepAsync(TKey key, Func<CancellationToken, Task<(T Value, TimeSpan? KeepFor)>> load,
            CancellationToken cancellationToken = default)
            => GetSlot(key).Cache.GetWithKeepAsync(load, cancellationToken);

        /// <summary>Marks one key as stale (ignored when the key is not held).</summary>
        public void Invalidate(TKey key)
        {
            if (_slots.TryGetValue(key, out var slot))
                slot.Cache.Invalidate();
        }

        /// <summary>Marks every held key as stale.</summary>
        public void InvalidateAll()
        {
            foreach (var slot in _slots.Values)
                slot.Cache.Invalidate();
        }

        void IInvalidatableCache.Invalidate() => InvalidateAll();

        private Slot GetSlot(TKey key)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                if (_slots.Count >= _maxEntries)
                    EvictOldest();
                // LastUsed is set inside the factory so a new slot is not seen as the oldest (0) and evicted at once.
                slot = _slots.GetOrAdd(key, _ => new Slot(new StaleWhileRefreshCache<T>(_timeProvider, _ttl, _logger))
                {
                    LastUsed = Interlocked.Increment(ref _tick),
                });
            }
            Interlocked.Exchange(ref slot.LastUsed, Interlocked.Increment(ref _tick));
            return slot;
        }

        /// <summary>Drops the least recently used key. A load in flight for it still completes; its result is just not kept.</summary>
        private void EvictOldest()
        {
            TKey? oldestKey = default;
            var oldest = long.MaxValue;
            var found = false;
            foreach (var pair in _slots)
            {
                var lastUsed = Interlocked.Read(ref pair.Value.LastUsed);
                if (lastUsed < oldest)
                {
                    oldest = lastUsed;
                    oldestKey = pair.Key;
                    found = true;
                }
            }
            if (found)
                _slots.TryRemove(oldestKey!, out _);
        }
    }
}
