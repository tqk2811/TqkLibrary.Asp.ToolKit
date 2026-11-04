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
- `AuthSession.ExpiresAt` giờ là `DateTimeOffset` bắt buộc (trước là `DateTime` tuỳ chọn); không còn fallback về `ExpireTimeSpan` của cookie.
- `SignInAsync` gán `HttpContext.User` bằng principal của phiên mới, nên lời gọi API sau đó trong cùng request đã mang bearer token.
- Cookie do bản 1.x cấp không có session token: user đó bị đăng xuất ngay ở request đầu tiên và phải đăng nhập lại.

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
    // API từ chối me (MỌI 401) thì PHẢI ném SessionExpiredException; mọi exception khác coi là lỗi tạm và user
    // KHÔNG bị đăng xuất.
    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await api.GetMeAsync(ct);
        response.ThrowIfSessionRejected();     // mọi 401 từ me -> SessionExpiredException
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
    ExpiresAt = login.ExpiresAt,           // trần tuyệt đối của phiên (DateTimeOffset, bắt buộc)
}, persistent: model.RememberMe);
// Action đăng xuất
await sessionManager.SignOutAsync(HttpContext);
```

Cookie không có session token (do bản 1.x cấp) bị từ chối và đăng xuất ngay khi cookie được kiểm tra, ở request đầu tiên,
dù request đó có gọi API hay không. `Events.OnValidatePrincipal` của app chạy sau bước kiểm này và chỉ chạy với cookie có token. Bước kiểm được gắn vào đối tượng
`Events` có sẵn lúc `AddTqkWebAuth` chạy, nên KHÔNG áp dụng khi app dùng `options.EventsType` hoặc gán lại `Events` (hay
`OnValidatePrincipal`) sau `AddTqkWebAuth`; khi đó cookie không token chỉ bị từ chối lúc gọi API.

Phiên đã chết bên API (bị thu hồi, hết hạn, user bị khoá) chỉ được phát hiện khi request gọi API: lời gọi `me` của
`CurrentUserMiddleware` cũng tính, và mọi lời gọi qua `SessionBearerHandler`. Trang không gọi API nào (vd dùng
`SkipCurrentUser`, hoặc `me` lấy từ cache) vẫn hiện là đã đăng nhập cho tới khi cookie hết hạn hoặc lời gọi API kế tiếp trả 401.

Hạn cookie: ticket hết hạn tại `AuthSession.ExpiresAt` và không bao giờ được cấp lại (`SlidingExpiration = false`).
Truyền vào đó trần TUYỆT ĐỐI của phiên (`SlidingSessionPolicy.GetAbsoluteExpiry` ở API), không phải hạn trượt: API tự
trượt phiên và trả 401 khi phiên đã chết, nên cookie chỉ cần sống lâu hơn phiên. Với `persistent: false` cookie là
cookie phiên của trình duyệt.

`SessionBearerHandler` thêm `Authorization: Bearer {sessionId}.{secret}` vào mọi lời gọi của client được gắn nó, và gửi
đúng một lần. API trả 401 có header `WWW-Authenticate: Bearer` (đúng thứ `Auth.Api` gửi khi phiên đã chết) thì ném
`SessionExpiredException` (`SessionExpiredMiddleware` xoá cookie và chuyển về trang đăng nhập, AJAX nhận body JSON 401).
401 KHÔNG có header đó là 401 nghiệp vụ của endpoint (ví dụ nhập sai mã 2FA ở trang `[Authorize]`) và được trả nguyên,
user vẫn đăng nhập; mọi status khác và lỗi mạng cũng được trả nguyên. Nếu `Auth.Api` của bạn đặt `OnChallenge` thì phải
giữ header `WWW-Authenticate`. Với API không xây trên `Auth.Api` và không gửi challenge header, đặt
`TreatAnyUnauthorizedAsSessionExpired = true`: lúc đó mọi 401 đều kết thúc phiên (tuỳ chọn này áp dụng cho
`SessionBearerHandler`, tức các lời gọi khác `me`).
Với `me`, `ThrowIfSessionRejected` coi MỌI 401 là phiên đã kết thúc (có hay không có header; `me` không có 401 nghiệp vụ,
và proxy có thể cắt `WWW-Authenticate`, khiến user đăng nhập mãi mà không có quyền). 403 không bao giờ tính.
Phía API: trả 401 nghiệp vụ bằng `Results.Unauthorized()` / `Unauthorized()` / `Problem(statusCode: 401)`, TUYỆT ĐỐI KHÔNG
dùng `Challenge()` / `Results.Challenge()` / `HttpContext.ChallengeAsync()` (challenge thêm `WWW-Authenticate: Bearer`,
làm phiên web kết thúc); và đặt scheme session-token làm `DefaultChallengeScheme` (hoặc scheme của mọi endpoint mà Web
gọi), vì một scheme Bearer khác (ví dụ JwtBearer) challenge cùng request cũng sẽ kết thúc phiên. Upload không bao giờ bị buffer (không
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
| `TreatAnyUnauthorizedAsSessionExpired` | `false` | Áp dụng cho `SessionBearerHandler` (các lời gọi khác `me`; `me` luôn coi mọi 401 là phiên đã kết thúc). `false`: chỉ 401 có `WWW-Authenticate: Bearer` mới kết thúc phiên. `true`: mọi 401 đều kết thúc (API không xây trên `Auth.Api`, không gửi challenge header). |
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

## Device Bound Session Credentials (DBSC)

Tuỳ chọn, mặc định tắt. Với DBSC, trình duyệt hỗ trợ giữ một cặp khoá trong thiết bị (TPM) và gắn phiên web vào
khoá đó: ngoài cookie chính, web cấp thêm một **cookie proof** sống ngắn, trình duyệt tự làm mới bằng cách ký
challenge của server bằng khoá thiết bị. Cookie chính bị chép sang máy khác sẽ hết dùng được khi proof hết hạn (mặc
định 10 phút), vì kẻ trộm không ký được. Trình duyệt không hỗ trợ DBSC bỏ qua các header này và vẫn chạy bằng cookie
chính như cũ.

Hỗ trợ trình duyệt (11/2026): Chrome 145+ trên Windows có TPM, chính thức từ 146; macOS từ 147 (đang mở dần). Trình
duyệt khác bỏ qua. Đặc tả là bản nháp W3C (<https://w3c.github.io/webappsec-dbsc/>), còn có thể đổi.

**API là nguồn sự thật duy nhất của khoá thiết bị**: web không bao giờ lưu khoá trong cookie. Web biết phiên đã gắn
khoá chưa, và khoá là gì, qua `me` (`deviceKeyJwk`, `deviceKeyAlgorithm`), rồi giữ trong một binding cache nhỏ trong bộ
nhớ của từng instance (mỗi mục sống tới hạn tuyệt đối của phiên, tối đa 30 ngày, giới hạn 100 000 phiên, tách riêng
khỏi `IMemoryCache` của app). Phiên đã biết là gắn khoá thì luôn được coi là gắn khoá. Câu trả lời "chưa gắn khoá"
của một lượt `me` mới chỉ được tin trong `UnboundRecheckInterval` (30 giây); quá khoảng đó, hoặc khi chưa biết gì
(instance mới khởi động), web hỏi lại `me` để biết, kể cả trên path `SkipCurrentUser` và khi user trong cache vẫn ghi
"chưa gắn khoá".

### Luồng chạy

1. `WebSessionManager.SignInAsync` (chỉ HTTPS) thêm
   `Secure-Session-Registration: (ES256 RS256);path="/dbsc/start";challenge="..."` vào response đăng nhập.
2. Trình duyệt tạo khoá rồi POST một JWT đã ký (`Secure-Session-Response`) tới `RegistrationPath`. Web kiểm challenge
   (cấp cho đúng phiên này, chưa hết hạn, chưa dùng), kiểm JWT (`typ` `dbsc+jwt`, `alg` nằm trong `Algorithms`, public
   key, chữ ký bằng chính khoá đó), gọi `IDeviceKeyApiClient.RegisterDeviceKeyAsync`, ghi nhận việc gắn khoá trong bộ
   nhớ, bỏ kết quả `me` đang cache, đặt cookie proof và trả session config (`session_identifier`, `refresh_url`,
   `scope`, `credentials`). Cookie chính không bị đụng tới.
3. Cookie proof hết hạn thì trình duyệt POST tới `RefreshPath` (chỉ HTTPS): web lấy khoá đã lưu từ binding cache, chưa
   có thì hỏi `me`, trả 403 kèm `Secure-Session-Challenge`, kiểm challenge đã ký bằng khoá đó rồi cấp cookie proof mới.
   Phiên đã đăng xuất, chưa gắn khoá, hoặc request qua HTTP nhận `{"continue": false}`, trình duyệt kết thúc phiên DBSC
   của nó; `me` không trả lời được thì nhận 503 (trình duyệt bỏ qua lượt refresh đó).
4. Ở mọi request khác, `CurrentUserMiddleware` kiểm cookie proof của phiên đã gắn khoá và giao lỗi cho failure handler
   bên dưới.

Challenge và proof không lưu trạng thái (Data Protection, purpose riêng; hạn nằm trong payload), nên nhiều instance
chỉ cần dùng chung key ring như cookie chính. Cookie proof mang session id và thumbprint RFC 7638 của khoá thiết bị;
proof làm cho khoá khác là `Invalid`. Chống replay challenge đã dùng thì giữ trong memory của từng instance (cố gắng
hết mức), khoá theo byte đã giải mã của challenge.

**Mất response đăng ký** (API đã lưu khoá nhưng câu trả lời không về tới trình duyệt) không làm khoá chặt người dùng:
request kế tiếp không có proof là một lỗi proof (`Missing`), còn lượt refresh lấy khoá qua `me` và cấp proof cho đúng
cookie chính cũ, không đổi.

```csharp
builder.Services.AddTqkWebAuth(cookie => { /* ... */ }, o =>
{
    o.DeviceBound.Enabled = true;
    o.DeviceBound.FailureMode = DeviceProofFailureMode.ReportOnly; // bắt đầu bằng ReportOnly
    o.DeviceBound.ExcludedPaths = ["/health", "/lib"];
});
builder.Services.AddHttpClient<IDeviceKeyApiClient, MyDeviceKeyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
```

`Enabled = true` mà không đăng ký `IDeviceKeyApiClient`, hoặc option sai, thì app hỏng ngay lúc khởi động, dù đặt
trong delegate của `AddTqkWebAuth` hay trong một `Configure<WebAuthOptions>` sau đó.

### Option (`WebAuthOptions.DeviceBound`)

| Option | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `Enabled` | `false` | Bật DBSC. Tắt = chạy y hệt khi chưa có tính năng này. |
| `RegistrationPath` / `RefreshPath` | `/dbsc/start` / `/dbsc/refresh` | Endpoint do `DeviceBoundSessionMiddleware` trả lời (chỉ POST). |
| `ProofCookieName` | `null` | Null = `{tên cookie chính}.proof`. Thuộc tính `Path=/; Secure; HttpOnly; SameSite=Lax`, host-only. |
| `ProofLifetime` | 10 phút | Thời gian sống của cookie proof (tối thiểu 1 phút). |
| `RegistrationChallengeLifetime` / `RefreshChallengeLifetime` | 5 phút / 2 phút | Hạn của challenge. |
| `Algorithms` | `ES256`, `RS256` | Thuật toán nhận cho lượt đăng ký MỚI. Phiên đã gắn khoá vẫn chạy với thuật toán của khoá đã lưu dù bị bỏ khỏi đây. |
| `MinRsaKeySize` | 2048 | Modulus RSA nhỏ nhất cho lượt đăng ký mới; exponent phải là 65537. Khoá đã lưu được đọc với ngưỡng sàn 2048. |
| `ExcludedPaths` | rỗng | Tiền tố path BỎ QUA hoàn toàn việc kiểm proof (cookie bị chép không có proof vẫn dùng được ở đó, kèm session token); cũng gửi cho trình duyệt dưới dạng luật scope `exclude`. Chỉ loại trừ path không lộ gì nhạy cảm. |
| `FailureMode` | `Reject` | Mode cố định, hoặc mode mặc định khi cho người dùng tự chọn (failure handler mặc định dùng nó). |
| `AllowUserChoice` | `false` | `false` = chính sách cố định, `true` = mỗi người dùng tự chọn (xem dưới). |
| `AllowedUserModes` | cả ba | Các mode người dùng được chọn. |
| `ProofExpiryGrace` | 2 phút | Proof thật, đúng phiên, hết hạn chưa quá khoảng này vẫn tính là hợp lệ. |
| `ClearSiteDataOnSignOut` | `false` | Gửi `Clear-Site-Data: "cookies"` khi đăng xuất/drop (xoá MỌI cookie của site). |
| `UnboundRecheckInterval` | 30 giây | Thời gian tin câu trả lời "chưa gắn khoá" của một lượt `me` mới khi quyết định việc gắn khoá (lớn hơn 0, tối đa 10 phút). Trong khoảng này phiên chưa gắn khoá không phải gọi `me` chỉ để biết việc gắn khoá; đây cũng là cửa sổ hở khi chạy nhiều instance ngay sau lượt đăng ký (xem Rủi ro). |

### Xử lý khi proof lỗi

Khi request của phiên đã gắn khoá không có proof (`Missing`), proof hết hạn quá thời gian ân hạn (`Expired`), proof
của phiên khác (`WrongSession`), hoặc proof không hợp lệ hay làm cho khoá khác (`Invalid`), thư viện hỏi
`DeviceProofFailureHandler` (singleton) để lấy một `DeviceProofOutcome`:

- `Continue` (= `ReportOnly`): request chạy bình thường; tăng counter và ghi log mức Debug.
- `Reject` (mặc định): request chạy như khách, KHÔNG có session token, cho tới hết request: endpoint có
  `AuthenticationSchemes` tường minh xác thực lại cookie, hay trang lỗi do `UseExceptionHandler` chạy lại, vẫn không gửi
  token và một 401 ở đó không xoá cookie. Xác thực lại scheme cookie cho tới hết request đều trả "không có kết quả",
  nên endpoint nội bộ có policy ghi tên scheme cookie (`[Authorize(AuthenticationSchemes = ...)]`,
  `AddAuthenticationSchemes`) bị challenge chứ không chạy (`AddTqkWebAuth` bọc `IAuthenticationService` đã đăng ký để
  làm việc này; thay service đó sau `AddTqkWebAuth` thì mất lớp bọc). Phiên KHÔNG bị đăng xuất, cookie chính vẫn giữ,
  nên khi trình duyệt làm mới proof xong thì request sau lại chạy bình thường.
- `SignOut`: thu hồi phiên trên API (`IAuthApiClient.LogoutAsync`, cố gắng), xoá cookie chính và cookie proof, request
  chạy như khách.
- `Handled` (chỉ handler tự viết): handler đã tự ghi response; pipeline dừng ngay tại đó, không gửi token, không cache
  user, hai cookie để nguyên.

Handler mặc định trả `context.DefaultMode` dưới dạng outcome: `FailureMode` cố định, hoặc lựa chọn hợp lệ của người
dùng (xem dưới). Muốn tuỳ biến thì kế thừa nó và đăng ký lớp con (trước hay sau `AddTqkWebAuth` đều được); service
scoped lấy từ `context.HttpContext.RequestServices`:

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
        return await base.HandleAsync(context);   // FailureMode / lựa chọn của người dùng
    }
}

builder.Services.AddSingleton<DeviceProofFailureHandler, MyProofFailureHandler>();
```

