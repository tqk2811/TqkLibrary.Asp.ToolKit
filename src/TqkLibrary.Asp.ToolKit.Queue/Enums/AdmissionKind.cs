namespace TqkLibrary.Asp.ToolKit.Queue.Enums;

/// <summary>Kết quả xét một việc đang chờ có được chạy ngay không.</summary>
public enum AdmissionKind
{
    /// <summary>Chạy ngay.</summary>
    Start,

    /// <summary>Chưa chạy được; xét lại khi có việc khác kết thúc hoặc có việc mới vào hàng.</summary>
    Wait,

    /// <summary>Chưa chạy được vì lý do thời gian (giãn nhịp, tạm dừng sau 429); xét lại sau một khoảng.</summary>
    Delay,
}
