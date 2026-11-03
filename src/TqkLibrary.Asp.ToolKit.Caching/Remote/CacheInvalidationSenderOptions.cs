using Microsoft.Extensions.Logging;

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

        /// <summary>
        /// Delays between retries of a failed call (transport error or non-success status other than 401/403/404).
        /// <see langword="null"/> (the default) means <see cref="DefaultRetryDelays"/>. The default lives outside the
        /// property on purpose: the configuration binder APPENDS configured elements to any non-null collection or array
        /// (verified for arrays, <c>IList</c>, <c>IReadOnlyList</c> and <c>IEnumerable</c>), so a property pre-filled with
        /// defaults can never be replaced from configuration. A configured list therefore replaces the defaults.
        /// To disable retries set an empty array in code (<c>o.RetryDelays = [];</c>); the binder ignores an empty
        /// configuration section, so configuration alone cannot express that. Read the effective value through
        /// <see cref="EffectiveRetryDelays"/>.
        /// </summary>
        public TimeSpan[]? RetryDelays { get; set; }

        /// <summary>Retry delays used when <see cref="RetryDelays"/> is <see langword="null"/>.</summary>
        public static IReadOnlyList<TimeSpan> DefaultRetryDelays { get; } =
        [
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        ];

        /// <summary><see cref="RetryDelays"/> when set, otherwise <see cref="DefaultRetryDelays"/>.</summary>
        public IReadOnlyList<TimeSpan> EffectiveRetryDelays => RetryDelays ?? DefaultRetryDelays;

        /// <summary>Capacity of the pending queue; when full the oldest group is dropped (it still expires by TTL remotely).</summary>
        public int QueueCapacity { get; set; } = 256;

        /// <summary>
        /// Level of the single startup log written when sending is disabled (<see cref="IsEnabled"/> is false).
        /// Default <see cref="LogLevel.Warning"/>, because a registered but unconfigured sender usually means a missing
        /// setting; set <see cref="LogLevel.None"/> to opt out (for example when disabling is intentional).
        /// </summary>
        public LogLevel DisabledLogLevel { get; set; } = LogLevel.Warning;

        /// <summary>True when both <see cref="TargetUrl"/> and <see cref="Key"/> are set.</summary>
        public bool IsEnabled => !string.IsNullOrWhiteSpace(TargetUrl) && !string.IsNullOrEmpty(Key);
    }
}
