namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// Cấu hình của <see cref="Policies.RateLimitPolicy{TItem}"/>: giãn nhịp BẮT ĐẦU việc (không giới hạn số việc chạy cùng lúc,
/// cái đó là việc của <see cref="Policies.ConcurrencyCapPolicy{TItem}"/>). Hai giới hạn dùng được riêng hoặc cùng nhau.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Khoảng cách tối thiểu giữa hai lần bắt đầu việc. <see cref="TimeSpan.Zero"/> = tắt.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Số lần bắt đầu tối đa trong mọi cửa sổ trượt dài <see cref="Window"/>. <c>null</c> = tắt.</summary>
    public int? MaxPerWindow { get; init; }

    /// <summary>Độ dài cửa sổ trượt của <see cref="MaxPerWindow"/>; phải dương khi <see cref="MaxPerWindow"/> có giá trị.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// <c>true</c> (mặc định): việc ưu tiên luôn được chạy, bỏ qua giới hạn, nhưng vẫn được ghi vào mốc/cửa sổ
    /// để việc thường sau đó nhường chỗ. <c>false</c>: việc ưu tiên cũng bị giới hạn như việc thường.
    /// </summary>
    public bool PriorityBypass { get; init; } = true;
}
