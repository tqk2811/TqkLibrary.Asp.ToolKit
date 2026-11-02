using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Interfaces;

/// <summary>
/// Chính sách cho chạy của một <see cref="DispatchQueue{TItem}"/>: trần song song, giãn nhịp, giữ hạn mức tài nguyên...
/// Mọi hàm đều chỉ được gọi trên luồng điều phối của hàng đợi, lần lượt, không bao giờ song song —
/// nên trạng thái bên trong (bộ đếm, tổng tạm giữ) KHÔNG cần khoá. Đổi lại, hàm phải chạy nhanh và
/// không I/O: dữ liệu cần đọc từ DB thì nạp sẵn vào cache, cập nhật ở chỗ khác rồi gọi <see cref="DispatchQueue{TItem}.Wake"/>.
/// </summary>
public interface IAdmissionPolicy<in TItem>
{
    /// <summary>Xét việc đang chờ có được chạy ngay không. <paramref name="isPriority"/> cho phép dùng phần dư dành cho việc ưu tiên.</summary>
    AdmissionDecision CanStart(TItem item, bool isPriority);

    /// <summary>
    /// Việc vừa được cho chạy (ngay sau <see cref="CanStart"/> trả <c>Start</c>): giữ chỗ và trả về "biên nhận" mô tả
    /// ĐÚNG những gì đã giữ (khoá đã tăng, hạn mức đã giữ). Hàng đợi cất biên nhận theo việc và trả nguyên lại
    /// cho <see cref="OnFinished"/>, nên chính sách không phải suy lại từ <paramref name="item"/> — cấu hình đổi giữa
    /// chừng cũng không làm nhả nhầm. Phải nguyên tử: ném thì coi như chưa giữ gì.
    /// </summary>
    object? OnStarted(TItem item, bool isPriority);

    /// <summary>Việc đã chạy xong (thành công, lỗi hay bị huỷ): nhả đúng những gì <paramref name="receipt"/> ghi.</summary>
    void OnFinished(TItem item, bool isPriority, object? receipt);
}
