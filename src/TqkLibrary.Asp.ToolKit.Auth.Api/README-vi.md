# TqkLibrary.Asp.ToolKit.Auth.Api

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Api/README.md) · **Tiếng Việt**

Xác thực API ASP.NET Core bằng session token mờ (opaque), không xoay vòng (`{sessionId}.{secret}`, chỉ lưu SHA-256 của secret): một authentication handler kiểm phiên ở mọi request qua cache ngắn trong bộ nhớ, và hạn trượt (sliding) có giới hạn tần suất ghi cùng trần tuyệt đối.

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Api
```

## Thay đổi breaking ở 2.0.0

2.0.0 bỏ JWT. Không còn access token lẫn refresh token: session token `{sessionId}.{secret}` được phát một lần lúc
đăng nhập và gửi dạng `Authorization: Bearer {sessionId}.{secret}` ở mọi lời gọi.

- Bỏ: `AccessTokenFactory`, `IssuedAccessToken`, `SessionTokenValidator`, `AddTqkJwtBearer`,
  `UseTqkSessionValidation`, các option `Issuer`, `Audience`, `SigningKey`, `AccessTokenLifetime`, `ClockSkew`,
  `EffectiveAudience`, `CreateSigningKey`, `CreateTokenValidationParameters`, và phụ thuộc
  `Microsoft.AspNetCore.Authentication.JwtBearer`.
- Đổi tên: `RefreshTokenCodec` → `SessionTokenCodec`, `IssuedRefreshToken` → `IssuedSessionToken`.
- `ISessionAccessSource.GetAsync(sessionId, ct)` (không còn user id) trả về `SessionAccessRecord?` (null = không có
  phiên): user id, hash secret, `CreatedAt`, `LastAccessAt`, `ExpiresAt`, `IsPersistent`, `IsRevoked`,
  `RevokedReason`, `UserRejectionReason`, cùng tên/email/role/permission. Mọi thời điểm là UTC. Thư viện kiểm secret
  trước, rồi mới kiểm user, thu hồi và hạn. Thêm `TouchAsync` để lưu hạn trượt.
- Scheme xác thực: `AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(...)`; body 401
  riêng chuyển sang `SessionTokenAuthenticationOptions.OnChallenge`, còn `GetSessionRejection` / `IsSessionRejected`
  thành extension của `HttpContext`.
- `SessionAccessCache.GetAsync` bỏ tham số user id và nạp `SessionAccessRecord?`. `SessionRejectionReasons.NoSession` /
  `UserMismatch` được thay bằng `InvalidToken`; không còn kiểu kết quả grant/reject.
- Token phải có session id tối đa 128 ký tự `A-Z a-z 0-9 - _` và secret base64url đúng 43 ký tự (đúng thứ
  `SessionTokenCodec.Create` cấp); `Create` ném exception với session id khác.
- Bỏ endpoint refresh của API; endpoint login trả session token (xem dưới).

## Cách dùng

API sở hữu phiên. App giữ bảng phiên (trong database của app; thư viện không có entity) với tối thiểu: id, user id,
hash secret, thời điểm tạo, lần truy cập cuối, hạn, cờ persistent, cờ thu hồi. Gắn thiết bị
thêm hai cột tuỳ chọn (xem [Gắn phiên với thiết bị (DBSC)](#gắn-phiên-với-thiết-bị-dbsc)); định dạng token không đổi.

Cài `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, CancellationToken ct)
    {
        var row = await db.Sessions.Where(s => s.Id == sessionId)
            .Select(s => new { Session = s, s.User })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;                                                           // phiên không tồn tại
        // Trả cả phiên đã thu hồi/hết hạn/user bị khoá: thư viện kiểm secret trước, rồi mới kiểm phần còn lại.
        return new SessionAccessRecord
        {
            UserId = row.User.Id, SecretHash = row.Session.SecretHash,
            CreatedAt = row.Session.CreatedAt, LastAccessAt = row.Session.LastAccessAt, ExpiresAt = row.Session.ExpiresAt,
            IsPersistent = row.Session.IsPersistent, IsRevoked = row.Session.RevokedAt != null,
            RevokedReason = row.Session.RevokedReason,                                     // mã riêng của app (tuỳ chọn), vd "password_changed"
            UserRejectionReason = row.User.IsLocked ? SessionRejectionReasons.UserLocked : null,
            UserName = row.User.UserName, Email = row.User.Email, Roles = roles, Permissions = permissions,
        };
    }

    // Gọi tối đa một lần mỗi SessionTouchInterval cho mỗi phiên trong một process; lỗi chỉ được log, request không hỏng.
    public Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken ct)
        => db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAccessAt, lastAccessAt).SetProperty(s => s.ExpiresAt, expiresAt), ct);
}
```

Mọi `DateTime` của record là UTC (`Unspecified` coi như UTC, `Local` được đổi sang UTC). `UserRejectionReason` (vd
`user_locked`) và `RevokedReason` chỉ được báo cho người gọi có secret đúng; secret sai hoặc session id không tồn tại
luôn trả `invalid_token`, nên endpoint không dùng để dò phiên hay user được. Chỉ record tìm thấy mới được cache; kết quả
"không có phiên" (null) không bao giờ được cache, nên phiên vừa tạo thấy được ngay ở request kế tiếp trên mọi instance.
Session id PHẢI không đoán được (GUID ngẫu nhiên hoặc byte ngẫu nhiên), không dùng id tăng dần.

`GetAsync` ném exception thì không có rejection nào được ghi, request hỏng với exception đó thay vì 401.

Đăng ký:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    // Phiên thường 7 ngày, phiên "ghi nhớ đăng nhập" 30 ngày (Persistent null = như Normal). Mặc định: 30 ngày.
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);        // trần tuyệt đối, tuỳ chọn; mặc định không có
    o.SessionTouchTimeout = TimeSpan.FromSeconds(5);     // mặc định; thời gian tối đa của một TouchAsync (không dùng RequestAborted)
    o.SessionTouchInterval = TimeSpan.FromHours(1);      // mặc định; bao lâu ghi hạn trượt một lần
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(o =>
{
    // Tuỳ chọn: body 401 riêng kèm lý do từ chối. Status 401 và WWW-Authenticate đã được đặt sẵn;
    // giữ nguyên header này (package Web dựa vào nó).
    o.OnChallenge = async context =>
    {
        if (context.Rejection is not { } rejection) return;     // không có token: giữ 401 trơn mặc định
        context.HandleResponse();
        await context.HttpContext.Response.WriteAsJsonAsync(new { code = "session_rejected", reason = rejection.Reason });
    };
});
```

