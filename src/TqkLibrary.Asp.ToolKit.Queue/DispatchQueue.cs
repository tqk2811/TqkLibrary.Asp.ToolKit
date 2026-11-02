using System.Collections.Concurrent;

using TqkLibrary.Asp.ToolKit.Queue.Enums;

using Nito.AsyncEx;

namespace TqkLibrary.Asp.ToolKit.Queue;

/// <summary>
/// The queue has its own dispatcher thread (a <c>LongRunning</c> thread running <see cref="AsyncContext"/>):
/// <list type="number">
/// <item>Outside, <see cref="Submit"/> pushes work into a mailbox (<see cref="ConcurrentQueue{T}"/>) - the only place where threads contend.</item>
/// <item>The dispatcher thread moves work from the mailbox into the waiting queues by fairness key, evaluating each item with
/// <see cref="Interfaces.IAdmissionPolicy{TItem}"/>; eligible items are removed from the waiting queue, added to the running set and started.</item>
/// <item>When work finishes: the wrapper returns to the dispatcher thread (via SynchronizationContext), removes it from the running set, tells the policy to release its slot, and wakes up to rescan.</item>
/// </list>
/// The waiting queues, running set and policy state are touched only by the dispatcher thread, so no locking is needed.
/// Priority work is evaluated first and may use the surplus the policy reserves for it; it does not take over slots of running work.
/// The queue owns the dispatcher thread and its internal cancellation source, released in <see cref="DisposeAsync"/>;
/// the policy is supplied from outside, so it is NOT disposed here.
/// </summary>
public sealed class DispatchQueue<TItem> : IAsyncDisposable
{
    // SemaphoreSlim only allocates a kernel handle when someone reads AvailableWaitHandle - nobody does here.
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ConcurrentQueue<DispatchEntry<TItem>> _inbox = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatchQueueOptions<TItem> _options;
    private readonly Task _dispatcher;

    // Used only by the dispatcher thread.
    private readonly Dictionary<object, DispatchBucket<TItem>> _buckets = new();
    private readonly LinkedList<object> _keyOrder = new();
    private readonly HashSet<DispatchEntry<TItem>> _running = new();

    private int _signaled;
    private int _stopping;

    // Clamp the timed wait: SemaphoreSlim.WaitAsync does not accept more than int.MaxValue ms, and a repeated Delay(0)
    // would become a busy spin.
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
        // The dispatch loop only reports errors inside each tick; if one escaped the loop, the whole queue would hang unnoticed.
        _dispatcher.ContinueWith(
            t => ReportError(new InvalidOperationException("Dispatch loop stopped unexpectedly", t.Exception!.GetBaseException())),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Submits a work item. Does not block: the ticket is returned immediately, the result is in <see cref="DispatchTicket{TResult}.Completion"/>.
    /// <paramref name="cancellationToken"/> can cancel both while waiting (removed from the queue) and while running (passed into <paramref name="work"/>).
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

        // Register BEFORE entering the mailbox: the dispatcher thread may take the item out and unregister right after Enqueue.
        if (cancellationToken.CanBeCanceled)
            entry.SetCancelRegistration(cancellationToken.Register(static state => ((DispatchQueue<TItem>)state!).Signal(), this));

        _inbox.Enqueue(entry);

        // Race with DisposeAsync: the dispatch loop may have exited after its final mailbox drain.
        if (Volatile.Read(ref _stopping) != 0)
            CancelInbox();
        else
            Signal();

        return entry.Ticket;
    }

    /// <summary>
    /// Tells the queue to re-evaluate waiting items when the policy's conditions change externally (e.g. a cap setting changed).
    /// Without calling this, an item blocked with <c>Wait</c> is re-evaluated only when new work arrives or other work finishes.
    /// </summary>
    public void Wake() => Signal();

    /// <summary>
    /// Stops accepting work, cancels waiting work, signals cancellation to running work, then waits for it to finish. Work that ignores
    /// the cancellation token makes this wait until the work finishes by itself.
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
        // The flag keeps the semaphore count at most 1: many wake-ups collapse into a single scan.
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
            // Lower the flag BEFORE draining the mailbox: work arriving after this point will Release the semaphore again and is not missed.
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
                // Null key, key's Equals/GetHashCode throwing...: the item has already left the mailbox, so the ticket must be completed here,
                // otherwise the caller waits forever.
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
            // Dictionary first, rotation second: if Add throws (broken key Equals/GetHashCode), the rotation
            // does not end up with a key the dictionary lacks - if it did, every later scan would throw.
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

    /// <summary>Starts all eligible work; returns the shortest delay at which the policy asks to be re-evaluated (if any).</summary>
    private TimeSpan? Scan()
    {
        TimeSpan? wakeAfter = null;
        ScanPass(isPriority: true, ref wakeAfter);
        ScanPass(isPriority: false, ref wakeAfter);
        UpdateQueuePositions();
        return wakeAfter;
    }

    /// <summary>
    /// Rotates through the keys: each round each key runs at most one item, and the key that just ran moves to the end of the rotation;
    /// repeats until a round starts no more work.
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
            // Do not call OnFinished: by contract, if OnStarted throws it has not held anything.
            ReportError(ex);
            entry.Fail(ex);
            return;
        }

        _running.Add(entry);
        _ = RunTrackedAsync(entry);
    }

    private async Task RunTrackedAsync(DispatchEntry<TItem> entry)
    {
        // Do not create a linked cancellation source when the caller cannot cancel: saves one allocation + registration per item.
        CancellationTokenSource? linked = null;
        try
        {
            // Inside the try: if creating the linked source throws (problem with the caller's token), the slot must still be released and the ticket completed.
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
            // If the handler throws, ignore it: if the dispatcher thread died, the whole queue would hang.
        }
    }
}
