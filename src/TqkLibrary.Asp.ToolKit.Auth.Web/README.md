# TqkLibrary.Asp.ToolKit.Auth.Web

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Web/README-vi.md)

ASP.NET Core web (BFF) authentication against a separate JWT API: API access/refresh tokens kept only in the encrypted auth cookie, a DelegatingHandler that attaches the bearer and refreshes it, a middleware that builds the current user from the API, and session-expiry handling.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
```

## Usage

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

// Login action: tokens returned by the API login endpoint
await sessionManager.SignInAsync(HttpContext, tokens, persistent: model.RememberMe);   // WebSessionManager
// Logout action
await sessionManager.SignOutAsync(HttpContext);
```

To call the API anonymously through the same client (no viewer bearer, no refresh), wrap the call in
`AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`. Typical case: the loader of a cache shared by every viewer,
otherwise the first viewer's bearer decides what everybody gets. Wrap it INSIDE the loader. The flag is per async flow
(other requests never see it). Endpoints that are always anonymous go in `AnonymousPathSuffixes` instead. The library
also uses the scope for `RefreshAsync`, so the handler does not re-enter itself during a refresh.

Options (`AddTqkWebAuth(cookie => ..., options => ...)`), all optional:

| Option | Default | Effect |
|---|---|---|
| `CurrentUserFailureMode` | `SessionOnly` | `me` failed (network/5xx/timeout): continue with the session-only principal (no permission), or `Throw` `CurrentUserUnavailableException`. |
| `CurrentUserFailureCacheFor` | `null` | Remember a failed `me` per session for this long, so an API outage does not make every request wait `CurrentUserTimeout`. Only transient failures are remembered (network error without status, 5xx, 429, timeout); other exceptions follow `CurrentUserFailureMode` but are asked again on the next request. |
| `SkipCurrentUser` | `null` | `Func<HttpContext, bool>`: no `me` call for matching requests (a cached user still applies). Do NOT use it on endpoints that need roles or policies: the request principal is then session-only (no roles, no permissions) unless a user is already cached. |
| `IsAjaxRequest` | `X-Requested-With` / `Accept: application/json` | `Func<HttpRequest, bool>` used by `SessionExpiredMiddleware`. |
| `WriteSessionExpiredAjaxResponse` | 401 `{ success, message, redirect }` | `Func<HttpContext, SessionExpiredException, string redirect, Task>`. |
| `StableIdentityClaimType` | `"sub"` | Claim carrying the session id on every principal; see antiforgery below. |

`CurrentUserInfo.ExtraClaims` (`CurrentUserClaim(Type, Value)` list) are added to the request principal and cached
with the rest in memory, never written to the cookie. Claim types the library owns are ignored: the session id and
`StableIdentityClaimType`, `ClaimTypes.Role`, `PermissionClaimType`, `ClaimTypes.NameIdentifier`, `ClaimTypes.Name`
and `ClaimTypes.Email`.

Uploads (`StreamContent`/`MultipartContent`) are not buffered by default, so they are not resent after a 401 + refresh
(a 401 on an upload means the session expired). Opt in per request with
`request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, bytes)`: a body of known length up to that many bytes
(at most `int.MaxValue`) is buffered and resent. The limit is enforced while reading, so a body longer than its declared
`Content-Length` fails the send with `HttpRequestException` before anything is sent.

When `SessionBearerHandler` throws `SessionExpiredException`, it also registers a `Response.OnStarting` callback that
drops the web session (deletes the cookie) unless that already happened, so the cookie is cleared even when an MVC
exception filter or a `try/catch` in the action handles the exception instead of `SessionExpiredMiddleware`. If the
response has already started nothing happens (headers can no longer be written; the next request clears the cookie). A
sign-in later in the same request cancels the pending drop, and a failure of the drop is logged, never thrown.

Antiforgery: ASP.NET Core binds its tokens to the `sub` claim (then `NameIdentifier`, then `upn`). Every web principal
(cookie, session-only when the API is unreachable, full after `me`) carries `sub` = API session id, so a form issued in
one state still posts in the other. Tokens are therefore bound to the session: after a new sign-in, forms need a new
token. The user id stays in `ClaimTypes.NameIdentifier`.

Consequences of `sub` = session id: forms opened before a deploy of this version, and forms opened before a re-login,
fail the antiforgery check once (the page must be reloaded). Code that reads the `sub` claim as the user id must read
`ClaimTypes.NameIdentifier` instead.

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. It has no NuGet dependency on other packages of this toolkit; the API it signs in against is expected to use the scheme implemented by [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) (non-rotating refresh tokens).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
