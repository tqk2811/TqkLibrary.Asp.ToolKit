# TqkLibrary.Asp.ToolKit.Caching

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Caching/README-vi.md)

In-process stale-while-refresh caches (single value and keyed) with group-based invalidation through a registry keyed by an app-defined enum, plus an optional HTTP channel to invalidate caches in another process.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Caching
```

## Usage

In-process stale-while-refresh caches for shared, read-heavy data, invalidated by groups that the app names with
its own enum.

```csharp
public enum CacheGroup { News, Settings }

builder.Services.AddTqkCaching<CacheGroup>();

public sealed class NewsCache
{
    public NewsCache(CacheRegistry<CacheGroup> registry)
    {
        // Create caches ONCE (singleton), never per request.
        Latest = registry.CreateKeyed<int, IReadOnlyList<NewsItem>>(TimeSpan.FromSeconds(60), 8, CacheGroup.News);
    }

    public KeyedStaleWhileRefreshCache<int, IReadOnlyList<NewsItem>> Latest { get; }
}

// Read: the loader must fetch inside the lambda, use the token it receives AND enforce its own timeout
// (the first load has no internal timeout and holds the lock for every caller without an old value).
var items = await newsCache.Latest.GetAsync(10, ct => repository.GetLatestAsync(10, ct));

// Write path, after the save succeeded:
invalidator.Invalidate(CacheGroup.News);   // ICacheInvalidator<CacheGroup>
```

Optional cross-process invalidation (for example API → web front end):

```csharp
// Sender (process that writes data)
builder.Services.AddTqkCacheInvalidationSender<CacheGroup>(o =>
{
    o.TargetUrl = "http://localhost:5000/_internal/cache/invalidate";
    o.Key = builder.Configuration["Caching:InternalKey"]!;
});
// A batch still waiting for the debounce window or a retry when the application stops is dropped;
// the receiving cache then expires by TTL.

// Receiver (process that caches); loopback only, key protected, 404 otherwise
builder.Services.AddTqkCacheInvalidationEndpoint<CacheGroup>(o => o.Key = builder.Configuration["Caching:InternalKey"]);
app.UseTqkCacheInvalidationEndpoint<CacheGroup>();   // before UseForwardedHeaders / UseHttpsRedirection
```

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. No dependency on other packages of this toolkit.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
