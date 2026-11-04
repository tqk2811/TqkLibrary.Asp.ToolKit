using System.Collections.Concurrent;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Per-process guard of the sliding-expiry write (singleton): at most one touch per session per hold period, so
    /// parallel requests that all see an old <c>LastAccessAt</c> write once, and a failing store is retried after a short
    /// back-off instead of on every request.
    /// </summary>
    internal sealed class SessionTouchThrottle
    {
        private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);
        private readonly object _pruneLock = new();
        private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

        /// <summary>True when the caller may touch <paramref name="sessionId"/> now; blocks it for <paramref name="hold"/>.</summary>
        public bool TryBegin(string sessionId, DateTimeOffset now, TimeSpan hold)
        {
            Prune(now);
            var until = now + hold;
            while (true)
            {
                if (_blockedUntil.TryGetValue(sessionId, out var current))
                {
                    if (current > now)
                        return false;
                    if (_blockedUntil.TryUpdate(sessionId, until, current))
                        return true;
                }
                else if (_blockedUntil.TryAdd(sessionId, until))
                {
                    return true;
                }
            }
        }

        /// <summary>The touch failed: allow the next attempt at <paramref name="retryAt"/>.</summary>
        public void Failed(string sessionId, DateTimeOffset retryAt) => _blockedUntil[sessionId] = retryAt;

        /// <summary>Number of tracked sessions; for tests.</summary>
        internal int Count => _blockedUntil.Count;

        private void Prune(DateTimeOffset now)
        {
            lock (_pruneLock)
            {
                if (now - _lastPrune < PruneEvery)
                    return;
                _lastPrune = now;
            }
            foreach (var pair in _blockedUntil)
            {
                if (pair.Value <= now)
                    _blockedUntil.TryRemove(pair); // only the exact value seen: a newer block stays
            }
        }
    }
}
