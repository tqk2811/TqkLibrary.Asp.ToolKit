# TqkLibrary.Asp.ToolKit.Auth.Web

**English** · [Tiếng Việt](#tiếng-việt)

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

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. It has no NuGet dependency on other packages of this toolkit; the API it signs in against is expected to use the scheme implemented by [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) (non-rotating refresh tokens).

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)

---

## Tiếng Việt

### Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
```

### Cách dùng

Web front end (MVC/Razor) đăng nhập qua một JWT API riêng. Access token và refresh token của API chỉ nằm trong
auth cookie đã mã hóa, HttpOnly; principal của cookie chỉ mang session id của API. Một `DelegatingHandler`
gắn bearer, refresh trước khi hết hạn hoặc một lần sau khi gặp 401, và ghi lại cookie. Thông tin user hiện tại
(tên, role, permission) được hỏi từ API ở mỗi request (có cache ngắn hạn).

API KHÔNG được xoay vòng refresh token kiểu dùng một lần (xem `Auth.Api`).

Cài đặt `IAuthApiClient` trên nền API client của bạn:

```csharp
public sealed class MyAuthApiClient(IMyApi api) : IAuthApiClient
{
    // refresh / me bị API từ chối (401/403) PHẢI ném SessionExpiredException; mọi exception khác đều
    // được coi là lỗi tạm thời và user KHÔNG bị đăng xuất.
    public async Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await api.RefreshAsync(refreshToken, ct);   // trả về HttpResponseMessage
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

Đăng ký:

```csharp
builder.Services.AddTqkDataProtectionKeyRing(builder.Environment);   // giữ session qua các lần restart
builder.Services.AddTqkWebAuth(
    cookie => { cookie.Cookie.Name = "app_auth"; cookie.LoginPath = "/account/login"; },
    options => options.AnonymousPathSuffixes.Add("/auth/login"));
builder.Services.AddHttpClient<IMyApi, MyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
builder.Services.AddScoped<IAuthApiClient, MyAuthApiClient>();

app.UseAuthentication();
app.UseTqkWebSession();   // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();

// Action đăng nhập: token do endpoint login của API trả về
await sessionManager.SignInAsync(HttpContext, tokens, persistent: model.RememberMe);   // WebSessionManager
// Action đăng xuất
await sessionManager.SignOutAsync(HttpContext);
```

Các loader của cache dùng chung cho mọi người xem phải chạy ở chế độ ẩn danh: `AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`.

### Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (refresh token không xoay vòng).

### Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
