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
    // me rejected by the API (ANY 401) MUST throw SessionExpiredException; any other exception is treated as
    // transient and the user is NOT signed out.
    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await api.GetMeAsync(ct);
        response.ThrowIfSessionRejected();     // any 401 from me -> SessionExpiredException
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
to and sends exactly once. A 401 from the API that carries `WWW-Authenticate: Bearer` (what `Auth.Api` sends for a dead
session) throws `SessionExpiredException` (`SessionExpiredMiddleware` drops the cookie and redirects to login, AJAX gets
a 401 JSON body). A 401 WITHOUT that header is a business 401 of the endpoint (for example a wrong 2FA code on an
`[Authorize]` page) and is returned as is, so the user stays signed in; so is every other status and transport error.
If your `Auth.Api` sets an `OnChallenge`, it must keep the `WWW-Authenticate` header. For an API not built on `Auth.Api`
that sends no challenge header, set `TreatAnyUnauthorizedAsSessionExpired = true`: any 401 then ends the session (this
option applies to `SessionBearerHandler`, i.e. calls other than `me`).
For `me`, `ThrowIfSessionRejected` treats ANY 401 as an ended session (with or without the header; `me` has no business
401, and a proxy may strip `WWW-Authenticate`, which would leave the user signed in without permissions forever). A 403
never counts.
On the API side: write business 401s with `Results.Unauthorized()` / `Unauthorized()` / `Problem(statusCode: 401)`, NEVER
with `Challenge()` / `Results.Challenge()` / `HttpContext.ChallengeAsync()` (a challenge adds `WWW-Authenticate: Bearer`,
which ends the web session); and make the session-token scheme the `DefaultChallengeScheme` (or the scheme of every
endpoint the Web calls), because another Bearer scheme (e.g. JwtBearer) that challenges the same request would end the
session too.
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
| `TreatAnyUnauthorizedAsSessionExpired` | `false` | Applies to `SessionBearerHandler` (calls other than `me`; `me` always treats any 401 as an ended session). `false`: only a 401 with `WWW-Authenticate: Bearer` ends the session. `true`: any 401 does (APIs not built on `Auth.Api` that send no challenge header). |
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

The **API is the single source of truth for the device key**: the web never stores the key in its cookie. It learns
whether a session is bound, and the key, from `me` (`deviceKeyJwk`, `deviceKeyAlgorithm`) and keeps that in a small
in-memory binding cache per instance (an entry lives until the session's absolute expiry, at most 30 days, bounded to
100 000 sessions, independent of the app's `IMemoryCache`). Once a session is known to be bound it stays bound. A
fresh `me` answer "not bound" is trusted only for `UnboundRecheckInterval` (30 s); after that, or when nothing is
known (cold instance), the binding is learned from `me` again, also on `SkipCurrentUser` paths and while a cached
user still says "not bound".

### Flow

1. `WebSessionManager.SignInAsync` (HTTPS only) adds
   `Secure-Session-Registration: (ES256 RS256);path="/dbsc/start";challenge="..."` to the login response.
2. The browser creates a key and posts a signed JWT (`Secure-Session-Response`) to `RegistrationPath`. The web checks
   the challenge (issued for this session, not expired, not used), the JWT (`typ` `dbsc+jwt`, `alg` in `Algorithms`,
   the public key, the signature made by that key), calls `IDeviceKeyApiClient.RegisterDeviceKeyAsync`, records the
   binding in memory, drops the cached `me` result, sets the proof cookie and returns the session config
   (`session_identifier`, `refresh_url`, `scope`, `credentials`). The main cookie is not touched.
3. When the proof cookie expires the browser posts to `RefreshPath` (HTTPS only): the web takes the stored key from its
   binding cache, or asks `me` on a miss, answers 403 with `Secure-Session-Challenge`, verifies the signed challenge
   with that key and issues a new proof cookie. A signed-out, unbound session, or a request over HTTP gets
   `{"continue": false}`, which ends the browser's DBSC session; `me` unavailable gets 503 (the browser skips the
   refresh).
4. On every other request `CurrentUserMiddleware` checks the proof cookie of a bound session and hands a failure to
   the failure handler below.

Challenges and proofs are stateless (Data Protection, purpose-separated; expiry inside the payload), so several
instances only need the shared key ring, as for the main cookie. The proof cookie carries the session id and the
RFC 7638 thumbprint of the device key; a proof made for another key is `Invalid`. Replay protection of a used
challenge is kept in memory, per instance (best effort), keyed on the decoded challenge bytes.

