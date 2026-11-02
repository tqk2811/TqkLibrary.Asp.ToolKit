namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>The waiting queue of a fairness key (e.g. per customer) is full; the new work is rejected instead of queuing indefinitely.</summary>
public sealed class DispatchQueueFullException : Exception
{
    public DispatchQueueFullException(object fairnessKey, int limit)
        : base($"Dispatch queue is full for key '{fairnessKey}' (limit {limit}).")
    {
        FairnessKey = fairnessKey;
        Limit = limit;
    }

    public object FairnessKey { get; }

    public int Limit { get; }
}
