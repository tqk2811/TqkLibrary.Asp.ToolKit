namespace TqkLibrary.Asp.ToolKit.Caching.Remote
{
    /// <summary>Options of <see cref="HttpCacheInvalidationSender{TGroup}"/> (the sending side).</summary>
    public sealed class CacheInvalidationSenderOptions
    {
        /// <summary>Absolute URL of the receiving endpoint (for example <c>http://localhost:5000/_internal/cache/invalidate</c>).</summary>
        public string TargetUrl { get; set; } = string.Empty;

        /// <summary>Shared key sent in <see cref="KeyHeader"/>; must equal the receiver's key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Header carrying the shared key.</summary>
        public string KeyHeader { get; set; } = "X-Cache-Key";

        /// <summary>Invalidations within this window are merged into one call.</summary>
        public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>Timeout of one HTTP attempt.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Delays between retries of a failed call (transport error or non-success status other than 401/403/404).</summary>
        public IList<TimeSpan> RetryDelays { get; set; } = new List<TimeSpan>
        {
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        };

        /// <summary>Capacity of the pending queue; when full the oldest group is dropped (it still expires by TTL remotely).</summary>
        public int QueueCapacity { get; set; } = 256;

        /// <summary>True when both <see cref="TargetUrl"/> and <see cref="Key"/> are set.</summary>
        public bool IsEnabled => !string.IsNullOrWhiteSpace(TargetUrl) && !string.IsNullOrEmpty(Key);
    }
}
