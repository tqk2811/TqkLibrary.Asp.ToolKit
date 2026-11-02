using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;
using TqkLibrary.Asp.ToolKit.Queue.Policies;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Queue.Tests;

/// <summary>
/// Hàng đợi điều phối một luồng: bộ đếm của chính sách không có khoá, nên mọi bài ở đây vừa kiểm hành vi
/// vừa gián tiếp kiểm điều kiện "chỉ luồng điều phối đụng tới chính sách".
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
    public async Task Ket_qua_cua_viec_ve_toi_nguoi_goi()
    {
        await using var queue = NewQueue(GlobalCap(1));

        var ticket = queue.Submit(new Job("a"), (job, _) => Task.FromResult(job.Customer + "!"));

        Assert.Equal("a!", await ticket.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Loi_cua_viec_ve_toi_nguoi_goi_va_tra_cho_cho_viec_sau()
    {
        var policy = GlobalCap(1);
        await using var queue = NewQueue(policy);

        var failed = queue.Submit<int>(new Job("a"), (_, _) => throw new InvalidOperationException("boom"));
        var next = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Completion.WaitAsync(Timeout));
        Assert.Equal(2, await next.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Khong_bao_gio_chay_qua_tran()
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
    public async Task Tran_chong_nhau_viec_phai_lot_moi_tran()
    {
        // Trần chung 3, trần mỗi customer 1: Customer "a" có 5 việc nhưng chỉ chạy 1 lúc một.
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
    public async Task Xoay_vong_khoa_cong_bang_Customer_den_sau_khong_phai_cho_het_hang_cua_Customer_khac()
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

        // Customer "b" không phải đứng sau cả ba việc của "a".
        Assert.True(order.IndexOf("b0") < order.IndexOf("a2"), string.Join(",", order));
    }

    [Fact]
    public async Task Viec_uu_tien_duoc_dung_phan_du_khi_tran_thuong_da_day()
    {
        await using var queue = NewQueue(GlobalCap(1, priorityExtra: 1));
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var normal = queue.Submit(new Job("a"), async (_, _) => { entered.SetResult(); await gate.Task; return 1; });
        await entered.Task.WaitAsync(Timeout);
        var normalWaiting = queue.Submit(new Job("a"), (_, _) => Task.FromResult(2));
        var priority = queue.Submit(new Job("a"), (_, _) => Task.FromResult(3), isPriority: true);

        // Việc thường thứ hai không được dùng phần dư; việc ưu tiên thì được, dù nộp sau.
        Assert.Equal(3, await priority.Completion.WaitAsync(Timeout));
        Assert.False(normalWaiting.Completion.IsCompleted);

        gate.SetResult();
        Assert.Equal(1, await normal.Completion.WaitAsync(Timeout));
        Assert.Equal(2, await normalWaiting.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Huy_khi_dang_cho_thi_ve_bi_huy_ngay_va_khong_chiem_cho()
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
    public async Task Huy_khi_dang_chay_thi_token_toi_viec_va_tran_duoc_nha()
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
    public async Task Hang_cho_cua_khoa_day_thi_tu_choi_nhung_viec_uu_tien_co_hang_rieng()
    {
        await using var queue = NewQueue(GlobalCap(1), maxPending: 1, maxPriorityPending: 1);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Chờ việc chặn THẬT SỰ chạy: QueuePosition cũng là null lúc việc còn nằm trong hộp thư chưa được xếp,
        // nên chờ theo nó thì việc chặn có thể vẫn chiếm chỗ hàng chờ và việc "waiting" bị từ chối nhầm.
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
    public async Task Chinh_sach_hen_gio_thi_hang_doi_tu_thuc_day_xet_lai()
    {
        var openAt = DateTime.UtcNow.AddMilliseconds(150);
        var policy = new DelegatePolicy((_, _) => DateTime.UtcNow >= openAt
            ? AdmissionDecision.Start
            : AdmissionDecision.Delay(openAt - DateTime.UtcNow));
        await using var queue = NewQueue(policy);

        var ticket = queue.Submit(new Job("a"), (_, _) => Task.FromResult(DateTime.UtcNow));

        // Không có sự kiện nào khác đánh thức: chỉ quãng hẹn giờ mới cho việc chạy.
        var startedAt = await ticket.Completion.WaitAsync(Timeout);
        Assert.True(startedAt >= openAt);
    }

    [Fact]
    public async Task Chinh_sach_chi_bi_goi_tren_mot_luong()
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
    public async Task Loi_cua_chinh_sach_khong_lam_chet_hang_doi()
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
    public async Task Dispose_huy_viec_dang_cho_bao_huy_viec_dang_chay_va_tu_choi_viec_moi()
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
    public async Task Bo_dem_ve_0_thi_xoa_khoa()
    {
        var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule(("customer", job.Customer), 2)]);
        await using var queue = NewQueue(policy);

        var tickets = Enumerable.Range(0, 30).Select(i => queue.Submit(new Job("t" + i), (_, _) => Task.FromResult(0))).ToList();
        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(Timeout);

        await WaitUntilAsync(() => policy.TrackedKeyCount == 0);
    }

    [Fact]
    public void Ghep_chinh_sach_cho_chay_khi_moi_chinh_sach_cho_chay()
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
    public async Task Khoa_cong_bang_null_thi_ve_bao_loi_khong_treo()
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
    public async Task Ghep_chinh_sach_chinh_sach_sau_nem_luc_bat_dau_thi_nha_cho_da_giu()
    {
        var cap = GlobalCap(1);
        await using var queue = NewQueue(new CompositeAdmissionPolicy<Job>(cap, new ThrowingOnStartPolicy()), errors: []);

        var failed = queue.Submit(new Job("a"), (_, _) => Task.FromResult(0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Completion.WaitAsync(Timeout));
        Assert.Equal(0, cap.TrackedKeyCount);
    }

    [Fact]
    public async Task Luat_doi_giua_luc_chay_va_luc_xong_van_nha_dung_khoa_da_giu()
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
    public async Task Wake_xet_lai_viec_dang_cho_khi_dieu_kien_ngoai_doi()
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
    public async Task Huy_khi_dang_chay_thi_ve_mang_dung_token_cua_nguoi_goi()
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
