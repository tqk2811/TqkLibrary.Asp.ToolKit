using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Short cache of the API current-user lookup per session id, so one page that triggers several requests (AJAX,
    /// partials, error page re-execution) does not ask the API every time. Consequence: a role/permission change
    /// made elsewhere shows up in the web UI after at most <see cref="WebAuthOptions.CurrentUserCacheLifetime"/>; a
    /// dead session is still blocked immediately by the API itself.
    /// <para>Singleton, per-instance memory (no need to share between instances).</para>
    /// </summary>
    public sealed class CurrentUserCache
    {
        private const string KeyPrefix = "tqk-web-current-user:";

        private readonly IMemoryCache _cache;
        private readonly IOptions<WebAuthOptions> _options;
        private readonly object _resetLock = new();
        private CancellationTokenSource _reset = new();

        public CurrentUserCache(IMemoryCache cache, IOptions<WebAuthOptions> options)
        {
            _cache = cache;
            _options = options;
        }

        /// <summary>The cached user of <paramref name="sessionId"/>, or null.</summary>
        public CurrentUserInfo? Get(string sessionId)
            => _cache.TryGetValue(KeyPrefix + sessionId, out CurrentUserInfo? user) ? user : null;

        /// <summary>Caches <paramref name="user"/> for <paramref name="sessionId"/>.</summary>
        public void Set(string sessionId, CurrentUserInfo user)
        {
            CancellationToken resetToken;
            lock (_resetLock)
                resetToken = _reset.Token;
            _cache.Set(KeyPrefix + sessionId, user, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_options.Value.CurrentUserCacheLifetime)
                .AddExpirationToken(new CancellationChangeToken(resetToken)));
        }

        /// <summary>Removes the entry of <paramref name="sessionId"/> (sign-in, sign-out, session dropped).</summary>
        public void Remove(string? sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
                _cache.Remove(KeyPrefix + sessionId);
        }

        /// <summary>Drops every entry (forces new permissions to apply for everybody right now).</summary>
        public void Clear()
        {
            CancellationTokenSource old;
            lock (_resetLock)
            {
                old = _reset;
                _reset = new CancellationTokenSource();
            }
            old.Cancel();
            old.Dispose();
        }
    }
}
