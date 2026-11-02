using System.Collections.Concurrent;

using TqkLibrary.Asp.ToolKit.Queue.Enums;

using Nito.AsyncEx;

namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// Hàng đợi có một luồng điều phối riêng (luồng <c>LongRunning</c> chạy <see cref="AsyncContext"/>):
/// <list type="number">
/// <item>Bên ngoài <see cref="Submit"/> đẩy việc vào hộp thư (<see cref="ConcurrentQueue{T}"/>) — chỗ duy nhất có đụng độ giữa các luồng.</item>
/// <item>Luồng điều phối chuyển việc từ hộp thư sang hàng chờ theo khoá công bằng, xét từng việc với
/// <see cref="Interfaces.IAdmissionPolicy{TItem}"/>, việc đủ điều kiện thì gỡ khỏi hàng chờ, đưa vào tập đang chạy và cho chạy.</item>
/// <item>Việc chạy xong: phần vỏ quay về luồng điều phối (nhờ SynchronizationContext), gỡ khỏi tập đang chạy, báo chính sách nhả chỗ, đánh thức để quét lại.</item>
/// </list>
/// Hàng chờ, tập đang chạy và trạng thái của chính sách chỉ luồng điều phối đụng tới nên không cần khoá.
/// Việc ưu tiên được xét trước và có thể dùng phần dư chính sách dành riêng; không giành chỗ của việc đang chạy.
/// Hàng đợi sở hữu luồng điều phối và nguồn huỷ nội bộ, giải phóng ở <see cref="DisposeAsync"/>;
/// chính sách nhận từ ngoài vào nên KHÔNG dispose hộ.
/// </summary>
public sealed class DispatchQueue<TItem> : IAsyncDisposable
{
    // SemaphoreSlim chỉ cấp kernel handle khi có ai đọc AvailableWaitHandle — ở đây không ai đọc.
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ConcurrentQueue<DispatchEntry<TItem>> _inbox = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatchQueueOptions<TItem> _options;
    private readonly Task _dispatcher;

    // Chỉ luồng điều phối dùng.
    private readonly Dictionary<object, DispatchBucket<TItem>> _buckets = new();
    private readonly LinkedList<object> _keyOrder = new();
    private readonly HashSet<DispatchEntry<TItem>> _running = new();

    private int _signaled;
    private int _stopping;

