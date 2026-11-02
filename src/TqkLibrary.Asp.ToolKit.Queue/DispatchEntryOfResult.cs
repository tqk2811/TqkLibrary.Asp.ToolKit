namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>Work with result <typeparamref name="TResult"/>; the ticket returned to the caller wraps its <see cref="TaskCompletionSource{TResult}"/>.</summary>
internal sealed class DispatchEntry<TItem, TResult> : DispatchEntry<TItem>
{
    private readonly Func<TItem, CancellationToken, Task<TResult>> _work;

    // RunContinuationsAsynchronously: without this flag, TrySetResult would run the caller's code
    // (writing the response, bookkeeping...) right on the dispatcher thread and block the whole queue.
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
            // Task.Run: the actual work (HTTP call, parse, stream) runs on the thread pool. Called directly, every continuation
            // without ConfigureAwait(false) inside the work would post back to the dispatcher thread and funnel the whole queue onto one thread.
            // This await still captures the dispatcher thread's SynchronizationContext, so it returns to that thread.
            var result = await Task.Run(() => _work(Item, token));
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The token passed in belongs to the linked cancellation source (which will be disposed); the caller waiting sees its own token.
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
