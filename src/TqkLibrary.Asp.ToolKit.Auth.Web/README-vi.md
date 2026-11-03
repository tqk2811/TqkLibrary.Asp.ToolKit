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

Tùy chọn (`AddTqkWebAuth(cookie => ..., options => ...)`), đều không bắt buộc:

| Tùy chọn | Mặc định | Tác dụng |
|---|---|---|
| `CurrentUserFailureMode` | `SessionOnly` | `me` lỗi (mạng/5xx/timeout): chạy tiếp với principal chỉ có session (không permission), hoặc `Throw` `CurrentUserUnavailableException`. |
| `CurrentUserFailureCacheFor` | `null` | Nhớ lượt `me` lỗi theo từng session trong khoảng này, để lúc API sập không phải request nào cũng chờ hết `CurrentUserTimeout`. Chỉ nhớ lỗi tạm thời (lỗi mạng không có status, 5xx, 429, timeout); exception khác vẫn theo `CurrentUserFailureMode` nhưng request sau hỏi lại. |
| `SkipCurrentUser` | `null` | `Func<HttpContext, bool>`: request khớp thì không gọi `me` (user đã cache vẫn được dùng). KHÔNG dùng cho endpoint cần role hoặc policy: principal của request khi đó chỉ có session (không role, không permission) trừ khi user đã nằm trong cache. |
| `IsAjaxRequest` | `X-Requested-With` / `Accept: application/json` | `Func<HttpRequest, bool>` dùng trong `SessionExpiredMiddleware`. |
| `WriteSessionExpiredAjaxResponse` | 401 `{ success, message, redirect }` | `Func<HttpContext, SessionExpiredException, string redirect, Task>`. |
| `StableIdentityClaimType` | `"sub"` | Claim mang session id trên mọi principal; xem phần antiforgery bên dưới. |

`CurrentUserInfo.ExtraClaims` (danh sách `CurrentUserClaim(Type, Value)`) được thêm vào principal của request và cache
trong memory cùng phần còn lại, không bao giờ ghi vào cookie. Các loại claim do thư viện sở hữu bị bỏ qua: session id
và `StableIdentityClaimType`, `ClaimTypes.Role`, `PermissionClaimType`, `ClaimTypes.NameIdentifier`, `ClaimTypes.Name`
và `ClaimTypes.Email`.

Body upload (`StreamContent`/`MultipartContent`) mặc định không được buffer nên không gửi lại sau 401 + refresh (401 với
upload nghĩa là session hết hạn). Bật theo từng request bằng
`request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, bytes)`: body biết trước độ dài và không quá số byte
này (tối đa `int.MaxValue`) được buffer và gửi lại. Giới hạn được kiểm khi đọc, nên body dài hơn `Content-Length` khai
báo sẽ làm lượt gửi lỗi `HttpRequestException` trước khi gửi bất cứ gì.

Khi `SessionBearerHandler` ném `SessionExpiredException`, nó đăng ký thêm một callback `Response.OnStarting` để drop
web session (xoá cookie) nếu chưa ai làm, nên cookie vẫn bị xoá kể cả khi exception filter của MVC hay `try/catch`
trong action bắt exception thay cho `SessionExpiredMiddleware`. Response đã bắt đầu gửi thì không làm gì cả (không ghi
được header nữa; request sau sẽ xoá cookie). Đăng nhập lại trong cùng request huỷ việc drop đang chờ, và lỗi khi drop
chỉ được log, không ném ra.

Antiforgery: ASP.NET Core gắn token với claim `sub` (rồi tới `NameIdentifier`, `upn`). Mọi principal của web (cookie,
principal chỉ có session khi API không gọi được, principal đầy đủ sau `me`) đều mang `sub` = session id của API, nên form
cấp ở trạng thái này vẫn post được ở trạng thái kia. Vì vậy token gắn với session: đăng nhập lại thì form cần token mới.
User id vẫn nằm ở `ClaimTypes.NameIdentifier`.

Hệ quả của `sub` = session id: form mở trước khi deploy phiên bản này, và form mở trước lần đăng nhập lại, sẽ lỗi
antiforgery một lần (phải tải lại trang). Code đang đọc claim `sub` như user id phải đọc `ClaimTypes.NameIdentifier`.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (refresh token không xoay vòng).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