    // Chặn quãng chờ theo thời gian: SemaphoreSlim.WaitAsync không nhận quá int.MaxValue ms, và Delay(0)
    // lặp lại thì thành vòng quay bận.
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    public DispatchQueue(DispatchQueueOptions<TItem> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _dispatcher = Task.Factory.StartNew(
            () => AsyncContext.Run(LoopAsync),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        // Vòng điều phối chỉ báo lỗi bên trong mỗi nhịp; văng ra ngoài vòng thì cả hàng đợi treo mà không ai hay.
        _dispatcher.ContinueWith(
            t => ReportError(new InvalidOperationException("Dispatch loop stopped unexpectedly", t.Exception!.GetBaseException())),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Nộp một việc. Không chặn: vé trả về ngay, kết quả nằm ở <see cref="DispatchTicket{TResult}.Completion"/>.
    /// <paramref name="cancellationToken"/> huỷ được cả lúc còn chờ (gỡ khỏi hàng) lẫn lúc đang chạy (truyền vào <paramref name="work"/>).
    /// </summary>
    public DispatchTicket<TResult> Submit<TResult>(
        TItem item,
        Func<TItem, CancellationToken, Task<TResult>> work,
        bool isPriority = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var entry = new DispatchEntry<TItem, TResult>(item, work, isPriority, cancellationToken);
        if (Volatile.Read(ref _stopping) != 0)
        {
            entry.Fail(new ObjectDisposedException(nameof(DispatchQueue<TItem>)));
            return entry.Ticket;
        }

        // Đăng ký TRƯỚC khi vào hộp thư: luồng điều phối có thể lấy việc ra và gỡ đăng ký ngay sau Enqueue.
        if (cancellationToken.CanBeCanceled)
            entry.SetCancelRegistration(cancellationToken.Register(static state => ((DispatchQueue<TItem>)state!).Signal(), this));

        _inbox.Enqueue(entry);

        // Đua với DisposeAsync: vòng điều phối có thể đã thoát sau lượt rút hộp thư cuối cùng.
        if (Volatile.Read(ref _stopping) != 0)
            CancelInbox();
        else
            Signal();

        return entry.Ticket;
    }

    /// <summary>
    /// Báo hàng đợi xét lại các việc đang chờ khi điều kiện của chính sách đổi từ bên ngoài (đổi cấu hình trần).
    /// Không gọi thì việc bị <c>Wait</c> chỉ được xét lại khi có việc mới hoặc việc khác xong.
    /// </summary>
    public void Wake() => Signal();

    /// <summary>
    /// Dừng nhận việc, huỷ việc đang chờ, báo huỷ cho việc đang chạy rồi chờ chúng kết thúc. Việc nào bỏ qua
    /// token huỷ thì lệnh này chờ tới khi việc đó tự xong.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            try
            {
                _shutdown.Cancel();
            }
            finally
            {
                Signal();
            }
        }

        await _dispatcher.ConfigureAwait(false);
        CancelInbox();
    }

    private void Signal()
    {
        // Cờ giữ số đếm của semaphore tối đa 1: dồn nhiều lần đánh thức chỉ thành một lượt quét.
        if (Interlocked.Exchange(ref _signaled, 1) == 0)
            _wake.Release();
    }

    private void CancelInbox()
    {
        while (_inbox.TryDequeue(out var entry))
            entry.Cancel();
    }

    private async Task LoopAsync()
    {
        if (_options.Name is { } name && Thread.CurrentThread.Name is null)
            Thread.CurrentThread.Name = name;

        while (true)
        {
            // Hạ cờ TRƯỚC khi rút hộp thư: việc đến sau điểm này sẽ Release lại semaphore, không bị bỏ sót.
            Volatile.Write(ref _signaled, 0);
            TimeSpan? wakeAfter = null;
            try
            {
                DrainInbox();
                if (Volatile.Read(ref _stopping) != 0)
                {
                    CancelAllPending();
                    if (_running.Count == 0)
                        return;
                }
                else
                {
                    wakeAfter = Scan();
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            if (wakeAfter is { } delay)
                await _wake.WaitAsync(Clamp(delay));
            else
                await _wake.WaitAsync();
        }
    }

    private static TimeSpan Clamp(TimeSpan delay)
        => delay < MinDelay ? MinDelay : delay > MaxDelay ? MaxDelay : delay;

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out var entry))
        {
            if (entry.CallerToken.IsCancellationRequested || Volatile.Read(ref _stopping) != 0)
            {
                entry.Cancel();
                continue;
            }

            try
            {
                Enqueue(entry);
            }
            catch (Exception ex)
            {
                // Khoá null, Equals/GetHashCode của khoá ném...: việc đã ra khỏi hộp thư nên phải kết thúc vé ở đây,
                // không thì người gọi chờ mãi.
                ReportError(ex);
                entry.Fail(ex);
            }
        }
    }

    private void Enqueue(DispatchEntry<TItem> entry)
    {
        var key = _options.FairnessKey(entry.Item)
            ?? throw new InvalidOperationException("FairnessKey returned null.");

        if (!_buckets.TryGetValue(key, out var bucket))
        {
            // Vào từ điển trước, vòng xoay sau: Add ném (Equals/GetHashCode của khoá hỏng) thì vòng xoay
            // không bị dư một khoá mà từ điển không có — dư thì mọi lượt quét sau đều ném.
            var orderNode = new LinkedListNode<object>(key);
            bucket = new DispatchBucket<TItem>(orderNode);
            _buckets.Add(key, bucket);
            _keyOrder.AddLast(orderNode);
        }

        var list = bucket.ListOf(entry.IsPriority);
        var limit = entry.IsPriority ? _options.MaxPriorityPendingPerKey : _options.MaxPendingPerKey;
        if (limit is { } max && list.Count >= max)
        {
            RemoveBucketIfEmpty(key, bucket);
            entry.Fail(new DispatchQueueFullException(key, max));
            return;
        }

        entry.FairnessKey = key;
        entry.Node = list.AddLast(entry);
    }

    /// <summary>Cho chạy mọi việc đủ điều kiện; trả quãng chờ ngắn nhất mà chính sách hẹn xét lại (nếu có).</summary>
    private TimeSpan? Scan()
    {
        TimeSpan? wakeAfter = null;
        ScanPass(isPriority: true, ref wakeAfter);
        ScanPass(isPriority: false, ref wakeAfter);
        UpdateQueuePositions();
        return wakeAfter;
    }