Ở mỗi request có `Authorization: Bearer {sessionId}.{secret}`, handler:

1. tách token (sai dạng → 401 `invalid_token`, không gọi source);
2. lấy phiên qua `SessionAccessCache` (mặc định 30 giây) → `ISessionAccessSource.GetAsync`; không có phiên →
   `invalid_token` (sau một lần kiểm hash giả, để tốn thời gian ngang với secret sai);
3. so hash secret trong thời gian cố định (`invalid_token`), rồi kiểm `UserRejectionReason` (trả đúng giá trị đó),
   `IsRevoked` (`RevokedReason`, null thì `revoked`), `ExpiresAt` và trần tuyệt đối `CreatedAt + MaxSessionLifetime`
   (`expired`);
4. lần ghi trước đã cũ hơn `SessionTouchInterval` thì gọi `TouchAsync(sessionId, now, newExpiry)` với
   `newExpiry = min(now + lifetime, CreatedAt + MaxSessionLifetime)` (mỗi phiên chỉ một lượt ghi cùng lúc trong một
   process; lỗi hoặc quá `SessionTouchTimeout` được log mức warning và thử lại sau tối đa một phút; lượt ghi không dùng
   cancellation token của request) rồi cập nhật bản ghi trong cache;
5. dựng principal: `ClaimTypes.NameIdentifier` và `UserIdClaimType` (`sub`) = user id, `SessionIdClaimType` (`sid`),
   `ClaimTypes.Name`, `ClaimTypes.Email`, `ClaimTypes.Role`, `PermissionClaimType` (`permission`), và khi gắn thiết bị
   `device_bound` / `device_key_jwk` / `device_key_alg` / `device_binding_mode` (xem [Gắn phiên với thiết bị (DBSC)](#gắn-phiên-với-thiết-bị-dbsc)).

Request không có header bearer thì không có kết quả (endpoint ẩn danh vẫn chạy, `[Authorize]` trả 401). Lý do từ chối
(`SessionRejection(Reason, Message)`) đọc được trong `OnChallenge` và qua `HttpContext.GetSessionRejection()`. Token
không bao giờ bị ghi log.

Một challenge luôn trả 401 kèm `WWW-Authenticate: Bearer error="invalid_token"` (request không gửi token thì chỉ có
`Bearer`, theo RFC 6750). Cả hai được đặt TRƯỚC khi `OnChallenge` chạy, nên hook được phép ghi body riêng nhưng
KHÔNG được xoá header `WWW-Authenticate`: `Auth.Web` chỉ kết thúc phiên của user khi nhận 401 có Bearer challenge này,
đó là cách nó phân biệt phiên đã chết với một 401 nghiệp vụ của endpoint (ví dụ nhập sai mã 2FA).

Quy tắc cho endpoint của bạn để 401 nghiệp vụ không làm user bị đăng xuất:

- Trả 401 nghiệp vụ bằng `Results.Unauthorized()` / `Unauthorized()` / `Problem(statusCode: 401)` (chỉ status). TUYỆT ĐỐI
  KHÔNG dùng `Challenge()` / `Results.Challenge()` / `HttpContext.ChallengeAsync()` cho việc này: challenge đi qua
  handler session-token và thêm `WWW-Authenticate: Bearer`, khiến phiên web của user kết thúc.
- `DefaultChallengeScheme` của API (hoặc scheme của mọi endpoint mà Web gọi) phải là scheme session-token. Nếu một scheme
  Bearer khác (ví dụ JwtBearer) cũng challenge cùng request, `WWW-Authenticate: Bearer` của nó cũng kết thúc phiên web.
- Tóm tắt những gì `Auth.Web` coi là phiên đã kết thúc: với `me` là mọi 401; với các lời gọi khác chỉ 401 có Bearer
  challenge; 403 không bao giờ.

Đăng nhập và đăng xuất (trong auth service của app):

```csharp
// Đăng nhập
var issued = SessionTokenCodec.Create(sessionId);       // lưu issued.SecretHash cùng phiên, không bao giờ lưu token
var expires = slidingPolicy.GetInitialExpiry(now, persistent: model.RememberMe);
// ... thêm dòng phiên (CreatedAt = now, ExpiresAt = expires, IsPersistent = RememberMe)
return new { sessionToken = issued.Token, userId, expiresAt = slidingPolicy.GetAbsoluteExpiry(now) };   // hạn cookie cho Web

// Đăng xuất (endpoint có xác thực): đánh dấu phiên đã thu hồi, rồi
sessionAccessCache.InvalidateSession(sessionId);
```

`SlidingSessionPolicy` không `sealed` và các method đều `virtual`. `AddTqkApiAuth` đăng ký nó bằng `TryAddSingleton`,
nên subclass đăng ký TRƯỚC sẽ được dùng:

```csharp
builder.Services.AddSingleton<SlidingSessionPolicy, MySlidingSessionPolicy>();
builder.Services.AddTqkApiAuth(o => { ... });
```

### Cache và dữ liệu cũ

`SessionAccessCache` giữ bản ghi của mỗi phiên trong `SessionAccessCacheLifetime` (mặc định 30 giây) theo process. Vì
vậy đổi tên, email, role, permission của user, hoặc thu hồi phiên, chỉ có hiệu lực sau khoảng đó TRỪ KHI app gọi
invalidate ngay sau khi ghi thành công:

- `InvalidateSession(sessionId)`: đăng xuất / thu hồi một phiên;
- `InvalidateUser(userId)`: đổi mật khẩu, khoá/xoá tài khoản, đổi role hoặc tên/email, và cả khi MỞ KHOÁ/bật lại user
  (trạng thái khoá cũng bị cache);
- `InvalidateAll()`: permission của role đổi, role bị đổi tên/xoá.

Lượt tra cứu đang chạy dở lúc invalidate không được dùng lại. Nhiều instance API thì invalidate chỉ tới instance xử
lý lần ghi; các instance khác trễ tối đa thời gian cache.

### Gắn phiên với thiết bị (DBSC)

Device Bound Session Credentials gắn một phiên với cặp khoá nằm trên thiết bị của user: phía Web front end
([TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web)) kiểm các bằng chứng
(proof) của thiết bị; API chỉ lưu public key một lần cho mỗi phiên và báo cho front end qua claim là phiên đã gắn hay
chưa. App không dùng DBSC thì bỏ qua cả mục này.

Thêm hai cột nullable vào bảng phiên: `DevicePublicKey` (JWK public dạng chuẩn hoá, text tối đa 4 KB) và
`DeviceKeyAlgorithm` (`ES256` hoặc `RS256`). Tuỳ chọn lưu thêm lựa chọn riêng của user `DeviceBindingMode` (vd ở bảng
user, do CHÍNH USER đặt ở trang cài đặt tài khoản): `ReportOnly`, `Reject` hoặc `SignOut`, null = mặc định của
project. Web front end chỉ theo lựa chọn này khi developer bật chính sách "mềm" (cho user tự chọn); với chính sách
"cứng" thì mode developer cố định được áp dụng và giá trị của user bị bỏ qua. API không bao giờ diễn giải giá trị này.

Điền bản ghi trong `GetAsync`:

```csharp
DevicePublicKeyJwk = row.Session.DevicePublicKey,      // null = chưa gắn (IsDeviceBound suy ra từ đây)
DeviceKeyAlgorithm = row.Session.DeviceKeyAlgorithm,  // nên lưu và trả về (web chỉ suy ra từ kty của JWK khi thiếu)
DeviceBindingMode = row.User.DeviceBindingMode,         // null = mặc định của project
```

Khi đã gắn, principal có `DeviceBoundClaimType` (`device_bound`) = `true`, `DeviceKeyJwkClaimType` (`device_key_jwk`,
JWK đã lưu) và `DeviceKeyAlgorithmClaimType` (`device_key_alg`); `DeviceBindingModeClaimType` (`device_binding_mode`)
được thêm khi có mode. Chưa gắn thì không có các claim này. Mọi claim type là option của `AddTqkApiAuth` (bắt buộc khác
rỗng).

API là nguồn sự thật duy nhất của khoá thiết bị: Web front end KHÔNG giữ khoá trong cookie, nó đọc khoá qua `me` (cache
trong bộ nhớ) khi cần.

Viết `IDeviceKeyStore` bằng MỘT câu lệnh nguyên tử, chỉ ghi khoá vào phiên còn sống (chưa revoke, chưa hết hạn), chưa
có khoá và được tạo trong cửa sổ đăng ký, trả true khi và chỉ khi đúng một dòng bị đổi (mỗi phiên đăng ký khoá một lần
và không bao giờ bị thay, để token phiên bị lộ không gắn lại được phiên sang thiết bị khác). `false` gồm các trường hợp:
đã có khoá, đã revoke, đã hết hạn, đã bị xoá, hoặc cửa sổ đăng ký đã đóng:

```csharp
public sealed class DeviceKeyStore(AppDbContext db) : IDeviceKeyStore
{
    // UPDATE Sessions SET DevicePublicKey=@key, DeviceKeyAlgorithm=@alg WHERE Id=@id AND RevokedAt IS NULL
    //   AND DevicePublicKey IS NULL AND ExpiresAt > @now AND (@createdNotBefore IS NULL OR CreatedAt >= @createdNotBefore)
    public async Task<bool> TrySetDeviceKeyAsync(string sessionId, string publicKeyJwk, string algorithm,
        DateTimeOffset now, DateTimeOffset? createdNotBefore, CancellationToken ct)
        => await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null && s.DevicePublicKey == null
                && s.ExpiresAt > now.UtcDateTime && (createdNotBefore == null || s.CreatedAt >= createdNotBefore.Value.UtcDateTime))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DevicePublicKey, publicKeyJwk)
                                      .SetProperty(s => s.DeviceKeyAlgorithm, algorithm), ct) == 1;
}

builder.Services.AddScoped<IDeviceKeyStore, DeviceKeyStore>();
```

`DeviceKeyRegistrationWindow` (option của `AddTqkApiAuth`, mặc định 10 phút, null = lúc nào cũng được) giới hạn việc
đăng ký khoá trong khoảng thời gian đó kể từ lúc phiên được tạo, để bearer token bị lộ không gắn được một phiên chưa gắn
sang thiết bị của kẻ tấn công (làm user thật bị khoá ngoài ở mode `Reject` / `SignOut`). Lần thử muộn hơn trả
`AlreadyBound`. Với SQL Server ở isolation SNAPSHOT, một UPDATE đồng thời sẽ ném lỗi update-conflict thay vì trả 0 dòng;
READ COMMITTED / RCSI, PostgreSQL, MySQL và SQLite trả 0 dòng.

`AddTqkApiAuth` đăng ký `DeviceKeyRegistrar` (scoped, chỉ resolve trong request scope); nó chỉ resolve `IDeviceKeyStore`
lúc đăng ký khoá. `RegisterAsync(user, publicKeyJwk, algorithm)` trả về:

| Kết quả | Khi nào | HTTP gợi ý |
| --- | --- | --- |
| `Registered` | đã lưu khoá; cache của phiên bị invalidate nên request sau có `device_bound` | 200 |
| `AlreadyBound` | phiên đã có khoá, hoặc đã revoke / hết hạn / quá cửa sổ đăng ký (theo claim, hoặc store trả false) | 409 |
| `Invalid` | thuật toán không phải `ES256`/`RS256`, hoặc JWK không phải public key hợp lệ | 400 |
| `NoSession` | người gọi không được xác thực bằng scheme session token có claim `sid` | 401 |

Khoá được nhận: EC `P-256` (`x`/`y` mỗi cái 32 byte, điểm phải nằm trên đường cong) cho `ES256`; RSA có modulus 2048
tới 8192 bit và `e` = `AQAB` cho `RS256`. Bị từ chối: có thành phần private (`d`, `p`, `q`, `dp`, `dq`, `qi`, `oth`,
`k`), thành phần trùng tên, `alg` trong JWK khác thuật toán truyền vào, `use` khác `sig`, base64url không chuẩn, hoặc JWK
dài quá 4096 ký tự. Text được lưu chỉ giữ các thành phần public theo thứ tự cố định (`crv`, `kty`, `x`, `y` / `e`,
`kty`, `n`).

Registrar KHÔNG kiểm proof of possession: Web phải xác minh JWT đăng ký DBSC do khoá mới ký TRƯỚC khi gọi
`RegisterAsync`. Nó cũng nhận ra scheme session token qua `ClaimsIdentity.AuthenticationType`, nên app không bao giờ
được tự tạo identity mang tên scheme đó.

Endpoint của app (Web front end gọi một lần sau khi đăng nhập, kèm khoá nó vừa sinh). Endpoint phải yêu cầu rõ scheme
session token, để người gọi ẩn danh hoặc dùng scheme khác nhận 401:

```csharp
using TqkLibrary.Asp.ToolKit.Auth.Api.Enums;

app.MapPost("/auth/device-key", async (DeviceKeyRequest body, ClaimsPrincipal user, DeviceKeyRegistrar registrar, CancellationToken ct) =>
    await registrar.RegisterAsync(user, body.PublicKeyJwk, body.Algorithm, ct) switch
    {
        DeviceKeyRegistrationResult.Registered => Results.Ok(),
        DeviceKeyRegistrationResult.AlreadyBound => Results.Conflict(),
        DeviceKeyRegistrationResult.Invalid => Results.BadRequest(),
        _ => Results.Unauthorized(),
    }).RequireAuthorization(p => p.AddAuthenticationSchemes(SessionTokenDefaults.AuthenticationScheme).RequireAuthenticatedUser());

public sealed record DeviceKeyRequest(string PublicKeyJwk, string Algorithm);
```

Lưu ý:

- Nhiều instance: sau `Registered`, `InvalidateSession` chỉ tác động tới instance cục bộ. Các instance khác vẫn giữ bản
  ghi trong cache không có claim `device_bound` cho tới khi hết `SessionAccessCacheLifetime`; Web tự coi `Registered`
  là đã gắn ở phía nó (trong bộ nhớ) và hỏi lại `me` sau. Nếu app có lúc nào xoá khoá thiết bị thì phải gọi `InvalidateSession`, vì đường tắt
  `AlreadyBound` tin vào claim.
- `DeviceBindingMode` là text tự do và API KHÔNG validate: Web phải whitelist các mode đã biết và quay về mặc định của nó
  nếu giá trị lạ.

Endpoint `me` của app phải trả `isDeviceBound`, `deviceKeyJwk`, `deviceKeyAlgorithm` và `deviceBindingMode`, để Web
front end biết phiên đã gắn chưa, dùng khoá nào để kiểm bằng chứng của thiết bị, và user đã chọn mode nào.
`GetDeviceBinding` đọc chúng từ principal:

```csharp
using TqkLibrary.Asp.ToolKit.Auth.Api.Extensions;

app.MapGet("/auth/me", (ClaimsPrincipal user, IOptions<ApiAuthOptions> api) =>
{
    var binding = user.GetDeviceBinding(api.Value);            // null = chưa gắn
    return Results.Ok(new
    {
        id = user.FindFirstValue(ClaimTypes.NameIdentifier),
        userName = user.Identity!.Name,
        // ... email, roles, permissions
        isDeviceBound = binding is not null,
        deviceKeyJwk = binding?.Jwk,
        deviceKeyAlgorithm = binding?.Algorithm,
        deviceBindingMode = binding?.Mode,
    });
}).RequireAuthorization(p => p.AddAuthenticationSchemes(SessionTokenDefaults.AuthenticationScheme).RequireAuthenticatedUser());
```

 Trang cài đặt tài khoản đổi `DeviceBindingMode` phải gọi
`InvalidateUser(userId)` sau khi ghi.

### Job dọn dẹp

Phiên đã thu hồi và đã hết hạn nằm lại trong bảng tới khi app xoá. Chạy một job nền (vd mỗi ngày một lần) xoá các
dòng có `RevokedAt` hoặc `ExpiresAt` cũ hơn một khoảng lưu giữ (vd 30 ngày, để còn dấu vết kiểm tra), xoá theo lô
(`DELETE TOP (1000) ... WHERE ...` trong vòng lặp, hoặc `ExecuteDeleteAsync` trên truy vấn `Take(1000)`) để không
khoá bảng lâu.

## Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework; không phụ thuộc NuGet nào khác. Phía Web front end tương ứng là [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
