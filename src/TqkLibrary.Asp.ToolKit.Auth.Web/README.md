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

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. It has no NuGet dependency on other packages of this toolkit; the API it signs in against is expected to use the scheme implemented by [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) (opaque, non-rotating session tokens).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