`DeviceProofFailureContext` có `HttpContext`, `SessionId`, `Reason`, `DefaultMode` và `User` (nếu đã biết). Handler
ném exception (trừ lệnh huỷ do request bị abort) hoặc trả giá trị không định nghĩa thì được ghi log và outcome mặc định
được áp. `DeviceProofFailureMode` vẫn là kiểu của option và của lựa chọn người dùng, nên không ai cấu hình hay chọn
được `Handled`.

Cách triển khai khuyên dùng: chạy `ReportOnly` trước, theo dõi counter `tqk.auth.dbsc.proof_failures` (meter
`TqkLibrary.Asp.ToolKit.Auth.Web`, tag `reason` = `missing`/`expired`/`wrong_session`/`invalid`, `mode` =
`report_only`/`reject`/`sign_out`/`handled`), rồi mới chuyển sang `Reject`. Dòng log mỗi request cố ý để mức Debug:
trình duyệt mất phiên DBSC sẽ lỗi ở MỌI request cho tới lần đăng nhập sau. `SignOut` chặt nhất, và biến mọi trục trặc
DBSC (lỗi TPM, lỗi trình duyệt) thành một lần bắt đăng nhập lại.

### Chính sách cố định hay để người dùng chọn

- **Cố định** (`AllowUserChoice = false`, mặc định): `FailureMode` áp cho mọi người; `me.deviceBindingMode` bị bỏ
  qua. Với phiên đã biết là gắn khoá, quyết định có trước khi gọi `me`, nên request bị từ chối không gọi API.
