using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>
    /// Registry of caches by group (singleton): every cache created here can be invalidated by group when its source
    /// data changes.
    /// <para>Call <c>Create</c> / <c>CreateKeyed</c> ONCE while building a singleton (constructor, field): the
    /// registry keeps a reference forever, so calling them per request leaks memory.</para>
    /// <para>Disposing the registry disposes the caches it created; caches passed to <see cref="Attach"/> stay with
    /// their owner.</para>
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class CacheRegistry<TGroup> : IDisposable where TGroup : struct, Enum
    {
        private const int WarnGroupSize = 100;

        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private readonly Dictionary<TGroup, List<IInvalidatableCache>> _groups = new();
        private readonly List<IDisposable> _owned = [];

        /// <summary>Creates the registry.</summary>
        /// <param name="timeProvider">Clock handed to every cache created through this registry.</param>
        /// <param name="loggerFactory">Optional; used for registry and cache warnings.</param>
        public CacheRegistry(TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            _timeProvider = timeProvider;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CacheRegistry<TGroup>>();
        }

        /// <summary>
        /// Creates a single-value cache attached to one or more groups: invalidating ANY of
        /// <paramref name="groups"/> marks it stale. Use several groups when the value is built from several sources.
        /// </summary>
        public StaleWhileRefreshCache<T> Create<T>(TimeSpan ttl, params TGroup[] groups)
        {
            EnsureGroups(groups);
            var cache = new StaleWhileRefreshCache<T>(_timeProvider, ttl, _logger);
            Register(groups, cache);
            return cache;
        }

        /// <summary>Like <see cref="Create{T}"/> but keyed, holding at most <paramref name="maxEntries"/> keys.</summary>
        public KeyedStaleWhileRefreshCache<TKey, T> CreateKeyed<TKey, T>(TimeSpan ttl, int maxEntries, params TGroup[] groups)
            where TKey : notnull
            => CreateKeyed<TKey, T>(ttl, maxEntries, null, groups);

        /// <summary>Like <see cref="CreateKeyed{TKey, T}(TimeSpan, int, TGroup[])"/> with a custom key comparer.</summary>
        public KeyedStaleWhileRefreshCache<TKey, T> CreateKeyed<TKey, T>(TimeSpan ttl, int maxEntries,
            IEqualityComparer<TKey>? comparer, params TGroup[] groups)
            where TKey : notnull
        {
            EnsureGroups(groups);
            var cache = new KeyedStaleWhileRefreshCache<TKey, T>(_timeProvider, ttl, maxEntries, _logger, comparer);
            Register(groups, cache);
            lock (_gate)
                _owned.Add(cache);
            return cache;
        }

        /// <summary>Disposes every cache created through <c>CreateKeyed</c>.</summary>
        public void Dispose()
        {
            IDisposable[] owned;
            lock (_gate)
            {
                owned = _owned.ToArray();
                _owned.Clear();
            }
            foreach (var cache in owned)
                cache.Dispose();
        }

        /// <summary>Attaches an existing cache (for example a custom <see cref="IInvalidatableCache"/>) to groups.</summary>
        public void Attach(IInvalidatableCache cache, params TGroup[] groups)
        {
            ArgumentNullException.ThrowIfNull(cache);
            EnsureGroups(groups);
            Register(groups, cache);
        }

        /// <summary>Invalidates every cache of the given groups; other groups are untouched.</summary>
        public void Invalidate(params TGroup[] groups)
        {
            ArgumentNullException.ThrowIfNull(groups);
            var seen = new HashSet<IInvalidatableCache>(ReferenceEqualityComparer.Instance);
            foreach (var group in groups)
            {
                IInvalidatableCache[] caches;
                lock (_gate)
                    caches = _groups.TryGetValue(group, out var list) ? list.ToArray() : [];
                foreach (var cache in caches)
                {
                    if (seen.Add(cache))
                        cache.Invalidate();
                }
            }
        }

        /// <summary>Invalidates every cache of every group.</summary>
        public void InvalidateAll()
        {
            TGroup[] groups;
            lock (_gate)
                groups = _groups.Keys.ToArray();
            Invalidate(groups);
        }

        private static void EnsureGroups(TGroup[] groups)
        {
            if (groups is null || groups.Length == 0)
                throw new ArgumentException("At least one cache group is required.", nameof(groups));
        }

        private void Register(TGroup[] groups, IInvalidatableCache cache)
        {
            lock (_gate)
            {
                foreach (var group in groups.Distinct())
                {
                    if (!_groups.TryGetValue(group, out var list))
                        _groups[group] = list = [];
                    list.Add(cache);
                    if (list.Count == WarnGroupSize + 1)
                        _logger.LogWarning("Cache group {Group} holds more than {Max} caches; are Create/CreateKeyed being called per request?",
                            group, WarnGroupSize);
                }
            }
        }
    }
}
