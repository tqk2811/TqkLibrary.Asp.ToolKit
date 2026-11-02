using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;
using TqkLibrary.Asp.ToolKit.Queue.Policies;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Queue.Tests;

/// <summary>
/// Single-threaded dispatch queue: the policy's counters have no locks, so every test here checks behavior and
/// also indirectly checks the condition "only the dispatcher thread touches the policy".
/// </summary>
public sealed class DispatchQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed record Job(string Customer, string Name = "");

    private static DispatchQueue<Job> NewQueue(IAdmissionPolicy<Job> policy, int? maxPending = null, int? maxPriorityPending = null, List<Exception>? errors = null)
        => new(new DispatchQueueOptions<Job>
        {
            Policy = policy,
            FairnessKey = job => job.Customer,
            MaxPendingPerKey = maxPending,
            MaxPriorityPendingPerKey = maxPriorityPending,
            OnInternalError = errors is null ? null : ex => { lock (errors) errors.Add(ex); },
            Name = "test-dispatcher",
        });

    private static ConcurrencyCapPolicy<Job> GlobalCap(int max, int priorityExtra = 0)
        => new(_ => [new CapRule("global", max, priorityExtra)]);

    [Fact]
    public async Task Result_of_job_reaches_caller()
    {
        await using var queue = NewQueue(GlobalCap(1));

        var ticket = queue.Submit(new Job("a"), (job, _) => Task.FromResult(job.Customer + "!"));

        Assert.Equal("a!", await ticket.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Job_exception_reaches_caller_and_frees_slot_for_next_job()
    {
        var policy = GlobalCap(1);
        await using var queue = NewQueue(policy);

        var failed = queue.Submit<int>(new Job("a"), (_, _) => throw new InvalidOperationException("boom"));
        var next = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Completion.WaitAsync(Timeout));
        Assert.Equal(2, await next.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Never_exceeds_cap()
    {
        await using var queue = NewQueue(GlobalCap(2));
        var running = 0;
        var peak = 0;

        var tickets = Enumerable.Range(0, 20).Select(i => queue.Submit(new Job("t" + (i % 3)), async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref running);
            return 0;
        })).ToList();

        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(Timeout);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Overlapping_caps_job_must_pass_every_cap()
    {
        // Global cap 3, per-customer cap 1: customer "a" has 5 items but only 1 runs at a time.
        var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule("global", 3), new CapRule(("customer", job.Customer), 1)]);
        await using var queue = NewQueue(policy);
        var runningA = 0;
        var peakA = 0;
        var runningAll = 0;
        var peakAll = 0;

        var tickets = Enumerable.Range(0, 10).Select(i => queue.Submit(new Job(i < 5 ? "a" : "b" + i), async (job, ct) =>
        {
            InterlockedMax(ref peakAll, Interlocked.Increment(ref runningAll));
            if (job.Customer == "a")
                InterlockedMax(ref peakA, Interlocked.Increment(ref runningA));
            await Task.Delay(10, ct);
            if (job.Customer == "a")
                Interlocked.Decrement(ref runningA);
            Interlocked.Decrement(ref runningAll);
            return 0;
        })).ToList();

        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(Timeout);
        Assert.Equal(1, peakA);
        Assert.True(peakAll <= 3, $"peakAll={peakAll}");
    }

    [Fact]
    public async Task Fairness_round_robin_late_customer_does_not_wait_for_other_customer_backlog()
    {
        await using var queue = NewQueue(GlobalCap(1));
        var gate = new TaskCompletionSource();
        var order = new List<string>();

        var blocker = queue.Submit(new Job("x", "blocker"), async (_, _) => { await gate.Task; return 0; });
        var tickets = new List<DispatchTicket<int>>();
        for (var i = 0; i < 3; i++)
            tickets.Add(queue.Submit(new Job("a", "a" + i), (job, _) => { lock (order) order.Add(job.Name); return Task.FromResult(0); }));
        tickets.Add(queue.Submit(new Job("b", "b0"), (job, _) => { lock (order) order.Add(job.Name); return Task.FromResult(0); }));
        await WaitUntilAsync(() => tickets[^1].Completion.IsCompleted || tickets[^1].QueuePosition is not null);

        gate.SetResult();
        await Task.WhenAll(tickets.Select(t => t.Completion).Append(blocker.Completion)).WaitAsync(Timeout);

        // Customer "b" does not have to wait behind all three of "a"'s items.
        Assert.True(order.IndexOf("b0") < order.IndexOf("a2"), string.Join(",", order));
    }

    [Fact]
    public async Task Priority_job_uses_extra_slots_when_normal_cap_is_full()
    {
        await using var queue = NewQueue(GlobalCap(1, priorityExtra: 1));
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var normal = queue.Submit(new Job("a"), async (_, _) => { entered.SetResult(); await gate.Task; return 1; });
        await entered.Task.WaitAsync(Timeout);
        var normalWaiting = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));
        var priority = queue.Submit(new Job("a"), (_, _) => Task.FromResult(3), isPriority: true);

        // The second normal item must not use the surplus; the priority item may, even though it was submitted later.
        Assert.Equal(3, await priority.Completion.WaitAsync(Timeout));
        Assert.False(normalWaiting.Completion.IsCompleted);

        gate.SetResult();
        Assert.Equal(1, await normal.Completion.WaitAsync(Timeout));
        Assert.Equal(2, await normalWaiting.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Cancel_while_pending_cancels_ticket_immediately_and_frees_slot()
    {
        var policy = GlobalCap(1);
        await using var queue = NewQueue(policy);
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var started = false;

        var blocker = queue.Submit(new Job("a"), async (_, _) => { await gate.Task; return 0; });
        var waiting = queue.Submit(new Job("a"), (_, _) => { started = true; return Task.FromResult(0); }, cancellationToken: cts.Token);
        await WaitUntilAsync(() => waiting.QueuePosition is not null);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Completion.WaitAsync(Timeout));
        Assert.Null(waiting.QueuePosition);

        gate.SetResult();
        await blocker.Completion.WaitAsync(Timeout);
        await WaitUntilAsync(() => policy.TrackedKeyCount == 0);
        Assert.False(started);
    }

    [Fact]
    public async Task Cancel_while_running_reaches_job_and_releases_cap()
    {
        var policy = GlobalCap(1);
        await using var queue = NewQueue(policy);
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();

        var running = queue.Submit(new Job("a"), async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout, ct);
            return 0;
        }, cancellationToken: cts.Token);
        await entered.Task.WaitAsync(Timeout);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Completion.WaitAsync(Timeout));

        var next = queue.Submit(new Job("a"), (_, _) => Task.FromResult(7));
        Assert.Equal(7, await next.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Full_pending_queue_rejects_normal_jobs_but_priority_has_own_queue()
    {
        await using var queue = NewQueue(GlobalCap(1), maxPending: 1, maxPriorityPending: 1);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Wait for the blocking work to ACTUALLY run: QueuePosition is also null while the item is still in the mailbox, not yet queued,
        // so waiting on it could leave the blocker occupying the waiting queue and the "waiting" item wrongly rejected.
        var blocker = queue.Submit(new Job("a"), async (_, _) => { started.SetResult(); await gate.Task; return 0; });
        await started.Task.WaitAsync(Timeout);
        var waiting = queue.Submit(new Job("a"), (_, _) => Task.FromResult(1));
        var rejected = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));
        var priority = queue.Submit(new Job("a"), (_, _) => Task.FromResult(3), isPriority: true);

        var ex = await Assert.ThrowsAsync<DispatchQueueFullException>(() => rejected.Completion.WaitAsync(Timeout));
        Assert.Equal("a", ex.FairnessKey);

        gate.SetResult();
        Assert.Equal(1, await waiting.Completion.WaitAsync(Timeout));
        Assert.Equal(3, await priority.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Policy_delay_wakes_queue_to_recheck()
    {
        var openAt = DateTime.UtcNow.AddMilliseconds(150);
        var policy = new DelegatePolicy((_, _) => DateTime.UtcNow >= openAt
            ? AdmissionDecision.Start
            : AdmissionDecision.Delay(openAt - DateTime.UtcNow));
        await using var queue = NewQueue(policy);

        var ticket = queue.Submit(new Job("a"), (_, _) => Task.FromResult(DateTime.UtcNow));

        // No other event wakes it: only the timed delay lets the work start.
        var startedAt = await ticket.Completion.WaitAsync(Timeout);
        Assert.True(startedAt >= openAt);
    }

    [Fact]
    public async Task Policy_is_called_on_a_single_thread()
    {
        var threads = new HashSet<int>();
        var policy = new DelegatePolicy((_, _) => { threads.Add(Environment.CurrentManagedThreadId); return AdmissionDecision.Start; },
            onEvent: () => threads.Add(Environment.CurrentManagedThreadId));
        await using var queue = NewQueue(policy);

        var tickets = Enumerable.Range(0, 50).Select(i => queue.Submit(new Job("t" + (i % 5)), async (_, _) =>
        {
            await Task.Yield();
            return Environment.CurrentManagedThreadId;
        })).ToList();
        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(Timeout);
        await queue.DisposeAsync();

        Assert.Single(threads);
    }

    [Fact]
    public async Task Policy_exception_does_not_kill_queue()
    {
        var errors = new List<Exception>();
        var policy = new DelegatePolicy((job, _) => job.Name == "bad" ? throw new InvalidOperationException("policy") : AdmissionDecision.Start);
        await using var queue = NewQueue(policy, errors: errors);

        var bad = queue.Submit(new Job("a", "bad"), (_, _) => Task.FromResult(0));
        var good = queue.Submit(new Job("a", "good"), (_, _) => Task.FromResult(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.Completion.WaitAsync(Timeout));
        Assert.Equal(1, await good.Completion.WaitAsync(Timeout));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task Dispose_cancels_pending_signals_running_and_rejects_new_jobs()
    {
        var queue = NewQueue(GlobalCap(1));
        var entered = new TaskCompletionSource();

        var running = queue.Submit(new Job("a"), async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout, ct);
            return 0;
        });
        var waiting = queue.Submit(new Job("a"), (_, _) => Task.FromResult(1));
        await entered.Task.WaitAsync(Timeout);

        await queue.DisposeAsync().AsTask().WaitAsync(Timeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Completion);
        var late = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => late.Completion);
    }

    [Fact]
    public async Task Counter_back_to_zero_removes_key()
    {
        var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule(("customer", job.Customer), 2)]);
        await using var queue = NewQueue(policy);

        var tickets = Enumerable.Range(0, 30).Select(i => queue.Submit(new Job("t" + i), (_, _) => Task.FromResult(0))).ToList();
        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(Timeout);

        await WaitUntilAsync(() => policy.TrackedKeyCount == 0);
    }

    [Fact]
    public void Composite_starts_only_when_every_policy_starts()
    {
        var start = new DelegatePolicy((_, _) => AdmissionDecision.Start);
        var wait = new DelegatePolicy((_, _) => AdmissionDecision.Wait);
        var delay1 = new DelegatePolicy((_, _) => AdmissionDecision.Delay(TimeSpan.FromSeconds(1)));
        var delay5 = new DelegatePolicy((_, _) => AdmissionDecision.Delay(TimeSpan.FromSeconds(5)));
        var job = new Job("a");

        Assert.Equal(AdmissionDecision.Start, new CompositeAdmissionPolicy<Job>(start, start).CanStart(job, false));
        Assert.Equal(AdmissionDecision.Wait, new CompositeAdmissionPolicy<Job>(start, delay1, wait).CanStart(job, false));
        Assert.Equal(AdmissionDecision.Delay(TimeSpan.FromSeconds(5)), new CompositeAdmissionPolicy<Job>(delay1, start, delay5).CanStart(job, false));
    }

    private sealed class DelegatePolicy(Func<Job, bool, AdmissionDecision> canStart, Action? onEvent = null) : IAdmissionPolicy<Job>
    {
        public AdmissionDecision CanStart(Job item, bool isPriority) => canStart(item, isPriority);

        public object? OnStarted(Job item, bool isPriority)
        {
            onEvent?.Invoke();
            return null;
        }

        public void OnFinished(Job item, bool isPriority, object? receipt) => onEvent?.Invoke();
    }

    private sealed class ThrowingOnStartPolicy : IAdmissionPolicy<Job>
    {
        public AdmissionDecision CanStart(Job item, bool isPriority) => AdmissionDecision.Start;

        public object? OnStarted(Job item, bool isPriority) => throw new InvalidOperationException("start");

        public void OnFinished(Job item, bool isPriority, object? receipt)
        {
        }
    }

    [Fact]
    public async Task Null_fairness_key_fails_ticket_without_hanging()
    {
        var errors = new List<Exception>();
        await using var queue = new DispatchQueue<Job>(new DispatchQueueOptions<Job>
        {
            Policy = GlobalCap(1),
            FairnessKey = job => job.Customer == "null" ? null! : job.Customer,
            OnInternalError = ex => { lock (errors) errors.Add(ex); },
        });

        var bad = queue.Submit(new Job("null"), (_, _) => Task.FromResult(0));
        var good = queue.Submit(new Job("a"), (_, _) => Task.FromResult(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.Completion.WaitAsync(Timeout));
        Assert.Equal(1, await good.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Composite_releases_held_slots_when_later_policy_throws_on_start()
    {
        var cap = GlobalCap(1);
        await using var queue = NewQueue(new CompositeAdmissionPolicy<Job>(cap, new ThrowingOnStartPolicy()), errors: []);

        var failed = queue.Submit(new Job("a"), (_, _) => Task.FromResult(0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Completion.WaitAsync(Timeout));
        Assert.Equal(0, cap.TrackedKeyCount);
    }

    [Fact]
    public async Task Rules_changed_while_running_still_release_held_keys()
    {
        var keyName = "old";
        var policy = new ConcurrencyCapPolicy<Job>(_ => [new CapRule(keyName, 1)]);
        await using var queue = NewQueue(policy);
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var running = queue.Submit(new Job("a"), async (_, _) => { entered.SetResult(); await gate.Task; return 0; });
        await entered.Task.WaitAsync(Timeout);
        keyName = "new";
        gate.SetResult();
        await running.Completion.WaitAsync(Timeout);

        await WaitUntilAsync(() => policy.TrackedKeyCount == 0);
    }

    [Fact]
    public async Task Wake_rechecks_pending_jobs_when_external_condition_changes()
    {
        var open = false;
        await using var queue = NewQueue(new DelegatePolicy((_, _) => Volatile.Read(ref open) ? AdmissionDecision.Start : AdmissionDecision.Wait));

        var ticket = queue.Submit(new Job("a"), (_, _) => Task.FromResult(1));
        await WaitUntilAsync(() => ticket.QueuePosition is not null);

        Volatile.Write(ref open, true);
        queue.Wake();

        Assert.Equal(1, await ticket.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Cancel_while_running_ticket_carries_caller_token()
    {
        await using var queue = NewQueue(GlobalCap(1));
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();

        var running = queue.Submit(new Job("a"), async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout, ct);
            return 0;
        }, cancellationToken: cts.Token);
        await entered.Task.WaitAsync(Timeout);
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Completion.WaitAsync(Timeout));
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met.");
            await Task.Delay(5);
        }
    }
}
