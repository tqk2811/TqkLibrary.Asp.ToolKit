using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>
    /// Singleton: invalidates the groups in <see cref="CacheRegistry{TGroup}"/> first, then tells every
    /// <see cref="ICacheInvalidationNotifier{TGroup}"/>. A failing notifier only logs a warning; it never fails the
    /// write that already succeeded.
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class CacheInvalidator<TGroup> : ICacheInvalidator<TGroup> where TGroup : struct, Enum
    {
        private readonly CacheRegistry<TGroup> _registry;
        private readonly ICacheInvalidationNotifier<TGroup>[] _notifiers;
        private readonly ILogger _logger;

        /// <summary>Creates the invalidator.</summary>
        /// <param name="registry">Registry whose caches are invalidated locally.</param>
        /// <param name="notifiers">Notifiers told after the local invalidation (may be empty).</param>
        /// <param name="loggerFactory">Optional; used to log notifier failures.</param>
        public CacheInvalidator(CacheRegistry<TGroup> registry, IEnumerable<ICacheInvalidationNotifier<TGroup>> notifiers,
            ILoggerFactory? loggerFactory = null)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(notifiers);
            _registry = registry;
            _notifiers = notifiers.ToArray();
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CacheInvalidator<TGroup>>();
        }

        /// <inheritdoc />
        public void Invalidate(params TGroup[] groups)
        {
            if (groups is null || groups.Length == 0)
                return;
            _registry.Invalidate(groups);
            foreach (var notifier in _notifiers)
            {
                try { notifier.OnInvalidated(groups); }
                catch (Exception ex) { _logger.LogWarning(ex, "Cache invalidation notifier {Notifier} failed", notifier.GetType().Name); }
            }
        }
    }
}
