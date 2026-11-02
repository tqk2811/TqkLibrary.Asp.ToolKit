namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// Một việc trong <see cref="DispatchQueue{TItem}"/>, không phụ thuộc kiểu kết quả để hàng đợi giữ chung một danh sách.
/// Các field điều phối (<see cref="FairnessKey"/>, <see cref="Node"/>) chỉ luồng điều phối đọc/ghi.
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

    /// <summary>Biên nhận chính sách trả lúc cho chạy, trả lại nguyên vẹn lúc việc kết thúc.</summary>
    public object? Receipt { get; set; }

    /// <summary>Gắn lúc nộp: người gọi huỷ khi việc còn chờ thì đánh thức luồng điều phối để gỡ việc ngay.</summary>
    public void SetCancelRegistration(CancellationTokenRegistration registration) => _cancelRegistration = registration;

    /// <summary>Gỡ đăng ký huỷ — gọi khi việc rời hàng chờ (được chạy hoặc kết thúc), không thì callback sống tới hết đời token của người gọi.</summary>
    public void ReleaseCancelRegistration() => _cancelRegistration.Dispose();

    /// <summary>Chạy việc thật trên thread pool rồi đặt kết quả cho vé. Không bao giờ ném.</summary>
    public abstract Task RunAsync(CancellationToken token);

    public abstract void Cancel();

    public abstract void Fail(Exception exception);

    public abstract void SetQueuePosition(int? position);
}
