using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using TqkLibrary.Asp.ToolKit.Queue.Enums;
using TqkLibrary.Asp.ToolKit.Queue.Models;
using TqkLibrary.Asp.ToolKit.Queue.Policies;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Queue.Tests;

/// <summary>Rate limit on work starts, checked with a fake clock so there is no real waiting.</summary>
public sealed class RateLimitPolicyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed record Job(string Customer);

    private static readonly Job Item = new("a");

    private static RateLimitPolicy<Job> New(FakeTimeProvider clock, RateLimitOptions options) => new(options, clock);

    /// <summary>Evaluates, and if allowed to start, records the start mark the way the queue does.</summary>
    private static AdmissionDecision TryStart(RateLimitPolicy<Job> policy, bool isPriority = false)
    {
        var decision = policy.CanStart(Item, isPriority);
        if (decision.Kind == AdmissionKind.Start)
            policy.OnStarted(Item, isPriority);
        return decision;
    }

    [Fact]
    public void MinInterval_second_job_delayed_by_remaining_time()
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
    public void MaxPerWindow_job_N_plus_1_waits_until_oldest_start_leaves_window()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MaxPerWindow = 3, Window = TimeSpan.FromMinutes(2) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=0
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=10
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=20

        var blocked = TryStart(policy);                              // the oldest mark t=0 leaves the window at t=120
        Assert.Equal(AdmissionKind.Delay, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(100), blocked.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(100));                    // t=120: the mark t=0 has just expired
        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // new mark t=120

        // Sliding window: now holds t=10, t=20, t=120 => the next start must wait until t=10 leaves (t=130).
        var next = TryStart(policy);
        Assert.Equal(AdmissionKind.Delay, next.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), next.RetryAfter);
    }

    [Fact]
    public void Combined_longer_window_wait_wins()
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

        // t=10: MinInterval requires 10s, the window requires 50s (mark t=0 leaves at t=60) => 50s.
        Assert.Equal(TimeSpan.FromSeconds(50), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Combined_longer_MinInterval_wait_wins()
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

        // MinInterval requires 25s (until t=60), the window requires only 5s (mark t=0 leaves at t=40) => 25s.
        Assert.Equal(TimeSpan.FromSeconds(25), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Priority_bypass_starts_while_blocked_and_still_counts_as_last_start()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(60) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);    // t=0
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AdmissionKind.Delay, TryStart(policy).Kind);    // normal work is blocked
        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);   // priority starts immediately, mark moves to t=10

        // Normal work must wait counted from the priority work's mark (t=10), not t=0.
        Assert.Equal(TimeSpan.FromSeconds(60), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void Priority_bypass_counts_toward_window()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MaxPerWindow = 1, Window = TimeSpan.FromSeconds(60) });

        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), TryStart(policy).RetryAfter);
    }

    [Fact]
    public void PriorityBypass_false_blocks_priority_jobs_too()
    {
        var clock = new FakeTimeProvider();
        var policy = New(clock, new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(60), PriorityBypass = false });

        Assert.Equal(AdmissionKind.Start, TryStart(policy, isPriority: true).Kind);
        var blocked = TryStart(policy, isPriority: true);
        Assert.Equal(AdmissionKind.Delay, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), blocked.RetryAfter);
    }

    [Fact]
    public void No_limits_configured_always_starts()
    {
        var policy = New(new FakeTimeProvider(), new RateLimitOptions());

        for (var i = 0; i < 5; i++)
            Assert.Equal(AdmissionKind.Start, TryStart(policy).Kind);
    }

    [Fact]
    public void Invalid_options_throw_ArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MinInterval = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 0, Window = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = -1, Window = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy<Job>(new RateLimitOptions { MaxPerWindow = 3, Window = TimeSpan.FromSeconds(-1) }));
    }

    /// <summary>
    /// DispatchQueue schedules re-evaluation with SemaphoreSlim.WaitAsync(timeout) on the real clock and does not accept a TimeProvider,
    /// so the integration test uses a very short real MinInterval instead of advancing a fake clock.
    /// </summary>
    [Fact]
    public async Task Integration_DispatchQueue_second_job_starts_no_earlier_than_MinInterval()
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
        // Allow a small tolerance: the policy's mark is recorded before the work body runs and reads the clock.
        Assert.True(gap >= interval - TimeSpan.FromMilliseconds(30), $"Gap was {gap}.");
    }
}
