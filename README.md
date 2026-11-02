# TqkLibrary.Asp.ToolKit

Tiếng Việt: [README-vi.md](README-vi.md) (hướng dẫn đầy đủ).

## TqkLibrary.Asp.ToolKit.Caching

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

## TqkLibrary.Asp.ToolKit.Auth.Web

Web front end (MVC/Razor) that signs in against a separate JWT API. The API access and refresh tokens live only in
the encrypted, HttpOnly auth cookie; the cookie principal carries only the API session id. A `DelegatingHandler`
attaches the bearer, refreshes it before expiry or once after a 401, and rewrites the cookie. The current user
(name, roles, permissions) is asked from the API on each request (short cache).

The API must NOT rotate refresh tokens as single-use (see `Auth.Api`).

Implement `IAuthApiClient` on top of your API client:

```csharp
public sealed class MyAuthApiClient(IMyApi api) : IAuthApiClient
{
    // refresh / me rejected by the API (401/403) MUST throw SessionExpiredException; any other exception is
    // treated as transient and the user is NOT signed out.
    public async Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await api.RefreshAsync(refreshToken, ct);   // returns HttpResponseMessage
        response.ThrowIfSessionRejected();                               // 401/403 -> SessionExpiredException
        response.EnsureSuccessStatusCode();
        return await ReadTokensAsync(response, ct);
    }

    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await api.GetMeAsync(ct);
        response.ThrowIfSessionRejected();
        response.EnsureSuccessStatusCode();
        return await ReadUserAsync(response, ct);
    }

    public Task LogoutAsync(CancellationToken ct) => api.LogoutAsync(ct);
}
```

Registration:

```csharp
builder.Services.AddTqkDataProtectionKeyRing(builder.Environment);   // keep sessions across restarts
builder.Services.AddTqkWebAuth(
    cookie => { cookie.Cookie.Name = "app_auth"; cookie.LoginPath = "/account/login"; },
    options => options.AnonymousPathSuffixes.Add("/auth/login"));
builder.Services.AddHttpClient<IMyApi, MyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
builder.Services.AddScoped<IAuthApiClient, MyAuthApiClient>();

app.UseAuthentication();
app.UseTqkWebSession();   // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();

// Login action: tokens returned by the API login endpoint
await sessionManager.SignInAsync(HttpContext, tokens, persistent: model.RememberMe);   // WebSessionManager
// Logout action
await sessionManager.SignOutAsync(HttpContext);
```

Loaders of caches shared by every viewer must run anonymously: `AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`.

## TqkLibrary.Asp.ToolKit.Auth.Api

JWT API side of the same scheme: compact access tokens (`sub`, `sid`, `jti`, lifetime), session check on every request
with a short cache, claims replaced by current data, non-rotating refresh tokens `<sessionId>.<secret>`.

Implement `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, string userId, CancellationToken ct)
    {
        // Return null when the session is revoked/expired, belongs to another user, or the account is unusable.
        ...
        return new SessionAccessRecord { UserId = userId, UserName = name, Roles = roles, Permissions = permissions };
    }
}
```

Registration:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.Issuer = "my-api";
    o.SigningKey = builder.Configuration["Tokens:Key"]!;    // >= 32 bytes
    o.MaxSessionLifetime = TimeSpan.FromDays(90);          // optional, default: no cap
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer();
```

Issuing and refreshing (inside the app's auth service):

```csharp
// Login
var refresh = RefreshTokenCodec.Create(sessionId);            // store refresh.SecretHash with the session
var expires = slidingPolicy.GetInitialExpiry(now);
var access = accessTokenFactory.Create(userId, sessionId, now);

// Refresh: same refresh token is returned, only the session expiry slides
if (!RefreshTokenCodec.TryParse(token, out var sid, out var secret) || !RefreshTokenCodec.Verify(secret, session.SecretHash)) return Unauthorized();
var next = slidingPolicy.GetNextExpiry(session.CreatedAt, now);   // null = absolute cap reached
```

After writes, invalidate `SessionAccessCache`: `InvalidateSession` (sign-out), `InvalidateUser` (password/role
change, lock), `InvalidateAll` (role permissions changed).

## TqkLibrary.Asp.ToolKit.Queue

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

## Releasing

Every release is a tag `vM.N.P` on a commit of `master`; the tag pushes all packages to nuget.org through CI.

```
git tag v1.2.3
git push origin v1.2.3      # triggers .github/workflows/release.yml
```

The tag must be higher than the highest version already on nuget.org. A commit after a tag builds as `M.N.P-dev.<commits since tag>`.

One-time setup on nuget.org (NuGet Trusted Publishing, no API key secret): in the account of `tqk2811`, add a trusted publishing policy with repository owner `tqk2811`, repository `TqkLibrary.Asp.ToolKit`, workflow file `release.yml`, and select the packages of this repository.