A **lost registration response** (the API stored the key but the answer never reached the browser) is not a lockout:
the next request without proof is a proof failure (`Missing`), and a refresh obtains the key through `me` and issues
the proof for the same, unchanged main cookie.

```csharp
builder.Services.AddTqkWebAuth(cookie => { /* ... */ }, o =>
{
    o.DeviceBound.Enabled = true;
    o.DeviceBound.FailureMode = DeviceProofFailureMode.ReportOnly; // start with ReportOnly
    o.DeviceBound.ExcludedPaths = ["/health", "/lib"];
});
builder.Services.AddHttpClient<IDeviceKeyApiClient, MyDeviceKeyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
```

`Enabled = true` without an `IDeviceKeyApiClient` registration, or with invalid options, fails at startup, whether it
is set in the `AddTqkWebAuth` delegate or in a later `Configure<WebAuthOptions>`.

### Options (`WebAuthOptions.DeviceBound`)

| Option | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Turns DBSC on. Off = exactly the behaviour without it. |
| `RegistrationPath` / `RefreshPath` | `/dbsc/start` / `/dbsc/refresh` | Endpoints answered by `DeviceBoundSessionMiddleware` (POST only). |
| `ProofCookieName` | `null` | Null = `{main cookie name}.proof`. Attributes `Path=/; Secure; HttpOnly; SameSite=Lax`, host-only. |
| `ProofLifetime` | 10 min | Lifetime of the proof cookie (at least 1 minute). |
| `RegistrationChallengeLifetime` / `RefreshChallengeLifetime` | 5 min / 2 min | Validity of the challenges. |
| `Algorithms` | `ES256`, `RS256` | Algorithms accepted for NEW registrations. A bound session keeps working with its stored key's algorithm after it is removed here. |
| `MinRsaKeySize` | 2048 | Smallest RSA modulus for new registrations; exponent must be 65537. Stored keys are read with the 2048 floor. |
| `ExcludedPaths` | empty | Path prefixes that BYPASS enforcement completely (a copied cookie without proof works there, with the session token); also sent to the browser as `exclude` scope rules. Exclude only paths that expose nothing sensitive. |
| `FailureMode` | `Reject` | Fixed mode, or the default when users may choose (used by the default failure handler). |
| `AllowUserChoice` | `false` | `false` = fixed policy, `true` = each user chooses (see below). |
| `AllowedUserModes` | all three | Modes a user may pick. |
| `ProofExpiryGrace` | 2 min | A genuine proof of the right session that expired less than this ago still counts. |
| `ClearSiteDataOnSignOut` | `false` | Sends `Clear-Site-Data: "cookies"` on sign-out/drop (deletes EVERY cookie of the site). |
| `UnboundRecheckInterval` | 30 s | How long a fresh `me` answer "not bound" is trusted for the binding decision (greater than 0, at most 10 min). Within it an unbound session needs no `me` call just for its binding; it is also the multi-instance window after a registration (see Risks). |

### Failure handling

When a bound session's request has no proof (`Missing`), an expired one beyond the grace (`Expired`), one of another
session (`WrongSession`), or an invalid one or one made for another key (`Invalid`), the library asks the
`DeviceProofFailureHandler` (singleton) for a `DeviceProofOutcome`:

- `Continue` (= `ReportOnly`): the request runs as usual; the failure is counted and logged at Debug level.
- `Reject` (default): the request runs as a guest WITHOUT the session token, for the rest of the request: an endpoint
  with explicit `AuthenticationSchemes` that re-authenticates the cookie, or an error page re-executed by
  `UseExceptionHandler`, still sends no token and a 401 there does not drop the cookie. The session is NOT signed out
  and the main cookie is kept, so the next request works again once the browser refreshed the proof.
- `SignOut`: the session is revoked on the API (`IAuthApiClient.LogoutAsync`, best effort), the main and proof cookies
  are deleted, the request runs as a guest.
- `Handled` (custom handlers only): the handler wrote the response itself; the pipeline stops right there, no token is
  sent, the user is not cached and both cookies stay as they are.

The default handler returns `context.DefaultMode` as an outcome: the fixed `FailureMode`, or the user's allowed choice
(see below). To customise, subclass it and register the subclass (before or after `AddTqkWebAuth`); scoped services
come from `context.HttpContext.RequestServices`:

```csharp
public sealed class MyProofFailureHandler : DeviceProofFailureHandler
{
    public override async ValueTask<DeviceProofOutcome> HandleAsync(DeviceProofFailureContext context)
    {
        if (context.Reason == DeviceProofFailureReason.Invalid)
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.HttpContext.Response.WriteAsync("Device proof required.");
            return DeviceProofOutcome.Handled;
        }
        return await base.HandleAsync(context);   // FailureMode / user choice
    }
}

builder.Services.AddSingleton<DeviceProofFailureHandler, MyProofFailureHandler>();
```