- **Người dùng chọn** (`AllowUserChoice = true`): mỗi người dùng chọn mode trong trang cài đặt tài khoản (app lưu,
  `me` trả về ở `deviceBindingMode`). Giá trị được phép thì thắng; không có giá trị, giá trị rỗng/khoảng trắng, giá
  trị lạ, hoặc nằm ngoài `AllowedUserModes` thì dùng `FailureMode`. Quyết định chờ có user (từ cache hoặc qua `me`);
  `me` lỗi hoặc bị bỏ qua (`SkipCurrentUser`) thì dùng mode mặc định. Ngoại lệ của "proof lỗi thì không gửi token": để
  biết mode của người dùng, chính sách này buộc phải gọi `me` CÓ session token trước. Phiên mà instance chưa biết là
  đã gắn khoá cũng vậy (phải hỏi `me`).

Khi `me` lỗi (hoặc `SkipCurrentUser` áp dụng) mà phiên đã biết là gắn khoá, proof vẫn được kiểm (không biết user),
TRƯỚC khi `CurrentUserFailureMode.Throw` kịp làm hỏng request: request đã thành khách vì proof lỗi thì không ném.
`SkipCurrentUser` không bao giờ bỏ qua việc gắn khoá CHƯA BIẾT: request đó vẫn gọi `me` một lần để biết (user trả về
được cache và áp dụng; lỗi ở lượt này không bị ném). Khi không biết gì về việc gắn khoá mà `me` lỗi (instance mới khởi
động đúng lúc API sập), phiên được coi là chưa gắn khoá cho request đó: đây là lỗ hổng còn lại của thiết kế.