    /// <summary>
    /// Xoay vòng các khoá: mỗi vòng mỗi khoá chạy tối đa một việc, khoá vừa chạy xuống cuối vòng;
    /// lặp tới khi một vòng không chạy thêm được việc nào.
    /// </summary>
    private void ScanPass(bool isPriority, ref TimeSpan? wakeAfter)
    {
        bool progressed;
        do
        {
            progressed = false;
            var node = _keyOrder.First;
            var remaining = _keyOrder.Count;
            while (node is not null && remaining-- > 0)
            {
                var next = node.Next;
                var bucket = _buckets[node.Value];
                if (TryStartOne(bucket.ListOf(isPriority), ref wakeAfter))
                {
                    progressed = true;
                    if (!bucket.IsEmpty)
                    {
                        _keyOrder.Remove(node);
                        _keyOrder.AddLast(node);
                    }
                }

                RemoveBucketIfEmpty(node.Value, bucket);
                node = next;
            }
        }
        while (progressed);
    }

    private bool TryStartOne(LinkedList<DispatchEntry<TItem>> list, ref TimeSpan? wakeAfter)
    {
        var node = list.First;
        while (node is not null)
        {
            var next = node.Next;
            var entry = node.Value;

            if (entry.CallerToken.IsCancellationRequested)
            {
                list.Remove(node);
                entry.Cancel();
                node = next;
                continue;
            }

            Models.AdmissionDecision decision;
            try
            {
                decision = _options.Policy.CanStart(entry.Item, entry.IsPriority);
            }
            catch (Exception ex)
            {
                ReportError(ex);
                list.Remove(node);
                entry.Fail(ex);
                node = next;
                continue;
            }

            switch (decision.Kind)
            {
                case AdmissionKind.Start:
                    list.Remove(node);
                    Launch(entry);
                    return true;
                case AdmissionKind.Delay:
                    if (wakeAfter is null || decision.RetryAfter < wakeAfter)
                        wakeAfter = decision.RetryAfter;
                    break;
            }

            node = next;
        }

        return false;
    }

    private void Launch(DispatchEntry<TItem> entry)
    {
        entry.Node = null;
        entry.ReleaseCancelRegistration();
        entry.SetQueuePosition(null);

        try
        {
            entry.Receipt = _options.Policy.OnStarted(entry.Item, entry.IsPriority);
        }
        catch (Exception ex)
        {
            // Không gọi OnFinished: theo hợp đồng, OnStarted ném là chưa giữ gì.
            ReportError(ex);
            entry.Fail(ex);
            return;
        }

        _running.Add(entry);
        _ = RunTrackedAsync(entry);
    }

    private async Task RunTrackedAsync(DispatchEntry<TItem> entry)
    {
        // Không tạo nguồn huỷ ghép khi người gọi không huỷ được: đỡ một lần cấp phát + đăng ký mỗi việc.
        CancellationTokenSource? linked = null;
        try
        {
            // Nằm trong try: tạo nguồn ghép mà ném (token của người gọi có vấn đề) thì vẫn phải nhả chỗ và kết thúc vé.
            linked = entry.CallerToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(entry.CallerToken, _shutdown.Token)
                : null;
            await entry.RunAsync(linked?.Token ?? _shutdown.Token);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            entry.Fail(ex);
        }
        finally
        {
            linked?.Dispose();
            _running.Remove(entry);
            try
            {
                _options.Policy.OnFinished(entry.Item, entry.IsPriority, entry.Receipt);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            Signal();
        }
    }

    private void CancelAllPending()
    {
        foreach (var bucket in _buckets.Values)
        {
            foreach (var entry in bucket.Priority)
                entry.Cancel();
            foreach (var entry in bucket.Normal)
                entry.Cancel();
        }

        _buckets.Clear();
        _keyOrder.Clear();
    }

    private void RemoveBucketIfEmpty(object key, DispatchBucket<TItem> bucket)
    {
        if (!bucket.IsEmpty)
            return;
        _buckets.Remove(key);
        if (bucket.OrderNode.List is not null)
            _keyOrder.Remove(bucket.OrderNode);
    }

    private void UpdateQueuePositions()
    {
        foreach (var bucket in _buckets.Values)
        {
            var position = 0;
            foreach (var entry in bucket.Priority)
                entry.SetQueuePosition(position++);
            foreach (var entry in bucket.Normal)
                entry.SetQueuePosition(position++);
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _options.OnInternalError?.Invoke(exception);
        }
        catch
        {
            // Người nhận lỗi tự ném thì bỏ qua: luồng điều phối chết là cả hàng đợi treo.
        }
    }
}
