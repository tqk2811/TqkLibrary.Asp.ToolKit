# TqkLibrary.Asp.ToolKit.Auth.Web

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Web/README-vi.md)

ASP.NET Core web (BFF) authentication against a separate API with opaque session tokens: the non-rotating API session token kept only in the encrypted auth cookie, a DelegatingHandler that attaches it as bearer, a middleware that builds the current user from the API, and session-expiry handling.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
```

## Breaking changes in 2.0.0

2.0.0 drops the access/refresh token pair. The web keeps the API session token `{sessionId}.{secret}` (issued once at
login, never rotated) and sends it as bearer; there is no refresh at all.

- `WebSessionManager.SignInAsync(context, AuthSession, persistent)`: `AuthSession` = `SessionToken`, `UserId`,
  `ExpiresAt` (cookie expiry), replacing `AuthTokenPair`.
- Removed: `AuthTokenPair`, `WebSessionTokens`, `IAuthApiClient.RefreshAsync`,
  `SessionBearerHandler.DefaultIsTransientRefreshFailure`, `SessionBearerHandler.ReplayBufferLimitOption`, and the
  options `AccessTokenName`, `AccessTokenExpiresName`, `RefreshTokenName`, `RefreshTokenExpiresName`, `RefreshSkew`,
  `RefreshFallbackMargin`, `RefreshTimeout`, `IsTransientRefreshFailure`.
- `WebSessionTicket` only exposes `GetSessionTokenAsync`. `WebSessionPrincipalFactory.CreateSessionOnly` takes the user id.
- `AuthSession.ExpiresAt` is a required `DateTimeOffset` (it was an optional `DateTime`); there is no fallback to the cookie `ExpireTimeSpan` any more.
- `SignInAsync` sets `HttpContext.User` to the new session principal, so API calls made later in the same request carry the bearer token.
- Cookies issued by 1.x carry no session token: those users are signed out on their first request and must sign in again.

## Usage

Web front end (MVC/Razor) that signs in against a separate API (the API side is `Auth.Api`). The encrypted, HttpOnly
auth cookie holds ONLY immutable data: the API session token, the session id and the user id. Name, email, roles and
permissions are never in the cookie: they come from the API `me` endpoint on each request (`CurrentUserMiddleware`,
cached per session for `CurrentUserCacheLifetime`, default 10 s), and the API itself loads them per request (its own
30 s cache). A user rename or role change therefore shows up after those cache lifetimes unless the app invalidates
`CurrentUserCache` (web) and `SessionAccessCache` (API).

The API is the authority on the session: it slides the expiry, revokes, and answers 401 for a dead session. The web
never refreshes anything; the cookie is written once at sign-in and dropped when the API says the session is dead.

Implement `IAuthApiClient` on top of your API client:

```csharp
public sealed class MyAuthApiClient(IMyApi api) : IAuthApiClient
{
    // me rejected by the API (401/403) MUST throw SessionExpiredException; any other exception is treated as
    // transient and the user is NOT signed out.
    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await api.GetMeAsync(ct);
        response.ThrowIfSessionRejected();     // 401/403 -> SessionExpiredException
        response.EnsureSuccessStatusCode();
        return await ReadUserAsync(response, ct);
    }

    public Task LogoutAsync(CancellationToken ct) => api.LogoutAsync(ct);
}
```

Registration:

```csharp
builder.Services.AddDataProtection()                                 // keep sessions across restarts
    .SetApplicationName("my-web").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.AddTqkWebAuth(
    cookie => { cookie.Cookie.Name = "app_auth"; cookie.LoginPath = "/account/login"; },
    options => options.AnonymousPathSuffixes.Add("/auth/login"));
builder.Services.AddHttpClient<IMyApi, MyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
builder.Services.AddScoped<IAuthApiClient, MyAuthApiClient>();

app.UseAuthentication();
app.UseTqkWebSession();   // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();

