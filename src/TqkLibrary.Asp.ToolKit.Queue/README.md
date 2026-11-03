# TqkLibrary.Asp.ToolKit.Queue

**English** · [Tiếng Việt](#tiếng-việt)

In-process dispatch queue with pluggable admission policies (concurrency caps, composite policies), fair round-robin scheduling by an app-defined key, bounded waiting lists and awaitable tickets.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Queue
```

## Usage

In-process dispatch queue: work items are submitted without blocking and started only when an admission policy lets
them run (for example concurrency caps). Waiting items are served round-robin by a fairness key, and each caller gets
an awaitable ticket. It does not depend on ASP.NET Core.

```csharp
var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule("global", 3), new CapRule(("customer", job.CustomerId), 1)]);

await using var queue = new DispatchQueue<Job>(new DispatchQueueOptions<Job>
{
    Policy = policy,
    FairnessKey = job => job.CustomerId,   // customers are served in turn
    MaxPendingPerKey = 100,                // more waiting items of one key => DispatchQueueFullException
});

var ticket = queue.Submit(job, async (item, ct) => await RunAsync(item, ct), isPriority: false, cancellationToken);
var result = await ticket.Completion;      // ticket.QueuePosition tells where it waits

// After a limit changes outside the queue (for example the cap configuration), re-check waiting items:
queue.Wake();
```

`RateLimitPolicy<TItem>` spaces out when work STARTS (it does not cap how many run at once), shared by all items.
Combine it with `ConcurrencyCapPolicy` through `CompositeAdmissionPolicy`; a blocked item gets `Delay`, so the queue
re-checks at the right moment without a `Wake()`:

```csharp
// "One mail every 2 minutes" and "at most 20 per 2 minutes", plus the usual concurrency cap.
var policy = new CompositeAdmissionPolicy<Job>(
    new RateLimitPolicy<Job>(new RateLimitOptions
    {
        MinInterval = TimeSpan.FromMinutes(2),   // min gap between two starts (Zero = off)
        MaxPerWindow = 20,                       // sliding window (null = off)
        Window = TimeSpan.FromMinutes(2),
        PriorityBypass = true,                   // priority items always start, but still count toward the limits
    }),
    new ConcurrencyCapPolicy<Job>(_ => [new CapRule("global", 3)]));
```

## Dependencies

Depends only on `Nito.AsyncEx.Context`; not tied to ASP.NET Core.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)

---

## Tiếng Việt

### Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Queue
```

### Cách dùng

Dispatch queue trong process: work item được submit mà không chặn và chỉ bắt đầu chạy khi một admission policy cho phép
(ví dụ giới hạn số lượng chạy đồng thời). Các item đang chờ được phục vụ theo round-robin theo một fairness key, và mỗi caller nhận
một ticket có thể await. Không phụ thuộc ASP.NET Core.

```csharp
var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule("global", 3), new CapRule(("customer", job.CustomerId), 1)]);

await using var queue = new DispatchQueue<Job>(new DispatchQueueOptions<Job>
{
    Policy = policy,
    FairnessKey = job => job.CustomerId,   // các customer được phục vụ lần lượt
    MaxPendingPerKey = 100,                // quá nhiều item chờ của cùng một key => DispatchQueueFullException
});

var ticket = queue.Submit(job, async (item, ct) => await RunAsync(item, ct), isPriority: false, cancellationToken);
var result = await ticket.Completion;      // ticket.QueuePosition cho biết vị trí đang chờ

// Sau khi một giới hạn thay đổi từ bên ngoài queue (ví dụ cấu hình cap), kiểm tra lại các item đang chờ:
queue.Wake();
```

`RateLimitPolicy<TItem>` giãn cách thời điểm công việc BẮT ĐẦU (không giới hạn số lượng chạy cùng lúc), dùng chung cho mọi item.
Kết hợp nó với `ConcurrencyCapPolicy` qua `CompositeAdmissionPolicy`; item bị chặn sẽ nhận `Delay`, nên queue
kiểm tra lại đúng thời điểm mà không cần `Wake()`:

```csharp
// "Mỗi 2 phút một mail" và "tối đa 20 mail trong 2 phút", cộng với concurrency cap thông thường.
var policy = new CompositeAdmissionPolicy<Job>(
    new RateLimitPolicy<Job>(new RateLimitOptions
    {
        MinInterval = TimeSpan.FromMinutes(2),   // khoảng cách tối thiểu giữa hai lần bắt đầu (Zero = tắt)
        MaxPerWindow = 20,                       // sliding window (null = tắt)
        Window = TimeSpan.FromMinutes(2),
        PriorityBypass = true,                   // item ưu tiên luôn được bắt đầu, nhưng vẫn được tính vào giới hạn
    }),
    new ConcurrencyCapPolicy<Job>(_ => [new CapRule("global", 3)]));
```

### Phụ thuộc

Chỉ phụ thuộc `Nito.AsyncEx.Context`; không gắn với ASP.NET Core.

### Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
