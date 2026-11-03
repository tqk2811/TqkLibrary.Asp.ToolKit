namespace TqkLibrary.Asp.ToolKit.Caching.Interfaces
{
    /// <summary>
    /// Something that wants to know when cache groups were invalidated (for example to forward the signal to another
    /// process).
    /// <para><b>Contract:</b> <see cref="OnInvalidated"/> runs on the request thread that performed the write, so it
    /// may ONLY enqueue work (queue/Channel) and return; no network or blocking IO, and it must not throw.</para>
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public interface ICacheInvalidationNotifier<TGroup> where TGroup : struct, Enum
    {
        /// <summary>Called after the local registry has invalidated the groups.</summary>
        void OnInvalidated(IReadOnlyList<TGroup> groups);
    }
}
