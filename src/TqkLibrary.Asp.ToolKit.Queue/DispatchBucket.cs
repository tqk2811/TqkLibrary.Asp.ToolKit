namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Waiting queues of one fairness key: priority and normal work are kept separately, each FIFO. Used only by the dispatcher thread.</summary>
internal sealed class DispatchBucket<TItem>
{
    public DispatchBucket(LinkedListNode<object> orderNode) => OrderNode = orderNode;

    /// <summary>Position of the key in the rotation; after the key runs one item, this node moves to the end of the rotation.</summary>
    public LinkedListNode<object> OrderNode { get; }

    public LinkedList<DispatchEntry<TItem>> Priority { get; } = new();

    public LinkedList<DispatchEntry<TItem>> Normal { get; } = new();

    public bool IsEmpty => Priority.Count == 0 && Normal.Count == 0;

    public LinkedList<DispatchEntry<TItem>> ListOf(bool isPriority) => isPriority ? Priority : Normal;
}
