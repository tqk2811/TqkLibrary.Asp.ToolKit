using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Giới hạn tốc độ bắt đầu việc, dùng chung cho mọi việc (toàn cục, không theo khoá): cách nhau tối thiểu
/// <see cref="RateLimitOptions.MinInterval"/> và/hoặc tối đa <see cref="RateLimitOptions.MaxPerWindow"/> lần trong cửa sổ
/// trượt <see cref="RateLimitOptions.Window"/>. Bị chặn thì trả <c>Delay</c> đúng quãng còn phải chờ, nên hàng đợi hẹn
/// xét lại đúng lúc mà không cần ai gọi <c>Wake</c>.
/// <para>
/// Trạng thái là mốc bắt đầu gần nhất và hàng đợi các mốc trong cửa sổ, không khoá vì chỉ luồng điều phối của hàng
/// đợi gọi tới. Hàng đợi mốc chỉ được nuôi khi <see cref="RateLimitOptions.MaxPerWindow"/> có giá trị, và được tỉa
/// mỗi lần xét nên bộ nhớ không vượt quá số lần bắt đầu trong một cửa sổ. Kết hợp với <see cref="ConcurrencyCapPolicy{TItem}"/>
/// qua <see cref="CompositeAdmissionPolicy{TItem}"/>.
/// </para>
/// </summary>
public sealed class RateLimitPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _clock;
    private readonly Queue<DateTimeOffset> _starts = new();
    private DateTimeOffset? _last;

    /// <param name="options">Cấu hình; kiểm hợp lệ ngay ở đây và được chụp lại giá trị, không đọc sống.</param>
    /// <param name="clock">Đồng hồ; <c>null</c> = <see cref="TimeProvider.System"/>. Truyền đồng hồ giả để test.</param>
    /// <exception cref="ArgumentOutOfRangeException">MinInterval âm, hoặc MaxPerWindow có giá trị mà nó hay Window không dương.</exception>
    public RateLimitPolicy(RateLimitOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.MinInterval, "MinInterval must be >= 0.");
        if (options.MaxPerWindow is { } max)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), max, "MaxPerWindow must be > 0.");
            if (options.Window <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), options.Window, "Window must be > 0 when MaxPerWindow is set.");
        }

        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        if (isPriority && _options.PriorityBypass)
            return AdmissionDecision.Start;

        var now = _clock.GetUtcNow();
        var wait = TimeSpan.Zero;

        if (_options.MinInterval > TimeSpan.Zero && _last is { } last)
            wait = last + _options.MinInterval - now;

        if (_options.MaxPerWindow is { } max)
        {
            // Mốc đã ra khỏi cửa sổ (<= now - Window) thì bỏ.
            while (_starts.Count > 0 && _starts.Peek() <= now - _options.Window)
                _starts.Dequeue();
            if (_starts.Count >= max)
            {
                var windowWait = _starts.Peek() + _options.Window - now;
                if (windowWait > wait)
                    wait = windowWait;
            }
        }

        return wait > TimeSpan.Zero ? AdmissionDecision.Delay(wait) : AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        // Việc ưu tiên bypass cũng được ghi: nó đã dùng một lượt, việc thường sau đó phải nhường.
        var now = _clock.GetUtcNow();
        _last = now;
        if (_options.MaxPerWindow is not null)
            _starts.Enqueue(now);
        return null;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        // Giới hạn tốc độ chỉ tính lúc bắt đầu; không có gì để nhả.
    }
}