// Login action: values returned by the API login endpoint
await sessionManager.SignInAsync(HttpContext, new AuthSession
{
    SessionToken = login.SessionToken,     // {sessionId}.{secret}
    UserId = login.UserId,
    ExpiresAt = login.ExpiresAt,           // absolute cap of the session (DateTimeOffset, required)
}, persistent: model.RememberMe);
// Logout action
await sessionManager.SignOutAsync(HttpContext);
```

A cookie without a session token (issued by 1.x) is rejected and signed out when it is validated, on its first request,
whether or not that request calls the API. Your own `Events.OnValidatePrincipal` runs after this check, and only for cookies that have a token. The check is chained onto the
`Events` present while `AddTqkWebAuth` runs, so it is not applied when you use `options.EventsType` or replace `Events` (or
`OnValidatePrincipal`) after `AddTqkWebAuth`; such a cookie is then only rejected when the API is called.

A session that died on the API (revoked, expired, user locked) is only detected when a request calls the API: the `me`
call of `CurrentUserMiddleware` counts, as does any call through `SessionBearerHandler`. A page that calls no API (for
example one with `SkipCurrentUser`, or a cached `me`) keeps showing the user as signed in until the cookie expires or
the next API call answers 401.

Cookie lifetime: the ticket expires at `AuthSession.ExpiresAt` and is never reissued (`SlidingExpiration = false`).
Give the session's ABSOLUTE cap there (`SlidingSessionPolicy.GetAbsoluteExpiry` on the API), not its sliding expiry:
the API slides the session itself and rejects it with 401 when it is dead, so the cookie only has to outlive it. With
`persistent: false` the cookie is a browser-session cookie.

`SessionBearerHandler` adds `Authorization: Bearer {sessionId}.{secret}` to every call of the client it is attached
to and sends exactly once. A 401 from the API throws `SessionExpiredException` (`SessionExpiredMiddleware` drops the
cookie and redirects to login, AJAX gets a 401 JSON body); every other status and transport error is returned as is.
Uploads are never buffered (nothing is ever resent). Guests, calls inside `AnonymousApiScope`, calls that set
`Authorization` themselves and paths in `AnonymousPathSuffixes` pass through untouched.

To call the API anonymously through the same client, wrap the call in
`AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`. Typical case: the loader of a cache shared by every viewer,
otherwise the first viewer's token decides what everybody gets. Wrap it INSIDE the loader. The flag is per async flow
(other requests never see it). Endpoints that are always anonymous go in `AnonymousPathSuffixes` instead.

Options (`AddTqkWebAuth(cookie => ..., options => ...)`), all optional:

| Option | Default | Effect |
|---|---|---|
| `CurrentUserTimeout` | 15 s | Timeout of `me`; must cover a cold API start. |
| `CurrentUserCacheLifetime` | 10 s | Cache of `me` per session. |
| `CurrentUserFailureMode` | `SessionOnly` | `me` failed (network/5xx/timeout): continue with the session-only principal (session id + user id, no permission), or `Throw` `CurrentUserUnavailableException`. |
| `CurrentUserFailureCacheFor` | `null` | Remember a failed `me` per session for this long, so an API outage does not make every request wait `CurrentUserTimeout`. Only transient failures are remembered (network error without status, 5xx, 429, timeout); other exceptions follow `CurrentUserFailureMode` but are asked again on the next request. |
| `SkipCurrentUser` | `null` | `Func<HttpContext, bool>`: no `me` call for matching requests (a cached user still applies). Do NOT use it on endpoints that need roles or policies: the request principal is then session-only unless a user is already cached. |
| `IsAjaxRequest` | `X-Requested-With` / `Accept: application/json` | `Func<HttpRequest, bool>` used by `SessionExpiredMiddleware`. |
| `WriteSessionExpiredAjaxResponse` | 401 `{ success, message, redirect }` | `Func<HttpContext, SessionExpiredException, string redirect, Task>`. |
| `AnonymousPathSuffixes` | empty | API path suffixes that never get the token (login, password reset...). |
| `StableIdentityClaimType` | `"sub"` | Claim carrying the session id on every principal; see antiforgery below. |

`CurrentUserInfo.ExtraClaims` (`CurrentUserClaim(Type, Value)` list) are added to the request principal and cached
with the rest in memory, never written to the cookie. Claim types the library owns are ignored: the session id and
`StableIdentityClaimType`, `ClaimTypes.Role`, `PermissionClaimType`, `ClaimTypes.NameIdentifier`, `ClaimTypes.Name`
and `ClaimTypes.Email`.

When `SessionBearerHandler` throws `SessionExpiredException`, it also registers a `Response.OnStarting` callback that
drops the web session (deletes the cookie) unless that already happened, so the cookie is cleared even when an MVC
exception filter or a `try/catch` in the action handles the exception instead of `SessionExpiredMiddleware`. If the
response has already started nothing happens (headers can no longer be written; the next request clears the cookie). A
sign-in later in the same request cancels the pending drop, and a failure of the drop is logged, never thrown.

Antiforgery: ASP.NET Core binds its tokens to the `sub` claim (then `NameIdentifier`, then `upn`). Every web principal
(cookie, session-only when the API is unreachable, full after `me`) carries `sub` = API session id, so a form issued in
one state still posts in the other. Tokens are therefore bound to the session: after a new sign-in, forms need a new
token. The user id is in `ClaimTypes.NameIdentifier`.

### On the API side

The API that issues the session tokens (see `Auth.Api`) should run a background cleanup job that deletes expired and
revoked session rows in batches after a retention period; the web keeps nothing server side.

## Device Bound Session Credentials (DBSC)

Optional, off by default. With DBSC a supporting browser keeps a key pair in the device (TPM) and binds the web
session to it: besides the main cookie, the web issues a short-lived **proof cookie** that the browser renews by
signing a server challenge with the device key. A main cookie copied to another machine stops working once its proof
expires (10 minutes by default), because the thief cannot sign. Browsers without DBSC ignore the headers and keep
working with the main cookie only.

Browser support (11/2026): Chrome 145+ on Windows with a TPM, generally available from 146; macOS from 147 (rolling
out). Other browsers ignore it. The specification is a W3C draft (<https://w3c.github.io/webappsec-dbsc/>) and may
still change.

### Flow

1. `WebSessionManager.SignInAsync` (HTTPS only) adds
   `Secure-Session-Registration: (ES256 RS256);path="/dbsc/start";challenge="..."` to the login response.
2. The browser creates a key and posts a signed JWT (`Secure-Session-Response`) to `RegistrationPath`. The web checks
   the challenge (issued for this session, not expired, not used), the JWT (`typ` `dbsc+jwt`, `alg` ES256/RS256, the
   public key, the signature made by that key), calls `IDeviceKeyApiClient.RegisterDeviceKeyAsync`, reissues the main
   cookie once with the public key in the ticket (same expiry and persistence), sets the proof cookie and returns the
   session config (`session_identifier`, `refresh_url`, `scope`, `credentials`).
3. When the proof cookie expires the browser posts to `RefreshPath`: the web answers 403 with
   `Secure-Session-Challenge`, the browser signs it, the web verifies it with the key from the ticket and issues a new
   proof cookie. A signed-out or unbound session gets `{"continue": false}`, which ends the browser's DBSC session.
4. On every other request `CurrentUserMiddleware` checks the proof cookie of a bound session and applies the failure
   mode below.

Challenges and proofs are stateless (Data Protection, purpose-separated; expiry inside the payload), so several
instances only need the shared key ring, as for the main cookie. Replay protection of a used challenge is kept in
memory, per instance (best effort).

```csharp
builder.Services.AddTqkWebAuth(cookie => { /* ... */ }, o =>
{
    o.DeviceBound.Enabled = true;                              // set it HERE (checked at startup)
    o.DeviceBound.FailureMode = DeviceProofFailureMode.ReportOnly; // start with ReportOnly
    o.DeviceBound.ExcludedPaths = ["/health", "/lib"];
});
builder.Services.AddHttpClient<IDeviceKeyApiClient, MyDeviceKeyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
```

`Enabled = true` without an `IDeviceKeyApiClient` registration, or with invalid options, fails at startup. Set
`Enabled` in the `AddTqkWebAuth` delegate (like `Scheme`): the startup check and the endpoint middleware are wired from
it.

### Options (`WebAuthOptions.DeviceBound`)

| Option | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Turns DBSC on. Off = exactly the behaviour without it. |
| `RegistrationPath` / `RefreshPath` | `/dbsc/start` / `/dbsc/refresh` | Endpoints answered by `DeviceBoundSessionMiddleware` (POST only). |
| `ProofCookieName` | `null` | Null = `{main cookie name}.proof`. Attributes `Path=/; Secure; HttpOnly; SameSite=Lax`, host-only. |
| `ProofLifetime` | 10 min | Lifetime of the proof cookie (at least 1 minute). |
| `RegistrationChallengeLifetime` / `RefreshChallengeLifetime` | 5 min / 2 min | Validity of the challenges. |
| `Algorithms` | `ES256`, `RS256` | Accepted signature algorithms (`none`, HMAC and others are always refused). |
| `MinRsaKeySize` | 2048 | Smallest RSA modulus; exponent must be 65537. |
| `ExcludedPaths` | empty | Path prefixes not checked; also sent to the browser as `exclude` scope rules. |
| `FailureMode` | `Reject` | Fixed mode, or the default when users may choose. |
| `AllowUserChoice` | `false` | `false` = fixed policy, `true` = each user chooses (see below). |
| `AllowedUserModes` | all three | Modes a user may pick. |
| `ProofExpiryGrace` | 2 min | A genuine proof of the right session that expired less than this ago still counts. |
| `OnProofFailure` | `null` | `Func<DeviceProofFailureContext, ValueTask<DeviceProofFailureMode>>`, last word on the decision. |
| `ClearSiteDataOnSignOut` | `false` | Sends `Clear-Site-Data: "cookies"` on sign-out/drop (deletes EVERY cookie of the site). |

### Failure modes

When a bound session's request has no proof, an expired one (beyond the grace), one of another session, or an
invalid one:

- `ReportOnly`: the request runs as usual; a Warning is logged and the counter is incremented.
- `Reject` (default): the request runs as a guest. The session is NOT signed out and the main cookie is kept, so the
  next request works again once the browser refreshed the proof.
- `SignOut`: the session is revoked on the API (`IAuthApiClient.LogoutAsync`, best effort), the main and proof cookies
  are deleted, the request runs as a guest.

Recommended rollout: deploy with `ReportOnly`, watch the counter `tqk.auth.dbsc.proof_failures` (meter
`TqkLibrary.Asp.ToolKit.Auth.Web`, tags `reason` = `missing`/`expired`/`wrong_session`/`invalid`/`unbound`,
`mode`), then switch to `Reject`. `SignOut` is the strictest and makes every DBSC hiccup (TPM error, browser bug) a
forced login.

### Fixed policy or user choice

- **Fixed** (`AllowUserChoice = false`, default): `FailureMode` applies to everybody; `me.deviceBindingMode` is ignored.
  The decision is taken before `me`, so a rejected request does not call the API.
- **User choice** (`AllowUserChoice = true`): each user picks a mode in their account settings (stored by the app,
  returned by `me` as `deviceBindingMode`). An allowed value wins; no value, an unknown value or one outside
  `AllowedUserModes` falls back to `FailureMode` (a bad value is logged). The decision waits for the user (cached or
  loaded through `me`); when `me` is unavailable or skipped (`SkipCurrentUser`) the default applies.

Order of the decision: project default → user choice (when allowed) → `OnProofFailure` hook. The hook receives the
`HttpContext`, the session id, the reason (`Missing`, `Expired`, `WrongSession`, `Invalid`), the mode decided so far
and the user when known; it returns the mode to apply (an exception keeps the decided mode).

### Contract with the app's API

- `IDeviceKeyApiClient.RegisterDeviceKeyAsync(jwk, algorithm, ct)` stores the public key (canonical public JWK, ES256
  or RS256) on the API session, through the client with `AddTqkSessionBearer`. Return `false` when the API refuses (for
  example 409: already bound, or outside the API's registration window right after login): the browser gets 403 and the
  session stays unbound. The web verifies the proof of possession BEFORE this call; the API does not have to.
- `me` (`CurrentUserInfo`) may return `isDeviceBound` (the session has a key) and `deviceBindingMode` (the user's
  choice, `ReportOnly`/`Reject`/`SignOut`, case-insensitive; the web whitelists it).
- The web treats a successful registration as bound right away (the key is in the cookie ticket); it does not wait for
  `me` to report `isDeviceBound`.

### Risks and limits

- Downgrade: a copy of the main cookie taken BEFORE the binding carries no key. When `me` reports `isDeviceBound`
  for such a ticket the request runs as a guest (whatever the mode) and the user is not cached. Until the API reports
  it (cache TTL) such a copy still works.
- A stolen proof cookie works until it expires (`ProofLifetime` + grace): keep the lifetime short.
- A request with a rejected proof still authenticates through an explicit `HttpContext.AuthenticateAsync(scheme)` or an
  `[Authorize]` policy naming the scheme; the library itself never sends the session token for it.
- Replay protection is per instance; behind a load balancer a proof can be replayed to another instance within the
  challenge lifetime (it stays bound to the same session and key).
- The draft may change header or field names; browsers that end their DBSC session (any 4xx except 403 on refresh)
  leave a bound main cookie without proof, which the failure mode then handles.

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. It has no NuGet dependency on other packages of this toolkit; the API it signs in against is expected to use the scheme implemented by [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) (opaque, non-rotating session tokens).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
