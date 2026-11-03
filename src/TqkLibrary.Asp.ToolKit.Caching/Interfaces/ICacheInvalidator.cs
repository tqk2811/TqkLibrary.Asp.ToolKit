namespace TqkLibrary.Asp.ToolKit.Caching.Interfaces
{
    /// <summary>
    /// Group invalidation entry point for the service layer. Call it AFTER the source data has been saved/committed
    /// successfully.
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public interface ICacheInvalidator<TGroup> where TGroup : struct, Enum
    {
        /// <summary>Marks every cache that belongs to one of <paramref name="groups"/> as stale.</summary>
        void Invalidate(params TGroup[] groups);
    }
}
