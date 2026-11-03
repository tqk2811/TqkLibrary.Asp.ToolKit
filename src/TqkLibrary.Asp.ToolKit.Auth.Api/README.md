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

## Dependencies

Targets net10.0, uses the ASP.NET Core shared framework and depends on `Microsoft.AspNetCore.Authentication.JwtBearer`. The matching web front end side is [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
