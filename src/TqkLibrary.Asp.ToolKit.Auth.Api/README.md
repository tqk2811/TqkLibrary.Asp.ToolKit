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
- `ISessionAccessSource.GetAsync(sessionId, ct)` (no user id) returns a nullable `SessionAccessRecord` (null = no such
  session): user id, secret hash, `CreatedAt`, `LastAccessAt`, `ExpiresAt`, `IsPersistent`, `IsRevoked`,
  `RevokedReason`, `UserRejectionReason`, plus name/email/roles/permissions. All times are UTC. The library checks the
  secret first, then the user, revocation and expiry. New `TouchAsync` persists the sliding expiry.
- Authentication scheme: `AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(...)`; the
  custom 401 body moves to `SessionTokenAuthenticationOptions.OnChallenge`, and `GetSessionRejection` /
  `IsSessionRejected` are `HttpContext` extensions.
- `SessionAccessCache.GetAsync` has no user id parameter and loads a `SessionAccessRecord?`. `SessionRejectionReasons.NoSession` /
  `UserMismatch` are replaced by `InvalidToken`; there is no grant/reject result type any more.
- A token must now have a session id of up to 128 characters of `A-Z a-z 0-9 - _` and a 43-character base64url secret
  (what `SessionTokenCodec.Create` issues); `Create` throws for other session ids.
- Remove the refresh endpoint of the API; the login endpoint returns the session token (see below).

## Usage

The API owns the sessions. The app keeps a session table (in its own database; the library has no entity) with at
least: id, user id, secret hash, created at, last access at, expires at, persistent flag, revoked flag. Room for
later per-session data (for example a device-bound public key) is up to the app: the token format does not change.

Implement `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, CancellationToken ct)
    {
        var row = await db.Sessions.Where(s => s.Id == sessionId)
            .Select(s => new { Session = s, s.User })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;                                                           // unknown session
        // Return the row even when revoked/expired/locked: the library checks the secret first, then the rest.
        return new SessionAccessRecord
        {
            UserId = row.User.Id, SecretHash = row.Session.SecretHash,
            CreatedAt = row.Session.CreatedAt, LastAccessAt = row.Session.LastAccessAt, ExpiresAt = row.Session.ExpiresAt,
            IsPersistent = row.Session.IsPersistent, IsRevoked = row.Session.RevokedAt != null,
            RevokedReason = row.Session.RevokedReason,                                     // optional app code, e.g. "password_changed"
            UserRejectionReason = row.User.IsLocked ? SessionRejectionReasons.UserLocked : null,
            UserName = row.User.UserName, Email = row.User.Email, Roles = roles, Permissions = permissions,
        };
    }

    // Called at most once per SessionTouchInterval per session and process; a failure is logged, never fails the request.
    public Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken ct)
        => db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAccessAt, lastAccessAt).SetProperty(s => s.ExpiresAt, expiresAt), ct);
}
```

All `DateTime` values of the record are UTC (`Unspecified` is taken as UTC, `Local` is converted). `UserRejectionReason`
(for example `user_locked`) and `RevokedReason` are only reported to a caller whose secret is correct; with a wrong secret,
or an unknown session id, the answer is always `invalid_token`, so the endpoint cannot be used to probe sessions or users.
Only a found record is cached; the "no such session" answer (null) is never cached, so a session created a moment ago is
visible on the next request on every instance. Session ids MUST be unguessable (a random GUID or random bytes), never
sequential ids.

If `GetAsync` throws, no rejection is recorded and the request fails with that exception instead of a 401.

