using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching
{
    /// <summary>
    /// Caches one value with an expiry; refreshed by exactly ONE caller at a time.
    /// <list type="bullet">
    ///   <item>Value and expiry live together in one immutable record assigned to a <c>volatile</c> field: a reader
    ///   outside the lock never sees a new expiry paired with an old value.</item>
    ///   <item>When the TTL has elapsed and another caller is already refreshing, the old value is returned
    ///   IMMEDIATELY (no queueing). Only the very first load, or a load after <see cref="Invalidate"/>, waits.</item>
    ///   <item><see cref="Invalidate"/> bumps a version number: a load that started BEFORE it cannot write its result
    ///   back.</item>
    ///   <item>A failed refresh while an old (not invalidated) value exists logs a warning, returns the old value and
    ///   extends it by <c>min(ttl, MaxStaleBackoff)</c> so not every request pays a timeout. Without an old value the
    ///   error is thrown and remembered for <see cref="FailureHoldFor"/>, so callers already queued on the lock get
    ///   the same error instead of each loading again (avoids N x timeout while the backend is down).</item>
    ///   <item>A refresh while an old, valid value exists runs with <see cref="CancellationToken.None"/> (one client
    ///   disconnecting does not cancel the refresh for everyone); the first load and a load after Invalidate use the
    ///   caller's token.</item>
    ///   <item><b>The <c>load</c> delegate must read its data inside the lambda</b>, not capture a precomputed value;
    ///   otherwise Invalidate is not safe (the next load writes the old data back).</item>
    ///   <item>The value is shared by every caller: never mutate it.</item>
    ///   <item><b>The <c>load</c> delegate must enforce its own timeout.</b> The first load (and a load after
    ///   Invalidate) holds the lock and only observes the caller's token; there is no internal timeout, so a load that
    ///   never completes blocks every caller that has no old value to fall back on.</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">Cached value type.</typeparam>
    public sealed class StaleWhileRefreshCache<T> : IInvalidatableCache
    {
        private sealed record Entry(T Value, DateTimeOffset NextRefreshAt, bool Invalidated = false);

        private sealed record Failure(ExceptionDispatchInfo Error, DateTimeOffset Until, long Version, long Seq);

        /// <summary>Default for how long a load error is remembered for callers queued on the lock.</summary>
        public static readonly TimeSpan FailureHoldFor = TimeSpan.FromSeconds(2);

        /// <summary>Default upper bound of the extension given to an old value after a failed refresh.</summary>
        public static readonly TimeSpan MaxStaleBackoff = TimeSpan.FromSeconds(10);

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _ttl;
        private readonly ILogger? _logger;
        private readonly TimeSpan _failureHoldFor;
        private readonly TimeSpan _maxStaleBackoff;

        private volatile Entry? _entry;
        private volatile Failure? _failure;
        private long _version;
        private long _failureSeq;

        /// <param name="timeProvider">Clock.</param>
        /// <param name="ttl">How long a value loaded through <see cref="GetAsync"/> is kept.</param>
        /// <param name="logger">Optional; logs a warning when a refresh fails and the old value is served.</param>
        /// <param name="failureHoldFor">How long a load error is remembered for callers queued on the lock (default <see cref="FailureHoldFor"/>).</param>
        /// <param name="maxStaleBackoff">Upper bound of the extension given to an old value after a failed refresh (default <see cref="MaxStaleBackoff"/>).</param>
        public StaleWhileRefreshCache(TimeProvider timeProvider, TimeSpan ttl, ILogger? logger = null,
            TimeSpan? failureHoldFor = null, TimeSpan? maxStaleBackoff = null)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            _timeProvider = timeProvider;
            _ttl = ttl;
            _logger = logger;
            _failureHoldFor = failureHoldFor ?? FailureHoldFor;
            _maxStaleBackoff = maxStaleBackoff ?? MaxStaleBackoff;
        }

        /// <summary>
        /// The fresh value, or a new one loaded through <paramref name="load"/> (a thrown error is not cached).
        /// <paramref name="load"/> must apply its own timeout: the first load has no internal one.
        /// </summary>
        public Task<T> GetAsync(Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(load);
            // Hit: skip the wrapping lambda and the async state machine below.
            var entry = _entry;
            if (entry is not null && _timeProvider.GetUtcNow() < entry.NextRefreshAt)
                return Task.FromResult(entry.Value);
            return GetWithKeepAsync(async ct => (await load(ct).ConfigureAwait(false), (TimeSpan?)_ttl), cancellationToken);
        }

        /// <summary>
        /// Same as <see cref="GetAsync"/> but <paramref name="load"/> decides how long to keep the value;
        /// <see langword="null"/> means "return it to this caller only, do not remember it".
        /// </summary>
        public async Task<T> GetWithKeepAsync(
            Func<CancellationToken, Task<(T Value, TimeSpan? KeepFor)>> load,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(load);
            var entry = _entry;
            if (entry is not null && _timeProvider.GetUtcNow() < entry.NextRefreshAt)
                return entry.Value;

            // Marker to tell whether another caller failed a load while this one was waiting for the lock.
            long failureSeqBefore = Interlocked.Read(ref _failureSeq);

            if (entry is { Invalidated: false })
            {
                // Old value only expired by TTL: someone else refreshing -> serve it, do not wait.
                // An invalidated value is NOT served: wait like the first load.
                if (!_lock.Wait(0, CancellationToken.None))
                    return entry.Value;
            }
            else
            {
                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                entry = _entry;
                if (entry is not null && _timeProvider.GetUtcNow() < entry.NextRefreshAt)
                    return entry.Value;

                var version = Interlocked.Read(ref _version);
                if (_failure is { } failed
                    && failed.Seq != failureSeqBefore
                    && failed.Version == version
                    && _timeProvider.GetUtcNow() < failed.Until
                    && entry is not { Invalidated: false })
                {
                    // The previous holder just failed while we were waiting: rethrow that error, do not load again.
                    failed.Error.Throw();
                }

                // With an old value the refresh belongs to nobody in particular; otherwise follow the caller's token.
                var loadToken = entry is { Invalidated: false } ? CancellationToken.None : cancellationToken;

                (T Value, TimeSpan? KeepFor) loaded;
                try
                {
                    loaded = await load(loadToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!loadToken.IsCancellationRequested)
                {
                    if (entry is { Invalidated: false })
                    {
                        _logger?.LogWarning(ex, "Refreshing cached {CacheValue} failed; serving the previous value.", typeof(T).Name);
                        ExtendStale(entry, version);
                        return entry.Value;
                    }

                    _failure = new Failure(ExceptionDispatchInfo.Capture(ex), _timeProvider.GetUtcNow() + _failureHoldFor, version,
                        Interlocked.Increment(ref _failureSeq));
                    throw;
                }

                _failure = null;
                if (loaded.KeepFor is { } keep && Interlocked.Read(ref _version) == version)
                {
                    _entry = new Entry(loaded.Value, _timeProvider.GetUtcNow() + keep);
                    // Invalidate slipped in between the version check and the assignment: mark again so it is not lost.
                    if (Interlocked.Read(ref _version) != version)
                        MarkInvalidated();
                }
                return loaded.Value;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>The remembered value (even if expired) without loading; <see langword="false"/> before the first load.</summary>
        public bool TryPeek(out T value)
        {
            var entry = _entry;
            value = entry is null ? default! : entry.Value;
            return entry is not null;
        }

        /// <inheritdoc />
        public void Invalidate()
        {
            Interlocked.Increment(ref _version);
            MarkInvalidated();
        }

        /// <summary>Extends the old value by min(ttl, backoff) with CAS, only if no Invalidate happened meanwhile.</summary>
        private void ExtendStale(Entry entry, long version)
        {
            if (Interlocked.Read(ref _version) != version)
                return;
            var extended = entry with { NextRefreshAt = _timeProvider.GetUtcNow() + (_ttl < _maxStaleBackoff ? _ttl : _maxStaleBackoff) };
#pragma warning disable CS0420 // Interlocked has its own memory barrier; ref to a volatile field is safe.
            Interlocked.CompareExchange(ref _entry, extended, entry);
#pragma warning restore CS0420
            // Invalidate slipped in between the CAS and the check: mark again.
            if (Interlocked.Read(ref _version) != version)
                MarkInvalidated();
        }

        /// <summary>Marks the current value as expired + invalidated; CAS so a value written by a newer load is not lost.</summary>
        private void MarkInvalidated()
        {
            while (true)
            {
                var entry = _entry;
                if (entry is null || entry.Invalidated)
                    return;
                var marked = entry with { NextRefreshAt = DateTimeOffset.MinValue, Invalidated = true };
#pragma warning disable CS0420 // Interlocked has its own memory barrier; ref to a volatile field is safe.
                if (ReferenceEquals(Interlocked.CompareExchange(ref _entry, marked, entry), entry))
                    return;
#pragma warning restore CS0420
            }
        }
    }
}