### Hợp đồng với API của app

- `me` (`CurrentUserInfo`) phải trả `deviceKeyJwk` và `deviceKeyAlgorithm` (khoá đã lưu, null khi chưa gắn) và có thể
  trả `deviceBindingMode` (lựa chọn của người dùng, `ReportOnly`/`Reject`/`SignOut`, không phân biệt hoa thường; web tự
  lọc theo danh sách cho phép). `IsDeviceBound` suy ra từ `deviceKeyJwk`. Auth.Api có sẵn `GetDeviceBinding` cho việc
  này. Thiếu `deviceKeyAlgorithm` thì web suy ra từ loại khoá của JWK (`EC` → `ES256`, `RSA` → `RS256`) như một đường
  lùi; vẫn nên trả về.
- `IDeviceKeyApiClient.RegisterDeviceKeyAsync(jwk, algorithm, ct)` lưu public key (JWK public dạng chuẩn, ES256 hoặc
  RS256) vào phiên trên API, qua client có `AddTqkSessionBearer`. Trả `false` cho HTTP 409 (đã gắn khoá, đã thu hồi/hết
  hạn, hoặc đã quá cửa sổ gắn khoá ngay sau đăng nhập của API) và 400, KHÔNG ném exception cho các mã này: trình duyệt
  nhận 400. Lỗi tạm thời được phép ném: trình duyệt nhận 503. Web kiểm proof-of-possession TRƯỚC lời gọi này; API
  không phải kiểm lại.

