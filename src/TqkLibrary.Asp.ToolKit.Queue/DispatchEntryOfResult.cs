namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Việc có kết quả <typeparamref name="TResult"/>; vé trả cho người gọi bọc <see cref="TaskCompletionSource{TResult}"/> của nó.</summary>
internal sealed class DispatchEntry<TItem, TResult> : DispatchEntry<TItem>
{
    private readonly Func<TItem, CancellationToken, Task<TResult>> _work;

    // RunContinuationsAsynchronously: không có cờ này thì TrySetResult chạy luôn code của người gọi
    // (ghi response, ghi sổ...) ngay trên luồng điều phối và chặn cả hàng đợi.
    private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DispatchEntry(TItem item, Func<TItem, CancellationToken, Task<TResult>> work, bool isPriority, CancellationToken callerToken)
        : base(item, isPriority, callerToken)
    {
        _work = work;
        Ticket = new DispatchTicket<TResult>(_completion.Task);
    }

    public DispatchTicket<TResult> Ticket { get; }

    public override async Task RunAsync(CancellationToken token)
    {
        try
        {
            // Task.Run: việc thật (gọi HTTP, parse, stream) chạy trên thread pool. Gọi thẳng thì mọi continuation
            // không ConfigureAwait(false) bên trong việc sẽ post về luồng điều phối và dồn cả hàng đợi vào một luồng.
            // Phần await này vẫn bắt SynchronizationContext của luồng điều phối nên quay về đúng luồng đó.
            var result = await Task.Run(() => _work(Item, token));
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Token truyền vào là của nguồn huỷ ghép (sẽ bị dispose); người gọi chờ thấy chính token của họ.
            _completion.TrySetCanceled(CallerToken.IsCancellationRequested ? CallerToken : token);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    public override void Cancel()
    {
        ReleaseCancelRegistration();
        SetQueuePosition(null);
        _completion.TrySetCanceled(CallerToken.IsCancellationRequested ? CallerToken : CancellationToken.None);
    }

    public override void Fail(Exception exception)
    {
        ReleaseCancelRegistration();
        SetQueuePosition(null);
        _completion.TrySetException(exception);
    }

    public override void SetQueuePosition(int? position) => Ticket.SetQueuePosition(position);
}
