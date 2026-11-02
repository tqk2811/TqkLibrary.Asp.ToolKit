using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Rate-limits work starts, shared by all work (global, not per key): at least
/// <see cref="RateLimitOptions.MinInterval"/> apart and/or at most <see cref="RateLimitOptions.MaxPerWindow"/> starts in the sliding
/// window <see cref="RateLimitOptions.Window"/>. When blocked it returns <c>Delay</c> with exactly the remaining wait, so the queue schedules
/// the re-evaluation at the right moment without anyone calling <c>Wake</c>.
/// <para>
/// State is the most recent start mark and a queue of marks within the window; no locking since only the queue's dispatcher thread
/// calls in. The mark queue is populated only when <see cref="RateLimitOptions.MaxPerWindow"/> has a value, and is pruned
/// on every evaluation so memory never exceeds the number of starts in one window. Combine with <see cref="ConcurrencyCapPolicy{TItem}"/>
/// via <see cref="CompositeAdmissionPolicy{TItem}"/>.
/// </para>
/// </summary>
public sealed class RateLimitPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _clock;
    private readonly Queue<DateTimeOffset> _starts = new();
    private DateTimeOffset? _last;

    /// <param name="options">Configuration; validated here and its values are captured, not read live.</param>
    /// <param name="clock">Clock; <c>null</c> = <see cref="TimeProvider.System"/>. Pass a fake clock in tests.</param>
    /// <exception cref="ArgumentOutOfRangeException">MinInterval is negative, or MaxPerWindow has a value while it or Window is not positive.</exception>
    public RateLimitPolicy(RateLimitOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.MinInterval, "MinInterval must be >= 0.");
        if (options.MaxPerWindow is { } max)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), max, "MaxPerWindow must be > 0.");
            if (options.Window <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), options.Window, "Window must be > 0 when MaxPerWindow is set.");
        }

        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        if (isPriority && _options.PriorityBypass)
            return AdmissionDecision.Start;

        var now = _clock.GetUtcNow();
        var wait = TimeSpan.Zero;

        if (_options.MinInterval > TimeSpan.Zero && _last is { } last)
            wait = last + _options.MinInterval - now;

        if (_options.MaxPerWindow is { } max)
        {
            // Drop marks that have left the window (<= now - Window).
            while (_starts.Count > 0 && _starts.Peek() <= now - _options.Window)
                _starts.Dequeue();
            if (_starts.Count >= max)
            {
                var windowWait = _starts.Peek() + _options.Window - now;
                if (windowWait > wait)
                    wait = windowWait;
            }
        }

        return wait > TimeSpan.Zero ? AdmissionDecision.Delay(wait) : AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        // Priority work that bypasses is also recorded: it used a slot, so later normal work must yield.
        var now = _clock.GetUtcNow();
        _last = now;
        if (_options.MaxPerWindow is not null)
            _starts.Enqueue(now);
        return null;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        // Rate limiting counts only at start; there is nothing to release.
    }
}
