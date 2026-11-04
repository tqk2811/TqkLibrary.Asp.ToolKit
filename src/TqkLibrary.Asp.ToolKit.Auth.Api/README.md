# TqkLibrary.Asp.ToolKit.Auth.Api

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Api/README-vi.md)

ASP.NET Core API authentication with opaque, non-rotating session tokens (`{sessionId}.{secret}`, only the SHA-256 of the secret is stored): an authentication handler that checks the session on every request through a short in-memory cache, and a throttled sliding expiry with an absolute cap.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Api
```

## Breaking changes in 2.0.0

2.0.0 drops JWT. There is no access token and no refresh token any more: the session token `{sessionId}.{secret}` is
issued once at login and sent as `Authorization: Bearer {sessionId}.{secret}` on every call.

- Removed: `AccessTokenFactory`, `IssuedAccessToken`, `SessionTokenValidator`, `AddTqkJwtBearer`,
  `UseTqkSessionValidation`, the options `Issuer`, `Audience`, `SigningKey`, `AccessTokenLifetime`, `ClockSkew`,
  `EffectiveAudience`, `CreateSigningKey`, `CreateTokenValidationParameters`, and the
  `Microsoft.AspNetCore.Authentication.JwtBearer` dependency.
- Renamed: `RefreshTokenCodec` → `SessionTokenCodec`, `IssuedRefreshToken` → `IssuedSessionToken`.
- `ISessionAccessSource.GetAsync(sessionId, ct)` (no user id) returns the session row: user id, secret hash,
  `CreatedAt`, `LastAccessAt`, `ExpiresAt`, `IsPersistent`, `IsRevoked`, plus name/email/roles/permissions. The library
  checks the secret, revocation and expiry itself. New `TouchAsync` persists the sliding expiry.
- Authentication scheme: `AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(...)`; the
  custom 401 body moves to `SessionTokenAuthenticationOptions.OnChallenge`, and `GetSessionRejection` /
  `IsSessionRejected` are `HttpContext` extensions.
- `SessionAccessCache.GetAsync` has no user id parameter. `SessionRejectionReasons.NoSession` / `UserMismatch` are
  replaced by `InvalidToken`.
- Remove the refresh endpoint of the API; the login endpoint returns the session token (see below).

## Usage

The API owns the sessions. The app keeps a session table (in its own database; the library has no entity) with at
least: id, user id, secret hash, created at, last access at, expires at, persistent flag, revoked flag. Room for
later per-session data (for example a device-bound public key) is up to the app: the token format does not change.

Implement `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessResult> GetAsync(string sessionId, CancellationToken ct)
    {
        var row = await db.Sessions.Where(s => s.Id == sessionId)
            .Select(s => new { Session = s, s.User })
            .SingleOrDefaultAsync(ct);
        if (row is null) return SessionAccessResult.Reject();                                   // unknown session
        if (row.User.IsLocked) return SessionAccessResult.Reject(SessionRejectionReasons.UserLocked);
        // Return the row even when revoked/expired: the library checks secret, revocation and expiry.
        return new SessionAccessRecord
        {
            UserId = row.User.Id, SecretHash = row.Session.SecretHash,
            CreatedAt = row.Session.CreatedAt, LastAccessAt = row.Session.LastAccessAt, ExpiresAt = row.Session.ExpiresAt,
            IsPersistent = row.Session.IsPersistent, IsRevoked = row.Session.RevokedAt != null,
            UserName = row.User.UserName, Email = row.User.Email, Roles = roles, Permissions = permissions,
        };
    }

    // Called at most once per SessionTouchInterval per session and process; a failure is logged, never fails the request.
    public Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken ct)
        => db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAccessAt, lastAccessAt).SetProperty(s => s.ExpiresAt, expiresAt), ct);
}
```

If `GetAsync` throws, no rejection is recorded and the request fails with that exception instead of a 401.

Registration:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    // Normal session 7 days, "remember me" session 30 days (Persistent null = same as Normal). Default: 30 days.
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);        // optional absolute cap for both kinds, default: no cap
    o.SessionTouchInterval = TimeSpan.FromHours(1);      // default; how often the sliding expiry is written
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(o =>
{
    // Optional: own 401 body with the rejection reason.
    o.OnChallenge = async context =>
    {
        if (context.Rejection is not { } rejection) return;     // no token at all: keep the default 401
        context.HandleResponse();
        context.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.HttpContext.Response.WriteAsJsonAsync(new { code = "session_rejected", reason = rejection.Reason });
    };
});
```

