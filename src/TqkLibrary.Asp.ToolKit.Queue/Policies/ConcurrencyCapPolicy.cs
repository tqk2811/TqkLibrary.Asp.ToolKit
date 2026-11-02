using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Nhiều trần song song chồng nhau (theo nhà cung cấp, theo khách hàng, theo loại việc...): việc chỉ chạy khi lọt
/// MỌI trần mà <c>rules</c> trả cho nó. Bộ đếm là <see cref="Dictionary{TKey,TValue}"/> thường vì chỉ luồng điều
/// phối của hàng đợi gọi tới; khoá về 0 thì bị xoá ngay nên bộ nhớ chỉ tỉ lệ với số khoá đang có việc chạy.
/// <para>
/// <c>rules</c> được đọc lại mỗi lần xét nên <see cref="CapRule.Max"/> lấy từ cấu hình sống được. Tập khoá đã tăng
/// được chụp vào biên nhận lúc bắt đầu, nên lúc kết thúc nhả đúng khoá đó dù luật đã đổi. Một việc không được trả
/// hai luật trùng khoá.
/// </para>
/// </summary>
public sealed class ConcurrencyCapPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly Func<TItem, IReadOnlyList<CapRule>> _rules;
    private readonly Dictionary<object, int> _running = new();

    public ConcurrencyCapPolicy(Func<TItem, IReadOnlyList<CapRule>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>Số khoá đang có việc chạy (để theo dõi rò bộ đếm). Chỉ chính xác khi đọc trên luồng điều phối hoặc lúc hàng đợi rảnh.</summary>
    public int TrackedKeyCount => _running.Count;

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        foreach (var rule in _rules(item))
        {
            if (_running.GetValueOrDefault(rule.Key) >= rule.LimitFor(isPriority))
                return AdmissionDecision.Wait;
        }

        return AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        // Chụp khoá trước rồi mới tăng: _rules ném giữa chừng thì chưa có bộ đếm nào bị đụng.
        var keys = _rules(item).Select(rule => rule.Key ?? throw new InvalidOperationException("CapRule.Key is null.")).ToArray();
        foreach (var key in keys)
            _running[key] = _running.GetValueOrDefault(key) + 1;
        return keys;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        if (receipt is not object[] keys)
            return;
        foreach (var key in keys)
        {
            if (!_running.TryGetValue(key, out var count))
                continue;
            if (count <= 1)
                _running.Remove(key);
            else
                _running[key] = count - 1;
        }
    }
}