Registration:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    // Normal session 7 days, "remember me" session 30 days (Persistent null = same as Normal). Default: 30 days.
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);        // optional absolute cap for both kinds, default: no cap
    o.SessionTouchInterval = TimeSpan.FromHours(1);      // default; how often the sliding expiry is written
    o.SessionTouchTimeout = TimeSpan.FromSeconds(5);     // default; budget of one TouchAsync (it ignores RequestAborted)
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(o =>
{
    // Optional: own 401 body with the rejection reason. Status 401 and WWW-Authenticate are already set;
    // keep the header (the Web package relies on it).
    o.OnChallenge = async context =>
    {
        if (context.Rejection is not { } rejection) return;     // no token at all: keep the default bare 401
        context.HandleResponse();
        await context.HttpContext.Response.WriteAsJsonAsync(new { code = "session_rejected", reason = rejection.Reason });
    };
});
```

On every request with `Authorization: Bearer {sessionId}.{secret}` the handler:

1. parses the token (malformed → 401 `invalid_token`, the source is not called);
2. loads the session through `SessionAccessCache` (default 30 s) → `ISessionAccessSource.GetAsync`; no such session →
   `invalid_token` (after a dummy hash check, so it takes as long as a wrong secret);
3. compares the secret hash in fixed time (`invalid_token`), then checks `UserRejectionReason` (its value), `IsRevoked`
   (`RevokedReason`, or `revoked` when null), `ExpiresAt` and the absolute cap `CreatedAt + MaxSessionLifetime` (`expired`);
4. when the last write is older than `SessionTouchInterval`, calls `TouchAsync(sessionId, now, newExpiry)` with
   `newExpiry = min(now + lifetime, CreatedAt + MaxSessionLifetime)` (one write per session per process at a time; a
   failure or a `SessionTouchTimeout` timeout is logged as a warning and retried after at most a minute; the write does
   not use the request's cancellation token) and updates the cached record;
5. builds the principal: `ClaimTypes.NameIdentifier` and `UserIdClaimType` (`sub`) = user id, `SessionIdClaimType`
   (`sid`), `ClaimTypes.Name`, `ClaimTypes.Email`, `ClaimTypes.Role`, `PermissionClaimType` (`permission`), and for device binding `device_bound` /
   `device_binding_mode` (see [Device binding (DBSC)](#device-binding-dbsc)).

A request without a bearer header gets no result (anonymous endpoints work, `[Authorize]` answers 401). The rejection
(`SessionRejection(Reason, Message)`) is available in `OnChallenge` and through `HttpContext.GetSessionRejection()`.
The token is never logged.

A challenge always answers 401 with `WWW-Authenticate: Bearer error="invalid_token"` (plain `Bearer` when the request
carried no token, per RFC 6750). Both are set BEFORE `OnChallenge` runs, so the hook may write its own body but MUST
NOT remove the `WWW-Authenticate` header: `Auth.Web` ends the user's session only on a 401 that carries this Bearer
challenge, which is how it tells a dead session from a business 401 of your endpoint (e.g. a wrong 2FA code).

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
- `InvalidateUser(userId)`: password change, account locked/deleted, role or name/email change, and also after
  UNLOCKING or enabling a user (the locked state is cached);
- `InvalidateAll()`: permissions of a role changed, role renamed/deleted.

A lookup in flight during an invalidation is never served afterwards. With several API instances an invalidation
only reaches the instance that handled the write; others lag up to the cache lifetime.

### Device binding (DBSC)

Device Bound Session Credentials bind a session to a key pair kept on the user's device: the web front end
([TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web)) checks the device
proofs; the API only stores the public key once per session and tells the front end, through claims, whether the session
is bound. Apps without DBSC need nothing of this section.

Add two nullable columns to the session table: `DevicePublicKey` (the canonical public JWK, text up to 4 KB) and
`DeviceKeyAlgorithm` (`ES256` or `RS256`). Optionally store a per-user preference `DeviceBindingMode` (for example on
the user table, set by the END USER on their account settings page): `ReportOnly`, `Reject` or `SignOut`, null = the
project default. The web front end honours that choice only when its developer enabled the "soft" policy (user choice
allowed); with the "hard" policy the mode fixed by the developer applies and the user's value is ignored. The API never
interprets it.

Fill the record in `GetAsync`:

```csharp
IsDeviceBound = row.Session.DevicePublicKey != null,
DeviceBindingMode = row.User.DeviceBindingMode,         // null = project default
```

The principal then carries `DeviceBoundClaimType` (`device_bound`) = `true` when bound, and `DeviceBindingModeClaimType`
(`device_binding_mode`) when the mode is set; both are absent otherwise. Both claim types are options of `AddTqkApiAuth`.

Implement `IDeviceKeyStore` with ONE atomic statement that writes the key only on a live session (not revoked, not
expired) that has none yet and was created within the registration window, and returns true iff exactly one row changed
(a key is registered once per session and never replaced, so a stolen session token cannot rebind the session to
another device). `false` covers: already bound, revoked, expired, deleted, or registration window closed:

```csharp
public sealed class DeviceKeyStore(AppDbContext db) : IDeviceKeyStore
{
    // UPDATE Sessions SET DevicePublicKey=@key, DeviceKeyAlgorithm=@alg WHERE Id=@id AND RevokedAt IS NULL
    //   AND DevicePublicKey IS NULL AND ExpiresAt > @now AND (@createdNotBefore IS NULL OR CreatedAt >= @createdNotBefore)
    public async Task<bool> TrySetDeviceKeyAsync(string sessionId, string publicKeyJwk, string algorithm,
        DateTimeOffset now, DateTimeOffset? createdNotBefore, CancellationToken ct)
        => await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null && s.DevicePublicKey == null
                && s.ExpiresAt > now.UtcDateTime && (createdNotBefore == null || s.CreatedAt >= createdNotBefore.Value.UtcDateTime))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DevicePublicKey, publicKeyJwk)
                                      .SetProperty(s => s.DeviceKeyAlgorithm, algorithm), ct) == 1;
}

