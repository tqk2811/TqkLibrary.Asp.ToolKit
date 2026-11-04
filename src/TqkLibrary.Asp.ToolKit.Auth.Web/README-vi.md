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
    // API từ chối me (401 có Bearer challenge) thì PHẢI ném SessionExpiredException; mọi exception khác coi là lỗi tạm và user
    // KHÔNG bị đăng xuất.
    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await api.GetMeAsync(ct);
        response.ThrowIfSessionRejected();     // 401 + Bearer challenge -> SessionExpiredException
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
`TreatAnyUnauthorizedAsSessionExpired = true`: lúc đó mọi 401 đều kết thúc phiên. `ThrowIfSessionRejected` theo cùng quy
tắc (403 không bao giờ tính). Upload không bao giờ bị buffer (không
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
| `TreatAnyUnauthorizedAsSessionExpired` | `false` | `false`: chỉ 401 có `WWW-Authenticate: Bearer` mới kết thúc phiên. `true`: mọi 401 đều kết thúc (API không xây trên `Auth.Api`, không gửi challenge header). |
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

### Luồng chạy

1. `WebSessionManager.SignInAsync` (chỉ HTTPS) thêm
   `Secure-Session-Registration: (ES256 RS256);path="/dbsc/start";challenge="..."` vào response đăng nhập.
2. Trình duyệt tạo khoá rồi POST một JWT đã ký (`Secure-Session-Response`) tới `RegistrationPath`. Web kiểm challenge
   (cấp cho đúng phiên này, chưa hết hạn, chưa dùng), kiểm JWT (`typ` `dbsc+jwt`, `alg` ES256/RS256, public key, chữ
   ký bằng chính khoá đó), gọi `IDeviceKeyApiClient.RegisterDeviceKeyAsync`, cấp lại cookie chính đúng một lần với
   public key trong ticket (giữ nguyên hạn và kiểu persistent), đặt cookie proof và trả session config
   (`session_identifier`, `refresh_url`, `scope`, `credentials`).
3. Cookie proof hết hạn thì trình duyệt POST tới `RefreshPath`: web trả 403 kèm `Secure-Session-Challenge`, trình
   duyệt ký challenge đó, web kiểm bằng khoá trong ticket rồi cấp cookie proof mới. Phiên đã đăng xuất hoặc chưa gắn
   khoá nhận `{"continue": false}`, trình duyệt kết thúc phiên DBSC của nó.
4. Ở mọi request khác, `CurrentUserMiddleware` kiểm cookie proof của phiên đã gắn khoá và áp failure mode bên dưới.

Challenge và proof không lưu trạng thái (Data Protection, purpose riêng; hạn nằm trong payload), nên nhiều instance
chỉ cần dùng chung key ring như cookie chính. Chống replay challenge đã dùng thì giữ trong memory của từng instance
(cố gắng hết mức, không tuyệt đối).

```csharp
builder.Services.AddTqkWebAuth(cookie => { /* ... */ }, o =>
{
    o.DeviceBound.Enabled = true;                              // đặt Ở ĐÂY (kiểm lúc khởi động)
    o.DeviceBound.FailureMode = DeviceProofFailureMode.ReportOnly; // bắt đầu bằng ReportOnly
    o.DeviceBound.ExcludedPaths = ["/health", "/lib"];
});
builder.Services.AddHttpClient<IDeviceKeyApiClient, MyDeviceKeyApi>(c => c.BaseAddress = new(apiUrl)).AddTqkSessionBearer();
```

`Enabled = true` mà không đăng ký `IDeviceKeyApiClient`, hoặc option sai, thì app hỏng ngay lúc khởi động. Đặt
`Enabled` trong delegate của `AddTqkWebAuth` (giống `Scheme`): bước kiểm lúc khởi động và middleware endpoint được
gắn theo giá trị ở đó.

### Option (`WebAuthOptions.DeviceBound`)

| Option | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `Enabled` | `false` | Bật DBSC. Tắt = chạy y hệt khi chưa có tính năng này. |
| `RegistrationPath` / `RefreshPath` | `/dbsc/start` / `/dbsc/refresh` | Endpoint do `DeviceBoundSessionMiddleware` trả lời (chỉ POST). |
| `ProofCookieName` | `null` | Null = `{tên cookie chính}.proof`. Thuộc tính `Path=/; Secure; HttpOnly; SameSite=Lax`, host-only. |
| `ProofLifetime` | 10 phút | Thời gian sống của cookie proof (tối thiểu 1 phút). |
| `RegistrationChallengeLifetime` / `RefreshChallengeLifetime` | 5 phút / 2 phút | Hạn của challenge. |
| `Algorithms` | `ES256`, `RS256` | Thuật toán chữ ký nhận (`none`, HMAC và các loại khác luôn bị từ chối). |
| `MinRsaKeySize` | 2048 | Modulus RSA nhỏ nhất; exponent phải là 65537. |
| `ExcludedPaths` | rỗng | Tiền tố path không kiểm proof; cũng gửi cho trình duyệt dưới dạng luật scope `exclude`. |
| `FailureMode` | `Reject` | Mode cố định, hoặc mode mặc định khi cho người dùng tự chọn. |
| `AllowUserChoice` | `false` | `false` = chính sách cố định, `true` = mỗi người dùng tự chọn (xem dưới). |
| `AllowedUserModes` | cả ba | Các mode người dùng được chọn. |
| `ProofExpiryGrace` | 2 phút | Proof thật, đúng phiên, hết hạn chưa quá khoảng này vẫn tính là hợp lệ. |
| `OnProofFailure` | `null` | `Func<DeviceProofFailureContext, ValueTask<DeviceProofFailureMode>>`, quyết định cuối cùng. |
| `ClearSiteDataOnSignOut` | `false` | Gửi `Clear-Site-Data: "cookies"` khi đăng xuất/drop (xoá MỌI cookie của site). |

### Failure mode

Khi request của phiên đã gắn khoá không có proof, proof hết hạn (quá thời gian ân hạn), proof của phiên khác, hoặc
proof không hợp lệ:

- `ReportOnly`: request chạy bình thường; ghi log Warning và tăng counter.
- `Reject` (mặc định): request chạy như khách. Phiên KHÔNG bị đăng xuất, cookie chính vẫn giữ, nên khi trình duyệt
  làm mới proof xong thì request sau lại chạy bình thường.
- `SignOut`: thu hồi phiên trên API (`IAuthApiClient.LogoutAsync`, cố gắng), xoá cookie chính và cookie proof, request
  chạy như khách.

Cách triển khai khuyên dùng: chạy `ReportOnly` trước, theo dõi counter `tqk.auth.dbsc.proof_failures` (meter
`TqkLibrary.Asp.ToolKit.Auth.Web`, tag `reason` = `missing`/`expired`/`wrong_session`/`invalid`/`unbound`, `mode`),
rồi mới chuyển sang `Reject`. `SignOut` chặt nhất, và biến mọi trục trặc DBSC (lỗi TPM, lỗi trình duyệt) thành một
lần bắt đăng nhập lại.

### Chính sách cố định hay để người dùng chọn

- **Cố định** (`AllowUserChoice = false`, mặc định): `FailureMode` áp cho mọi người; `me.deviceBindingMode` bị bỏ
  qua. Quyết định có trước khi gọi `me`, nên request bị từ chối không gọi API.
- **Người dùng chọn** (`AllowUserChoice = true`): mỗi người dùng chọn mode trong trang cài đặt tài khoản (app lưu,
  `me` trả về ở `deviceBindingMode`). Giá trị được phép thì thắng; không có giá trị, giá trị lạ, hoặc nằm ngoài
  `AllowedUserModes` thì dùng `FailureMode` (giá trị sai được ghi log). Quyết định chờ có user (từ cache hoặc qua
  `me`); `me` lỗi hoặc bị bỏ qua (`SkipCurrentUser`) thì dùng mode mặc định.

Thứ tự quyết định: mặc định của project → lựa chọn của người dùng (khi được phép) → hook `OnProofFailure`. Hook nhận
`HttpContext`, session id, lý do (`Missing`, `Expired`, `WrongSession`, `Invalid`), mode đã quyết tới lúc đó và user
nếu biết; hook trả mode sẽ áp (hook ném exception thì giữ mode đã quyết).

### Hợp đồng với API của app

- `IDeviceKeyApiClient.RegisterDeviceKeyAsync(jwk, algorithm, ct)` lưu public key (JWK public dạng chuẩn, ES256 hoặc
  RS256) vào phiên trên API, qua client có `AddTqkSessionBearer`. Trả `false` khi API từ chối (vd 409: đã gắn khoá,
  hoặc đã quá cửa sổ cho phép gắn khoá ngay sau đăng nhập của API): trình duyệt nhận 403 và phiên vẫn chưa gắn khoá.
  Web kiểm proof-of-possession TRƯỚC lời gọi này; API không phải kiểm lại.
- `me` (`CurrentUserInfo`) có thể trả `isDeviceBound` (phiên đã có khoá) và `deviceBindingMode` (lựa chọn của người
  dùng, `ReportOnly`/`Reject`/`SignOut`, không phân biệt hoa thường; web tự lọc theo danh sách cho phép).
- Đăng ký thành công thì web coi là đã gắn khoá ngay (khoá nằm trong ticket cookie), không chờ `me` báo
  `isDeviceBound`.

### Rủi ro và giới hạn

- Hạ cấp: bản chép cookie chính lấy TRƯỚC lúc gắn khoá không có khoá. Khi `me` báo `isDeviceBound` cho ticket như vậy
  thì request chạy như khách (bất kể mode) và user không được cache. Trước khi API báo (TTL cache) bản chép đó vẫn
  dùng được.
- Cookie proof bị trộm dùng được tới khi hết hạn (`ProofLifetime` + ân hạn): giữ thời gian sống ngắn.
- Request có proof bị từ chối vẫn xác thực được qua lời gọi `HttpContext.AuthenticateAsync(scheme)` tường minh hoặc
  policy `[Authorize]` ghi rõ scheme; bản thân thư viện không bao giờ gửi session token cho request đó.
- Chống replay theo từng instance; sau load balancer, một proof có thể bị phát lại sang instance khác trong thời hạn
  của challenge (vẫn gắn với đúng phiên và khoá đó).
- Bản nháp có thể đổi tên header hoặc field; trình duyệt kết thúc phiên DBSC (mọi 4xx trừ 403 ở refresh) thì để lại
  cookie chính đã gắn khoá mà không có proof, khi đó failure mode xử lý.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc NuGet vào các package khác của toolkit này; API mà nó đăng nhập vào được kỳ vọng dùng cơ chế do [TqkLibrary.Asp.ToolKit.Auth.Api](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) cài đặt (session token mờ, không xoay vòng).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
