# TqkLibrary.Asp.ToolKit.Auth.Api

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Api/README-vi.md)

ASP.NET Core JWT API helpers for session-bound tokens: compact access tokens (sub/sid/jti/exp), per-request session validation with a short in-memory cache, non-rotating refresh token codec and sliding session expiry.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Api
```

## Usage

JWT API side of the same scheme: compact access tokens (`sub`, `sid`, `jti`, lifetime), session check on every request
with a short cache, claims replaced by current data, non-rotating refresh tokens `<sessionId>.<secret>`.

Implement `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessResult> GetAsync(string sessionId, string userId, CancellationToken ct)
    {
        // Reject (optionally with a reason) when the session is revoked/expired, belongs to another user, or the
        // account is unusable. Rejections are never cached.
        if (session is null || session.RevokedAt is not null) return SessionAccessResult.Reject(SessionRejectionReasons.Revoked);
        if (user.IsLocked) return SessionAccessResult.Reject(SessionRejectionReasons.UserLocked);
        ...
        // A SessionAccessRecord converts implicitly to SessionAccessResult.Grant(...).
        return new SessionAccessRecord { UserId = userId, UserName = name, Roles = roles, Permissions = permissions };
    }
}
```

If `ISessionAccessSource.GetAsync` throws, no rejection is recorded (`SessionTokenValidator.GetSessionRejection` returns
null) and the request fails with that exception instead of a 401.

Registration:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.Issuer = "my-api";
    o.SigningKey = builder.Configuration["Tokens:Key"]!;    // >= 32 bytes
    // Normal session 7 days, "remember me" session 30 days (Persistent null = same as Normal). Default: 30 days.
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);          // optional absolute cap for both kinds, default: no cap
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer(jwt =>
{
    // Optional: own 401 body with the rejection reason returned by ISessionAccessSource.
    jwt.Events.OnChallenge = async context =>
    {
        if (SessionTokenValidator.GetSessionRejection(context.HttpContext) is not { } rejection) return;
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "session_rejected", reason = rejection.Reason });
    };
});
```

`SessionTokenValidator.GetSessionRejection(HttpContext)` returns a `SessionRejection(Reason, Message)` (stored as an
`HttpContext.Features` entry) or null; `IsSessionRejected` stays as a shortcut. The library sets the reasons
`no_session` (token without session/user id) and `user_mismatch` (record of another user) itself.

Issuing and refreshing (inside the app's auth service):

```csharp
// Login
var refresh = RefreshTokenCodec.Create(sessionId);            // store refresh.SecretHash with the session
var expires = slidingPolicy.GetInitialExpiry(now, persistent: model.RememberMe);   // store "persistent" with the session
var access = accessTokenFactory.Create(userId, sessionId, now);

// Refresh: same refresh token is returned, only the session expiry slides
if (!RefreshTokenCodec.TryParse(token, out var sid, out var secret) || !RefreshTokenCodec.Verify(secret, session.SecretHash)) return Unauthorized();
var next = slidingPolicy.GetNextExpiry(session.CreatedAt, now, session.IsPersistent);   // null = absolute cap reached
```

`SlidingSessionPolicy` is not sealed and both methods are `virtual`. `AddTqkApiAuth` registers it with
`TryAddSingleton`, so a subclass registered BEFORE it wins:

```csharp
builder.Services.AddSingleton<SlidingSessionPolicy, MySlidingSessionPolicy>();
builder.Services.AddTqkApiAuth(o => { ... });
```

After writes, invalidate `SessionAccessCache`: `InvalidateSession` (sign-out; only that session), `InvalidateUser`
(password/role change, lock; only that user's sessions), `InvalidateAll` (role permissions changed). A lookup in flight
during an invalidation is never served afterwards.

## Dependencies

Targets net10.0, uses the ASP.NET Core shared framework and depends on `Microsoft.AspNetCore.Authentication.JwtBearer`. The matching web front end side is [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
