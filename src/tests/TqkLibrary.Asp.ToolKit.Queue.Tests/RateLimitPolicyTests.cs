using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using TqkLibrary.Asp.ToolKit.Queue.Enums;
using TqkLibrary.Asp.ToolKit.Queue.Models;
using TqkLibrary.Asp.ToolKit.Queue.Policies;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Queue.Tests;

/// <summary>Giới hạn tốc độ bắt đầu việc, kiểm bằng đồng hồ giả nên không phải chờ thật.</summary>
public sealed class RateLimitPolicyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed record Job(string Customer);

    private static readonly Job Item = new("a");

    private static RateLimitPolicy<Job> New(FakeTimeProvider clock, RateLimitOptions options) => new(options, clock);

    /// <summary>Xét rồi, nếu được chạy thì ghi mốc bắt đầu như hàng đợi làm.</summary>
    private static AdmissionDecision TryStart(RateLimitPolicy<Job> policy, bool isPriority = false)
    {
        var decision = policy.CanStart(Item, isPriority);
        if (decision.Kind == AdmissionKind.Start)
            policy.OnStarted(Item, isPriority);
        return decision;
    }

    [Fact]
    public void MinInterval_viec_thu_hai_bi_delay_dung_quang_con_lai()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MinInterval = TimeSpan.FromMinutes(2) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);

        clock.Advance(TimeSpan.FromSeconds(30));
        var blocked = TryStart(policy);
        Assert.Equal(AdmissionKind.Delay, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), blocked.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(TimeSpan.FromSeconds(1), TryStart(policy).RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);
    }

    [Fact]
    public void MaxPerWindow_viec_N_cong_1_cho_toi_khi_moc_cu_nhat_ra_khoi_cua_so()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MaxPerWindow = 3, Window = TimeSpan.FromMinutes(2) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=0
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=10
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=20

        var blocked = TryStart(policy);                              // mốc cũ nhất t=0 ra khỏi cửa sổ lúc t=120
        Assert.Equal(AdmissionKind.Delay, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(100), blocked.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(100));                    // t=120: mốc t=0 vừa hết hạn
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // mốc mới t=120

        // Cửa sổ trượt: giờ còn t=10, t=20, t=120 => lượt kế phải chờ tới khi t=10 ra (t=130).
        var next = TryStart(policy);
        Assert.Equal(AdmissionKind.Delay, next.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), next.RetryAfter);
    }

    [Fact]
    public void Ket_hop_cua_so_dai_hon_thi_lay_quang_cua_so()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions
        {
            MinInterval = TimeSpan.FromSeconds(10),
            MaxPerWindow = 2,
            Window = TimeSpan.FromSeconds(60),
        });

        TryStart(policy);                                            // t=0
        clock.Advance(TimeSpan.FromSeconds(10));
        TryStart(policy);                                            // t=10

        // t=10: MinInterval đòi 10s, cửa sổ đòi 50s (mốc t=0 ra lúc t=60) => 50s.
        Assert.Equal(TimeSpan.FromSeconds(50), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Ket_hop_MinInterval_dai_hon_thi_lay_quang_MinInterval()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions
        {
            MinInterval = TimeSpan.FromSeconds(30),
            MaxPerWindow = 2,
            Window = TimeSpan.FromSeconds(40),
        });

        TryStart(policy);                                            // t=0
        clock.Advance(TimeSpan.FromSeconds(30));
        TryStart(policy);                                            // t=30
        clock.Advance(TimeSpan.FromSeconds(5));                      // t=35

        // MinInterval đòi 25s (tới t=60), cửa sổ chỉ đòi 5s (mốc t=0 ra lúc t=40) => 25s.
        Assert.Equal(TimeSpan.FromSeconds(25), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Viec_uu_tien_bypass_van_start_khi_dang_bi_chan_va_van_duoc_tinh_vao_moc()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(60) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=0
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Delay, TryStart(policy).Kind);    // việc thường bị chặn
        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);   // ưu tiên chạy ngay, mốc dời sang t=10

        // Việc thường phải chờ tính từ mốc của việc ưu tiên (t=10), không phải t=0.
        Assert.Equal(TimeSpan.FromSeconds(60), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Viec_uu_tien_bypass_duoc_tinh_vao_cua_so()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MaxPerWindow = 1, Window = TimeSpan.FromSeconds(60) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void PriorityBypass_false_viec_uu_tien_cung_bi_chan()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(60), PriorityBypass = false });

        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);
        var blocked = TryStart(policy, isPriority: true);
        Assert.Equal(AdmissionKind.Delay, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), blocked.RetryAfter);
    }

    [Fact]
    public void Khong_cau_hinh_gi_thi_luon_start()
    {
        var policy = New(new FakeTimeProvider(), new RateLimitOptions());

        for (var i = 0; i < 5; i++)
            Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);
    }

    [Fact]
    public void Options_sai_nem_ArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 0, Window = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = -1, Window = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 3, Window = TimeSpan.FromSeconds(-1) }));
    }

    /// <summary>
    /// DispatchQueue hẹn xét lại bằng SemaphoreSlim.WaitAsync(timeout) theo đồng hồ thật, không nhận TimeProvider,
    /// nên bài tích hợp dùng MinInterval thật rất ngắn thay vì tua đồng hồ giả.
    /// </summary>
    [Fact]
    public async Task Tich_hop_DispatchQueue_viec_hai_bat_dau_khong_som_hon_MinInterval()
    {
        var interval = TimeSpan.FromMilliseconds(200);
        var policy = new CompositeAdmissionPolicy<Job>(
            new RateLimitPolicy<Job>(new RateLimitOptions { MinInterval = interval }),
            new ConcurrencyCapPolicy<Job>(_ => [new CapRule("global", 5)]));
        await using var queue = new DispatchQueue<Job>(new DispatchQueueOptions<Job>
        {
            Policy = policy,
            FairnessKey = job => job.Customer,
            Name = "test-ratelimit",
        });

        var startTimes = new ConcurrentDictionary<string, long>();
        Task<int> Run(string name) => queue.Submit(new Job(name), (job, _) =>
        {
            startTimes[job.Customer] = Stopwatch.GetTimestamp();
            return Task.FromResult(0);
        }).Completion;

        var first = Run("one");
        var second = Run("two");
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        var gap = Stopwatch.GetElapsedTime(startTimes["one"], startTimes["two"]);
        // Trừ chút sai số: mốc của chính sách ghi trước khi thân việc chạy và đọc đồng hồ.
        Assert.True(gap >= interval - TimeSpan.FromMilliseconds(30), $"Gap was {gap}.");
    }
}