### Rủi ro và giới hạn

- Trình duyệt mất phiên DBSC (xoá dữ liệu site, `continue: false`, mọi 4xx trừ 403 ở refresh) thì phiên đã gắn khoá
  không còn proof: với `Reject`/`SignOut` người dùng phải đăng nhập lại, vì chỉ đăng ký khoá ngay sau đăng nhập (API chỉ
  nhận khoá trong cửa sổ đăng ký, mặc định 10 phút).
- Cookie proof bị trộm dùng được tới khi hết hạn (`ProofLifetime` + ân hạn), và chỉ đi cùng đúng cookie chính của phiên
  đó: giữ thời gian sống ngắn.
- `ExcludedPaths` bỏ qua hoàn toàn việc kiểm proof.
- Instance mới + API sập: instance chưa từng thấy phiên thì không biết phiên đã gắn khoá khi `me` lỗi, và coi là chưa
  gắn khoá tới khi `me` trả lời lại.
- Nhiều instance: ngay sau lượt đăng ký ở một instance, instance khác vừa thấy phiên chưa gắn khoá sẽ không kiểm proof
  của phiên đó trong tối đa `UnboundRecheckInterval`.
- Chống replay theo từng instance; sau load balancer, một proof có thể bị phát lại sang instance khác trong thời hạn
  của challenge (vẫn gắn với đúng phiên và khoá đó).
- Bản nháp có thể đổi tên header hoặc field.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (session token mờ, không xoay vòng).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