On every request with `Authorization: Bearer {sessionId}.{secret}` the handler:

1. parses the token (malformed → 401 `invalid_token`, the source is not called);
2. loads the session through `SessionAccessCache` (default 30 s) → `ISessionAccessSource.GetAsync`;
3. compares the secret hash in fixed time (`invalid_token`), then checks `IsRevoked` (`revoked`), `ExpiresAt` and the
   absolute cap `CreatedAt + MaxSessionLifetime` (`expired`);
4. when the last write is older than `SessionTouchInterval`, calls `TouchAsync(sessionId, now, newExpiry)` with
   `newExpiry = min(now + lifetime, CreatedAt + MaxSessionLifetime)` (one write per session per process at a time; a
   failure is logged and retried after at most a minute) and updates the cached record;
5. builds the principal: `ClaimTypes.NameIdentifier` and `UserIdClaimType` (`sub`) = user id, `SessionIdClaimType`
   (`sid`), `ClaimTypes.Name`, `ClaimTypes.Email`, `ClaimTypes.Role`, `PermissionClaimType` (`permission`).

A request without a bearer header gets no result (anonymous endpoints work, `[Authorize]` answers 401). The rejection
(`SessionRejection(Reason, Message)`) is available in `OnChallenge` and through `HttpContext.GetSessionRejection()`.
The token is never logged.

Login and logout (inside the app's auth service):

```csharp
// Login
var issued = SessionTokenCodec.Create(sessionId);       // store issued.SecretHash with the session, never the token
var expires = slidingPolicy.GetInitialExpiry(now, persistent: model.RememberMe);
// ... insert the session row (CreatedAt = now, ExpiresAt = expires, IsPersistent = RememberMe)
return new { sessionToken = issued.Token, userId, expiresAt = slidingPolicy.GetAbsoluteExpiry(now) };   // cookie expiry for the web

// Logout (endpoint protected by the scheme): mark the session revoked, then
sessionAccessCache.InvalidateSession(sessionId);
```

`SlidingSessionPolicy` is not sealed and its methods are `virtual`. `AddTqkApiAuth` registers it with
`TryAddSingleton`, so a subclass registered BEFORE it wins:

```csharp
builder.Services.AddSingleton<SlidingSessionPolicy, MySlidingSessionPolicy>();
builder.Services.AddTqkApiAuth(o => { ... });
```

### Cache and stale data

`SessionAccessCache` keeps each session record for `SessionAccessCacheLifetime` (default 30 s) per process. A change
of the user's name, email, roles or permissions, or a revocation, therefore takes effect after that delay UNLESS the
app invalidates right after the write succeeds:

- `InvalidateSession(sessionId)`: sign-out / one session revoked;
- `InvalidateUser(userId)`: password change, account locked/deleted, role or name/email change;
- `InvalidateAll()`: permissions of a role changed, role renamed/deleted.

A lookup in flight during an invalidation is never served afterwards. With several API instances an invalidation
only reaches the instance that handled the write; others lag up to the cache lifetime.

### Cleanup job

Revoked and expired sessions stay in the table until the app deletes them. Run a background job (for example once
a day) that deletes rows with `RevokedAt` or `ExpiresAt` older than a retention period (for example 30 days, to keep
an audit trail), in batches (`DELETE TOP (1000) ... WHERE ...` in a loop, or `ExecuteDeleteAsync` on a `Take(1000)`
query) so it does not lock the table.

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework; no other NuGet dependency. The matching web front end side is [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
