# TqkLibrary.Asp.ToolKit.Auth.Web

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Web/README.md) · **Tiếng Việt**

Xác thực Web (BFF) ASP.NET Core qua một API riêng dùng session token mờ (opaque): session token của API (không xoay vòng) chỉ nằm trong auth cookie đã mã hoá, một DelegatingHandler gắn nó làm bearer, một middleware dựng user hiện tại từ API, và xử lý phiên hết hạn.

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
```

## Thay đổi breaking ở 2.0.0

2.0.0 bỏ cặp access/refresh token. Web giữ session token của API `{sessionId}.{secret}` (phát một lần lúc đăng nhập,
không xoay vòng) và gửi nó làm bearer; không còn refresh nào cả.

- `WebSessionManager.SignInAsync(context, AuthSession, persistent)`: `AuthSession` = `SessionToken`, `UserId`,
  `ExpiresAt` (hạn cookie), thay cho `AuthTokenPair`.
- Bỏ: `AuthTokenPair`, `WebSessionTokens`, `IAuthApiClient.RefreshAsync`,
  `SessionBearerHandler.DefaultIsTransientRefreshFailure`, `SessionBearerHandler.ReplayBufferLimitOption`, và các option
  `AccessTokenName`, `AccessTokenExpiresName`, `RefreshTokenName`, `RefreshTokenExpiresName`, `RefreshSkew`,
  `RefreshFallbackMargin`, `RefreshTimeout`, `IsTransientRefreshFailure`.
- `WebSessionTicket` chỉ còn `GetSessionTokenAsync`. `WebSessionPrincipalFactory.CreateSessionOnly` nhận thêm user id.
- Cookie do bản 1.x cấp không có session token: user đó bị coi như đã đăng xuất (`SessionExpiredException` ở lời gọi
  API đầu tiên) và phải đăng nhập lại.

## Cách dùng

Web front end (MVC/Razor) đăng nhập qua một API riêng (phía API là `Auth.Api`). Auth cookie (mã hoá, HttpOnly) CHỈ chứa
dữ liệu bất biến: session token của API, session id và user id. Tên, email, role, permission không bao giờ nằm trong
cookie: Web lấy từ endpoint `me` của API ở mỗi request (`CurrentUserMiddleware`, cache theo phiên trong
`CurrentUserCacheLifetime`, mặc định 10 giây), còn API tự nạp chúng mỗi request (cache riêng 30 giây). Vì vậy đổi tên
hay đổi role của user chỉ hiện ra sau các khoảng cache đó, trừ khi app invalidate `CurrentUserCache` (Web) và
`SessionAccessCache` (API).

API là bên quyết định về phiên: API trượt hạn, thu hồi, và trả 401 khi phiên đã chết. Web không refresh gì cả; cookie
được ghi một lần lúc đăng nhập và bị xoá khi API báo phiên đã chết.

Cài `IAuthApiClient` trên API client của app:

```csharp
public sealed class MyAuthApiClient(IMyApi api) : IAuthApiClient
{
    // API từ chối me (401/403) thì PHẢI ném SessionExpiredException; mọi exception khác coi là lỗi tạm và user
    // KHÔNG bị đăng xuất.
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

Đăng ký:

```csharp
builder.Services.AddDataProtection()                                 // giữ phiên qua lần restart
    .SetApplicationName("my-web").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.AddTqkWebAuth(
    cookie => { cookie.Cookie.Name = "app_auth"; cookie.LoginPath = "/account/login"; },
    options => options.AnonymousPathSuffixes.Add("/auth/login"));
builder.Services.AddHttpClient<IMyApi, MyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
builder.Services.AddScoped<IAuthApiClient, MyAuthApiClient>();

app.UseAuthentication();
app.UseTqkWebSession();   // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();

// Action đăng nhập: giá trị do endpoint login của API trả về
await sessionManager.SignInAsync(HttpContext, new AuthSession
{
    SessionToken = login.SessionToken,     // {sessionId}.{secret}
    UserId = login.UserId,
    ExpiresAt = login.ExpiresAt,           // trần tuyệt đối của phiên (UTC); null = now + ExpireTimeSpan của cookie
}, persistent: model.RememberMe);
// Action đăng xuất
await sessionManager.SignOutAsync(HttpContext);
```

Hạn cookie: ticket hết hạn tại `AuthSession.ExpiresAt` và không bao giờ được cấp lại (`SlidingExpiration = false`).
Truyền vào đó trần TUYỆT ĐỐI của phiên (`SlidingSessionPolicy.GetAbsoluteExpiry` ở API), không phải hạn trượt: API tự
trượt phiên và trả 401 khi phiên đã chết, nên cookie chỉ cần sống lâu hơn phiên. Với `persistent: false` cookie là
cookie phiên của trình duyệt.

`SessionBearerHandler` thêm `Authorization: Bearer {sessionId}.{secret}` vào mọi lời gọi của client được gắn nó, và gửi
đúng một lần. API trả 401 thì ném `SessionExpiredException` (`SessionExpiredMiddleware` xoá cookie và chuyển về trang
đăng nhập, AJAX nhận body JSON 401); mọi status khác và lỗi mạng được trả nguyên. Upload không bao giờ bị buffer (không
có gì được gửi lại). Khách, lời gọi trong `AnonymousApiScope`, lời gọi tự đặt `Authorization`, và đường dẫn trong
`AnonymousPathSuffixes` đi qua nguyên trạng.

Gọi API ẩn danh qua cùng client: bọc lời gọi trong `AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`. Ca điển
hình: hàm tải của cache dùng chung cho mọi người, nếu không token của người đầu tiên sẽ quyết định dữ liệu cho tất cả.
Bọc BÊN TRONG hàm tải. Cờ đi theo từng luồng async (request khác không thấy). Endpoint luôn ẩn danh thì khai ở
`AnonymousPathSuffixes`.

Options (`AddTqkWebAuth(cookie => ..., options => ...)`), đều tuỳ chọn:

| Option | Mặc định | Ý nghĩa |
|---|---|---|
| `CurrentUserTimeout` | 15 giây | Hạn chờ `me`; phải đủ cho API khởi động nguội. |
| `CurrentUserCacheLifetime` | 10 giây | Cache `me` theo phiên. |
| `CurrentUserFailureMode` | `SessionOnly` | `me` lỗi (mạng/5xx/timeout): chạy tiếp với principal chỉ có phiên (session id + user id, không permission), hoặc `Throw` `CurrentUserUnavailableException`. |
| `CurrentUserFailureCacheFor` | `null` | Nhớ lần `me` lỗi theo phiên trong khoảng này, để API sập không làm mọi request chờ `CurrentUserTimeout`. Chỉ nhớ lỗi tạm (lỗi mạng không status, 5xx, 429, timeout); exception khác vẫn theo `CurrentUserFailureMode` nhưng request sau hỏi lại. |
| `SkipCurrentUser` | `null` | `Func<HttpContext, bool>`: không gọi `me` cho request khớp (user đã cache vẫn được dùng). KHÔNG dùng cho endpoint cần role/policy: principal khi đó chỉ có phiên nếu chưa có user trong cache. |
| `IsAjaxRequest` | `X-Requested-With` / `Accept: application/json` | `Func<HttpRequest, bool>` dùng bởi `SessionExpiredMiddleware`. |
| `WriteSessionExpiredAjaxResponse` | 401 `{ success, message, redirect }` | `Func<HttpContext, SessionExpiredException, string redirect, Task>`. |
| `AnonymousPathSuffixes` | rỗng | Đuôi đường dẫn API không bao giờ nhận token (login, quên mật khẩu...). |
| `StableIdentityClaimType` | `"sub"` | Claim mang session id trên mọi principal; xem antiforgery bên dưới. |

`CurrentUserInfo.ExtraClaims` (danh sách `CurrentUserClaim(Type, Value)`) được thêm vào principal của request và cache
cùng phần còn lại trong bộ nhớ, không bao giờ ghi vào cookie. Các loại claim do thư viện quản lý bị bỏ qua: session id
và `StableIdentityClaimType`, `ClaimTypes.Role`, `PermissionClaimType`, `ClaimTypes.NameIdentifier`, `ClaimTypes.Name`
và `ClaimTypes.Email`.

Khi `SessionBearerHandler` ném `SessionExpiredException`, nó còn đăng ký một callback `Response.OnStarting` xoá phiên
Web (xoá cookie) nếu chưa ai làm, nên cookie vẫn bị xoá kể cả khi exception filter của MVC hoặc `try/catch` trong
action xử lý exception thay cho `SessionExpiredMiddleware`. Response đã bắt đầu gửi thì không làm gì (không ghi header
được nữa; request sau sẽ xoá cookie). Đăng nhập lại trong cùng request huỷ lệnh xoá đang chờ, và lỗi khi xoá chỉ được
log, không ném ra.

Antiforgery: ASP.NET Core gắn token của nó với claim `sub` (rồi `NameIdentifier`, rồi `upn`). Mọi principal của Web
(cookie, chỉ có phiên khi API không gọi được, đầy đủ sau `me`) đều mang `sub` = session id của API, nên form mở ở trạng
thái này vẫn post được ở trạng thái kia. Token vì thế gắn với phiên: đăng nhập lại thì form cần token mới. User id nằm
ở `ClaimTypes.NameIdentifier`.

### Phía API

API phát session token (xem `Auth.Api`) nên chạy một job nền xoá theo lô các dòng phiên đã hết hạn hoặc đã thu hồi sau
một khoảng lưu giữ; Web không lưu gì phía server.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (session token mờ, không xoay vòng).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
