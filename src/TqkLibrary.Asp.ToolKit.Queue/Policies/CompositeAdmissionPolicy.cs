using TqkLibrary.Asp.ToolKit.Queue.Enums;
using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Ghép nhiều chính sách: việc chỉ chạy khi MỌI chính sách cho chạy. Khi bị chặn, có chính sách nào bảo
/// <c>Wait</c> (chờ sự kiện) thì kết quả là <c>Wait</c>; nếu mọi chính sách chặn đều là <c>Delay</c> thì lấy quãng
/// dài nhất, vì việc phải chờ tới khi chính sách chặn lâu nhất mở.
/// </summary>
public sealed class CompositeAdmissionPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly IAdmissionPolicy<TItem>[] _policies;

    public CompositeAdmissionPolicy(params IAdmissionPolicy<TItem>[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies;
    }

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        var waitForEvent = false;
        TimeSpan? longestDelay = null;
        foreach (var policy in _policies)
        {
            var decision = policy.CanStart(item, isPriority);
            switch (decision.Kind)
            {
                case AdmissionKind.Wait:
                    waitForEvent = true;
                    break;
                case AdmissionKind.Delay:
                    if (longestDelay is null || decision.RetryAfter > longestDelay)
                        longestDelay = decision.RetryAfter;
                    break;
            }
        }

        if (waitForEvent)
            return AdmissionDecision.Wait;
        return longestDelay is { } delay ? AdmissionDecision.Delay(delay) : AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        var receipts = new object?[_policies.Length];
        for (var i = 0; i < _policies.Length; i++)
        {
            try
            {
                receipts[i] = _policies[i].OnStarted(item, isPriority);
            }
            catch
            {
                // Giữ hợp đồng nguyên tử: chính sách sau ném thì nhả các chính sách trước đã giữ.
                for (var j = i - 1; j >= 0; j--)
                {
                    try
                    {
                        _policies[j].OnFinished(item, isPriority, receipts[j]);
                    }
                    catch
                    {
                        // Lỗi gốc quan trọng hơn; lỗi lúc hoàn tác không được che nó.
                    }
                }

                throw;
            }
        }

        return receipts;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        // Biên nhận luôn là mảng do OnStarted ở trên tạo; khác đi là bên gọi sai hợp đồng.
        var receipts = receipt as object?[];

        // Chính sách nào ném cũng không được làm các chính sách sau mất lượt nhả chỗ.
        List<Exception>? errors = null;
        for (var i = 0; i < _policies.Length; i++)
        {
            try
            {
                _policies[i].OnFinished(item, isPriority, receipts?[i]);
            }
            catch (Exception ex)
            {
                (errors ??= new()).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException(errors);
    }
}