`DeviceProofFailureContext` carries `HttpContext`, `SessionId`, `Reason`, `DefaultMode` and `User` (when known). A
handler that throws (other than a cancellation of the aborted request) or returns an undefined value is logged and the
default outcome applies. `DeviceProofFailureMode` stays the type of the options and of the user's choice, so
`Handled` can never be configured or chosen by a user.

Recommended rollout: deploy with `ReportOnly`, watch the counter `tqk.auth.dbsc.proof_failures` (meter
`TqkLibrary.Asp.ToolKit.Auth.Web`, tags `reason` = `missing`/`expired`/`wrong_session`/`invalid`, `mode` =
`report_only`/`reject`/`sign_out`/`handled`), then switch to `Reject`. The per-request log line is Debug on purpose:
a browser that lost its DBSC session fails on every request until the next sign-in. `SignOut` is the strictest and
makes every DBSC hiccup (TPM error, browser bug) a forced login.

### Fixed policy or user choice

- **Fixed** (`AllowUserChoice = false`, default): `FailureMode` applies to everybody; `me.deviceBindingMode` is ignored.
  For a session already known to be bound, the decision is taken before `me`, so a rejected request does not call the
  API.
- **User choice** (`AllowUserChoice = true`): each user picks a mode in their account settings (stored by the app,
  returned by `me` as `deviceBindingMode`). An allowed value wins; no value, a blank or unknown value, or one outside
  `AllowedUserModes` falls back to `FailureMode`. The decision waits for the user (cached or loaded through `me`);
  when `me` is unavailable or skipped (`SkipCurrentUser`) the default applies. Exception to "no token after a failed
  proof": to learn the user's mode, this policy necessarily calls `me` WITH the session token first. The same holds
  for a session whose binding this instance does not know yet (it has to ask `me`).

When `me` fails (or `SkipCurrentUser` applies) and the session is known to be bound, the proof is still enforced
(user unknown), BEFORE `CurrentUserFailureMode.Throw` could fail the request: a request made a guest by the proof
failure does not throw. `SkipCurrentUser` never skips an UNKNOWN binding: such a request still calls `me` once to
learn it (the user it returns is cached and applied; a failure there is not thrown). When nothing is known about the
binding and `me` fails (a cold instance during an API outage), the session is treated as not bound for that request:
this is the residual gap of the design.

### Contract with the app's API

- `me` (`CurrentUserInfo`) must return `deviceKeyJwk` and `deviceKeyAlgorithm` (the stored key, null when not bound)
  and may return `deviceBindingMode` (the user's choice, `ReportOnly`/`Reject`/`SignOut`, case-insensitive; the web
  whitelists it). `IsDeviceBound` is derived from `deviceKeyJwk`. Auth.Api provides `GetDeviceBinding` for this.
- `IDeviceKeyApiClient.RegisterDeviceKeyAsync(jwk, algorithm, ct)` stores the public key (canonical public JWK, ES256
  or RS256) on the API session, through the client with `AddTqkSessionBearer`. Return `false` for HTTP 409 (already
  bound, revoked/expired, or outside the API's registration window right after login) and 400 — never throw for
  them: the browser gets 400. A transient failure may throw: the browser gets 503. The web verifies the proof of
  possession BEFORE this call; the API does not have to.

### Risks and limits

- Losing the browser's DBSC session (site data cleared, `continue: false`, any 4xx except 403 on refresh) leaves a
  bound session without proof: with `Reject`/`SignOut` the user has to sign in again, because registration only
  happens right after sign-in (the API accepts a key only within its registration window, 10 minutes by default).
- A stolen proof cookie works until it expires (`ProofLifetime` + grace), and only with the session's own main cookie:
  keep the lifetime short.
- `ExcludedPaths` bypass enforcement completely.
- Cold instance + API outage: an instance that never saw the session cannot tell it is bound while `me` fails, and
  treats it as not bound until `me` answers again.
- Several instances: right after a registration on one instance, another instance that saw the session unbound just
  before enforces nothing for that session for at most `UnboundRecheckInterval`.
- Replay protection is per instance; behind a load balancer a proof can be replayed to another instance within the
  challenge lifetime (it stays bound to the same session and key).
- The draft may change header or field names.

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. It has no NuGet dependency on other packages of this toolkit; the API it signs in against is expected to use the scheme implemented by [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) (opaque, non-rotating session tokens).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
