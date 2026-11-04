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
hash secret, thời điểm tạo, lần truy cập cuối, hạn, cờ persistent, cờ thu hồi. Dữ liệu thêm theo phiên về sau (vd
public key gắn thiết bị) do app tự thêm: định dạng token không đổi.

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
luôn trả `invalid_token`, nên endpoint không dùng để dò phiên hay user được. Kết quả "không có phiên" cũng được cache
như một record; gọi `InvalidateSession(sessionId)` sau khi tạo phiên nếu id đó có thể vừa bị dò.

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
    // Tuỳ chọn: body 401 riêng kèm lý do từ chối.
    o.OnChallenge = async context =>
    {
        if (context.Rejection is not { } rejection) return;     // không có token: giữ 401 mặc định
        context.HandleResponse();
        context.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
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
   `ClaimTypes.Name`, `ClaimTypes.Email`, `ClaimTypes.Role`, `PermissionClaimType` (`permission`).

Request không có header bearer thì không có kết quả (endpoint ẩn danh vẫn chạy, `[Authorize]` trả 401). Lý do từ chối
(`SessionRejection(Reason, Message)`) đọc được trong `OnChallenge` và qua `HttpContext.GetSessionRejection()`. Token
không bao giờ bị ghi log.

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
- `InvalidateUser(userId)`: đổi mật khẩu, khoá/xoá tài khoản, đổi role hoặc tên/email;
- `InvalidateAll()`: permission của role đổi, role bị đổi tên/xoá.

Lượt tra cứu đang chạy dở lúc invalidate không được dùng lại. Nhiều instance API thì invalidate chỉ tới instance xử
lý lần ghi; các instance khác trễ tối đa thời gian cache.

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
