using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Interfaces;

/// <summary>
/// Admission policy of a <see cref="DispatchQueue{TItem}"/>: concurrency caps, pacing, holding resource quotas...
/// All methods are called only on the queue's dispatcher thread, one at a time, never in parallel -
/// so internal state (counters, reserved totals) needs NO locking. In return, methods must be fast and
/// free of I/O: data that must be read from the DB should be preloaded into a cache, updated elsewhere, then call <see cref="DispatchQueue{TItem}.Wake"/>.
/// </summary>
public interface IAdmissionPolicy<in TItem>
{
    /// <summary>Evaluates whether a waiting item may start now. <paramref name="isPriority"/> allows using the surplus reserved for priority work.</summary>
    AdmissionDecision CanStart(TItem item, bool isPriority);

    /// <summary>
    /// The item was just allowed to start (right after <see cref="CanStart"/> returned <c>Start</c>): reserve the slot and return a "receipt" describing
    /// EXACTLY what was reserved (counters incremented, quota held). The queue stores the receipt per item and hands it back unchanged
    /// to <see cref="OnFinished"/>, so the policy need not re-derive it from <paramref name="item"/> - a configuration change in
    /// between cannot cause a wrong release. Must be atomic: if it throws, nothing is considered held.
    /// </summary>
    object? OnStarted(TItem item, bool isPriority);

    /// <summary>The work has finished (success, error or cancellation): release exactly what <paramref name="receipt"/> records.</summary>
    void OnFinished(TItem item, bool isPriority, object? receipt);
}
