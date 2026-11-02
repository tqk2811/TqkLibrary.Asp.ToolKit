using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Several stacked concurrency caps (per provider, per customer, per work type...): work starts only when it passes
/// ALL caps that <c>rules</c> returns for it. The counter is a plain <see cref="Dictionary{TKey,TValue}"/> because only the queue's
/// dispatcher thread calls in; a key that drops to 0 is removed immediately, so memory is proportional to the number of keys with running work.
/// <para>
/// <c>rules</c> is re-read on every evaluation, so <see cref="CapRule.Max"/> can come from live configuration. The set of keys already incremented is
/// captured into the receipt at start, so on finish exactly those keys are released even if the rules changed. A work item must not be
/// given two rules with the same key.
/// </para>
/// </summary>
public sealed class ConcurrencyCapPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly Func<TItem, IReadOnlyList<CapRule>> _rules;
    private readonly Dictionary<object, int> _running = new();

    public ConcurrencyCapPolicy(Func<TItem, IReadOnlyList<CapRule>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>Number of keys with running work (to monitor counter leaks). Accurate only when read on the dispatcher thread or while the queue is idle.</summary>
    public int TrackedKeyCount => _running.Count;

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        foreach (var rule in _rules(item))
        {
            if (_running.GetValueOrDefault(rule.Key) >= rule.LimitFor(isPriority))
                return AdmissionDecision.Wait;
        }

        return AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        // Capture the keys first, then increment: if _rules throws midway, no counter has been touched.
        var keys = _rules(item).Select(rule => rule.Key ?? throw new InvalidOperationException("CapRule.Key is null.")).ToArray();
        foreach (var key in keys)
            _running[key] = _running.GetValueOrDefault(key) + 1;
        return keys;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        if (receipt is not object[] keys)
            return;
        foreach (var key in keys)
        {
            if (!_running.TryGetValue(key, out var count))
                continue;
            if (count <= 1)
                _running.Remove(key);
            else
                _running[key] = count - 1;
        }
    }
}
