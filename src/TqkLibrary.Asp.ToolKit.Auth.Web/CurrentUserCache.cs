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
        private const string FailurePrefix = "tqk-web-current-user-failed:";

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
            _cache.Remove(FailurePrefix + sessionId);
            _cache.Set(KeyPrefix + sessionId, user, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_options.Value.CurrentUserCacheLifetime)
                .AddExpirationToken(new CancellationChangeToken(resetToken)));
        }

        /// <summary>True when a failed lookup of <paramref name="sessionId"/> is still remembered (<see cref="SetFailure"/>).</summary>
        public bool HasFailure(string sessionId) => _cache.TryGetValue(FailurePrefix + sessionId, out _);

        /// <summary>
        /// Remembers for <paramref name="duration"/> that the lookup of <paramref name="sessionId"/> failed
        /// (<see cref="WebAuthOptions.CurrentUserFailureCacheFor"/>). A later successful <see cref="Set"/> clears it.
        /// </summary>
        public void SetFailure(string sessionId, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                return;
            CancellationToken resetToken;
            lock (_resetLock)
                resetToken = _reset.Token;
            _cache.Set(FailurePrefix + sessionId, true, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(duration)
                .AddExpirationToken(new CancellationChangeToken(resetToken)));
        }

        /// <summary>Removes the entry (and remembered failure) of <paramref name="sessionId"/> (sign-in, sign-out, session dropped).</summary>
        public void Remove(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            _cache.Remove(KeyPrefix + sessionId);
            _cache.Remove(FailurePrefix + sessionId);
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
