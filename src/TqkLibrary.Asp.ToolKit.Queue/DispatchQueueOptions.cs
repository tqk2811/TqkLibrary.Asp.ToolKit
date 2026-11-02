using TqkLibrary.Asp.ToolKit.Queue.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Cấu hình của <see cref="DispatchQueue{TItem}"/>.</summary>
public sealed class DispatchQueueOptions<TItem>
{
    /// <summary>Chính sách cho chạy; ghép nhiều trần bằng <see cref="Policies.CompositeAdmissionPolicy{TItem}"/>.</summary>
    public required IAdmissionPolicy<TItem> Policy { get; init; }

    /// <summary>
    /// Khoá chia lượt công bằng (thường là khách hàng): các khoá được xét xoay vòng, khoá vừa được chạy
    /// một việc thì xuống cuối vòng. Trả cùng một giá trị cho mọi việc thì thành FIFO thuần.
    /// </summary>
    public required Func<TItem, object> FairnessKey { get; init; }

    /// <summary>Trần số việc THƯỜNG đang chờ của mỗi khoá; vượt thì việc mới bị <see cref="DispatchQueueFullException"/>. <c>null</c> = không giới hạn.</summary>
    public int? MaxPendingPerKey { get; init; }

    /// <summary>Trần riêng cho việc ƯU TIÊN đang chờ của mỗi khoá, để hàng thường đầy không chặn việc ưu tiên. <c>null</c> = không giới hạn.</summary>
    public int? MaxPriorityPendingPerKey { get; init; }

    /// <summary>Nhận lỗi ném ra từ chính sách hoặc từ vòng điều phối (đã được nuốt để hàng đợi không chết). Gắn logger vào đây.</summary>
    public Action<Exception>? OnInternalError { get; init; }

    /// <summary>Tên luồng điều phối, để nhìn thấy trong debugger/dump.</summary>
    public string? Name { get; init; }
}
