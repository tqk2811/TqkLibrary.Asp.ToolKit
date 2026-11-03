using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>No-op invalidator: default for services built outside DI (tests, tools) that have no cache to clear.</summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class NullCacheInvalidator<TGroup> : ICacheInvalidator<TGroup> where TGroup : struct, Enum
    {
        /// <summary>Shared instance.</summary>
        public static readonly NullCacheInvalidator<TGroup> Instance = new();

        private NullCacheInvalidator() { }

        /// <inheritdoc />
        public void Invalidate(params TGroup[] groups) { }
    }
}
