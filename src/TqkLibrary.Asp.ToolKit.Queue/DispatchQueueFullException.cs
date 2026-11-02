namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Hàng chờ của khoá công bằng (vd theo khách hàng) đã đầy, việc mới bị từ chối thay vì xếp hàng vô hạn.</summary>
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
