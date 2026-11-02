using TqkLibrary.Asp.ToolKit.Queue.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Configuration of <see cref="DispatchQueue{TItem}"/>.</summary>
public sealed class DispatchQueueOptions<TItem>
{
    /// <summary>Admission policy; combine several caps with <see cref="Policies.CompositeAdmissionPolicy{TItem}"/>.</summary>
    public required IAdmissionPolicy<TItem> Policy { get; init; }

    /// <summary>
    /// Fairness key (usually the customer): keys are evaluated in rotation, and a key that has just started
    /// one item moves to the end of the rotation. Returning the same value for every item gives plain FIFO.
    /// </summary>
    public required Func<TItem, object> FairnessKey { get; init; }

    /// <summary>Cap on NORMAL work waiting per key; beyond it new work is rejected with <see cref="DispatchQueueFullException"/>. <c>null</c> = unlimited.</summary>
    public int? MaxPendingPerKey { get; init; }

    /// <summary>Separate cap on PRIORITY work waiting per key, so a full normal queue does not block priority work. <c>null</c> = unlimited.</summary>
    public int? MaxPriorityPendingPerKey { get; init; }

    /// <summary>Receives errors thrown from the policy or the dispatch loop (swallowed so the queue does not die). Attach a logger here.</summary>
    public Action<Exception>? OnInternalError { get; init; }

    /// <summary>Name of the dispatcher thread, visible in the debugger/dumps.</summary>
    public string? Name { get; init; }
}
