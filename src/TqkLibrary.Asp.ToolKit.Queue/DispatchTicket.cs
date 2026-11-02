namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// Ticket of a work item submitted to <see cref="DispatchQueue{TItem}"/>. <see cref="Completion"/> always completes exactly
/// once: with the result, the work's error, <see cref="DispatchQueueFullException"/>, or cancellation (the caller cancelled
/// while the work was waiting/running, or the queue was disposed).
/// </summary>
public sealed class DispatchTicket<TResult>
{
    private int _queuePosition = -1;

    internal DispatchTicket(Task<TResult> completion) => Completion = completion;

    public Task<TResult> Completion { get; }

    /// <summary>
    /// Position (0-based) in the waiting queue of this item's fairness key, priority work first; <c>null</c>
    /// when not yet queued, already started or finished. Written by the dispatcher thread after each scan, read-only from outside.
    /// </summary>
    public int? QueuePosition
    {
        get
        {
            var value = Volatile.Read(ref _queuePosition);
            return value < 0 ? null : value;
        }
    }

    internal void SetQueuePosition(int? position) => Volatile.Write(ref _queuePosition, position ?? -1);
}