builder.Services.AddScoped<IDeviceKeyStore, DeviceKeyStore>();
```

`DeviceKeyRegistrationWindow` (option of `AddTqkApiAuth`, default 10 minutes, null = any time) limits registration to
that time after the session was created, so a stolen bearer token cannot bind a not-yet-bound session to the attacker's
device (which would lock the real user out in the `Reject` / `SignOut` modes). A later attempt answers `AlreadyBound`.
Under SQL Server SNAPSHOT isolation a concurrent UPDATE throws an update-conflict error instead of returning 0 rows;
READ COMMITTED / RCSI, PostgreSQL, MySQL and SQLite return 0 rows.

`AddTqkApiAuth` registers `DeviceKeyRegistrar` (scoped, resolve it only inside a request scope); it resolves
`IDeviceKeyStore` only when a key is registered. `RegisterAsync(user, publicKeyJwk, algorithm)` returns:

| Result | When | Suggested HTTP |
| --- | --- | --- |
| `Registered` | key stored; the session's cache entry is invalidated, so the next request has `device_bound` | 200 |
| `AlreadyBound` | the session already has a key, or it is revoked / expired / past the registration window (claim or store returned false) | 409 |
| `Invalid` | algorithm not `ES256`/`RS256`, or the JWK is not an acceptable public key | 400 |
| `NoSession` | the caller is not authenticated by the session token scheme with a `sid` claim | 401 |

Accepted keys: EC `P-256` (`x`/`y` 32 bytes each, the point must be on the curve) for `ES256`; RSA with a 2048 to 8192
bit modulus and `e` = `AQAB` for `RS256`. Any private member (`d`, `p`, `q`, `dp`, `dq`, `qi`, `oth`, `k`), a duplicate
member, a JWK `alg` other than the given algorithm, a `use` other than `sig`, non-canonical base64url or a JWK longer than
4096 characters is rejected. The stored text keeps only the public members in a fixed order (`crv`, `kty`, `x`, `y` /
`e`, `kty`, `n`).

The registrar does NOT check proof of possession: the Web must verify the DBSC registration JWT signed by the new key
BEFORE calling `RegisterAsync`. It also detects the session token scheme through `ClaimsIdentity.AuthenticationType`, so
the app must never build identities with that scheme name by hand.

App endpoint (the web front end calls it once after sign-in with the key it generated). The endpoint must require the
session token scheme explicitly, so an anonymous or other-scheme caller gets 401:

```csharp
using TqkLibrary.Asp.ToolKit.Auth.Api.Enums;

app.MapPost("/auth/device-key", async (DeviceKeyRequest body, ClaimsPrincipal user, DeviceKeyRegistrar registrar, CancellationToken ct) =>
    await registrar.RegisterAsync(user, body.PublicKeyJwk, body.Algorithm, ct) switch
    {
        DeviceKeyRegistrationResult.Registered => Results.Ok(),
        DeviceKeyRegistrationResult.AlreadyBound => Results.Conflict(),
        DeviceKeyRegistrationResult.Invalid => Results.BadRequest(),
        _ => Results.Unauthorized(),
    }).RequireAuthorization(p => p.AddAuthenticationSchemes(SessionTokenDefaults.AuthenticationScheme).RequireAuthenticatedUser());

public sealed record DeviceKeyRequest(string PublicKeyJwk, string Algorithm);
```

Notes:

- Multi-instance: after `Registered`, `InvalidateSession` only reaches the local instance. Other instances keep the
  cached record without the `device_bound` claim until `SessionAccessCacheLifetime` ends, so the Web should treat
  `Registered` as bound on its own side. If an app ever removes a device key it must call `InvalidateSession`, because the
  `AlreadyBound` shortcut trusts the claim.
- `DeviceBindingMode` is free text and is NOT validated by the API: the Web must whitelist the known modes and fall back
  to its default.

The app's `me` endpoint must expose `isDeviceBound` and `deviceBindingMode` (from the claims or the record) so the web
front end knows whether to register a key and which mode the user chose. The account settings page that changes
`DeviceBindingMode` must call `InvalidateUser(userId)` afterwards.

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
