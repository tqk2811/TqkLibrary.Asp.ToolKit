# TqkLibrary.Asp.ToolKit.Auth.Web

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Web/README.md) · **Tiếng Việt**

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
```

## Cách dùng

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
builder.Services.AddDataProtection()                                 // giữ session qua các lần restart
    .SetApplicationName("my-web").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
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

Muốn gọi API ẩn danh qua cùng client (không gắn bearer của người xem, không refresh) thì bọc lời gọi trong
`AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`. Trường hợp hay gặp: hàm tải của cache dùng chung cho mọi
người xem, nếu không thì token của người kích hoạt đầu tiên quyết định dữ liệu cho tất cả. Bọc BÊN TRONG hàm tải. Cờ
riêng cho từng luồng async (request khác không thấy). Endpoint luôn ẩn danh thì khai trong `AnonymousPathSuffixes`.
Thư viện cũng dùng scope này cho `RefreshAsync` để handler không tự gọi vòng vào chính nó khi refresh.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (refresh token không xoay vòng).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
