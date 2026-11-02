namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// An item in <see cref="DispatchQueue{TItem}"/>, independent of the result type so the queue can keep a single shared list.
/// The dispatch fields (<see cref="FairnessKey"/>, <see cref="Node"/>) are read/written only by the dispatcher thread.
/// </summary>
internal abstract class DispatchEntry<TItem>
{
    private CancellationTokenRegistration _cancelRegistration;

    protected DispatchEntry(TItem item, bool isPriority, CancellationToken callerToken)
    {
        Item = item;
        IsPriority = isPriority;
        CallerToken = callerToken;
    }

    public TItem Item { get; }

    public bool IsPriority { get; }

    public CancellationToken CallerToken { get; }

    public object? FairnessKey { get; set; }

    public LinkedListNode<DispatchEntry<TItem>>? Node { get; set; }

    /// <summary>Receipt returned by the policy when the item was started; handed back unchanged when the item finishes.</summary>
    public object? Receipt { get; set; }

    /// <summary>Attached on submit: when the caller cancels while the item is waiting, wakes the dispatcher thread so the item is removed immediately.</summary>
    public void SetCancelRegistration(CancellationTokenRegistration registration) => _cancelRegistration = registration;

    /// <summary>Removes the cancellation registration - call when the item leaves the waiting queue (started or finished); otherwise the callback lives as long as the caller's token.</summary>
    public void ReleaseCancelRegistration() => _cancelRegistration.Dispose();

    /// <summary>Runs the actual work on the thread pool, then sets the ticket's result. Never throws.</summary>
    public abstract Task RunAsync(CancellationToken token);

    public abstract void Cancel();

    public abstract void Fail(Exception exception);

    public abstract void SetQueuePosition(int? position);
}
