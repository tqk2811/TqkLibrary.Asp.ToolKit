# TqkLibrary.Asp.ToolKit

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

// Read: the loader must fetch inside the lambda and use the token it receives.
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
    public async Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        try { return Map(await api.RefreshAsync(refreshToken, ct)); }
        catch (MyApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException("Session ended.", ex);   // anything else = transient, no sign-out
        }
    }

    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct) => Map(await api.GetMeAsync(ct));

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
