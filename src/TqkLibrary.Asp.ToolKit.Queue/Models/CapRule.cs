namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// Một trần song song mà việc phải lọt qua. Việc thường chạy được khi số việc đang chạy cùng <see cref="Key"/>
/// nhỏ hơn <see cref="Max"/>; việc ưu tiên được thêm <see cref="PriorityExtra"/> chỗ mà việc thường không bao giờ dùng.
/// </summary>
/// <param name="Key">Khoá của bộ đếm, vd <c>("customer", CustomerId)</c> hoặc <c>("provider", provider)</c>. So sánh bằng <see cref="object.Equals(object)"/>.</param>
/// <param name="Max">Số việc thường tối đa chạy cùng lúc với cùng khoá.</param>
/// <param name="PriorityExtra">Số chỗ thêm chỉ dành cho việc ưu tiên.</param>
public readonly record struct CapRule(object Key, int Max, int PriorityExtra = 0)
{
    public int LimitFor(bool isPriority) => isPriority ? Max + PriorityExtra : Max;
}
