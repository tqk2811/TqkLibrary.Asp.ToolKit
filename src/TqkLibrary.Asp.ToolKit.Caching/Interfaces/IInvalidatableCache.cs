namespace TqkLibrary.Asp.ToolKit.Caching.Interfaces
{
    /// <summary>A cache that <see cref="CacheRegistry{TGroup}"/> can invalidate by group.</summary>
    public interface IInvalidatableCache
    {
        /// <summary>
        /// Marks every remembered value as stale: the next read waits for a fresh value, and a load that is already
        /// in flight is not allowed to write its (possibly old) result back.
        /// </summary>
        void Invalidate();
    }
}
