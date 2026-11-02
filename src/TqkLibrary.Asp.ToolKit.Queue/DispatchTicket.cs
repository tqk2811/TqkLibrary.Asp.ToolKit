namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// Vé của một việc đã nộp vào <see cref="DispatchQueue{TItem}"/>. <see cref="Completion"/> luôn kết thúc đúng
/// một lần: có kết quả, lỗi của việc, <see cref="DispatchQueueFullException"/>, hoặc huỷ (người gọi huỷ khi
/// đang chờ/đang chạy, hay hàng đợi bị dispose).
/// </summary>
public sealed class DispatchTicket<TResult>
{
    private int _queuePosition = -1;

    internal DispatchTicket(Task<TResult> completion) => Completion = completion;

    public Task<TResult> Completion { get; }

    /// <summary>
    /// Vị trí (tính từ 0) trong hàng chờ của khoá công bằng của việc này, việc ưu tiên đứng trước; <c>null</c>
    /// khi chưa được xếp, đã chạy hoặc đã kết thúc. Luồng điều phối ghi sau mỗi lượt quét, bên ngoài chỉ đọc.
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
