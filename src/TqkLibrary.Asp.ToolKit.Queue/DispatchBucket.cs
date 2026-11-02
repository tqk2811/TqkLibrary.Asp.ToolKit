namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Hàng chờ của một khoá công bằng: việc ưu tiên và việc thường tách riêng, mỗi hàng FIFO. Chỉ luồng điều phối dùng.</summary>
internal sealed class DispatchBucket<TItem>
{
    public DispatchBucket(LinkedListNode<object> orderNode) => OrderNode = orderNode;

    /// <summary>Vị trí của khoá trong vòng xoay; khoá vừa được chạy một việc thì nút này xuống cuối vòng.</summary>
    public LinkedListNode<object> OrderNode { get; }

    public LinkedList<DispatchEntry<TItem>> Priority { get; } = new();

    public LinkedList<DispatchEntry<TItem>> Normal { get; } = new();

    public bool IsEmpty => Priority.Count == 0 && Normal.Count == 0;

    public LinkedList<DispatchEntry<TItem>> ListOf(bool isPriority) => isPriority ? Priority : Normal;
}
