# TqkLibrary.Asp.ToolKit.Auth.Api

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Auth.Api/README.md) · **Tiếng Việt**

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Api
```

## Cách dùng

Phía JWT API của cùng một cơ chế: access token gọn (`sub`, `sid`, `jti`, thời hạn), kiểm tra session ở mọi request
với cache ngắn hạn, claim được thay bằng dữ liệu hiện tại, refresh token không xoay vòng dạng `<sessionId>.<secret>`.

Cài đặt `ISessionAccessSource`:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessResult> GetAsync(string sessionId, string userId, CancellationToken ct)
    {
        // Reject (có thể kèm lý do) khi session đã bị thu hồi/hết hạn, thuộc user khác, hoặc tài khoản không dùng
        // được. Kết quả reject không bao giờ được cache.
        if (session is null || session.RevokedAt is not null) return SessionAccessResult.Reject(SessionRejectionReasons.Revoked);
        if (user.IsLocked) return SessionAccessResult.Reject(SessionRejectionReasons.UserLocked);
        ...
        // SessionAccessRecord tự chuyển ngầm thành SessionAccessResult.Grant(...).
        return new SessionAccessRecord { UserId = userId, UserName = name, Roles = roles, Permissions = permissions };
    }
}
```

Nếu `ISessionAccessSource.GetAsync` ném exception thì không có rejection nào được ghi (`SessionTokenValidator.GetSessionRejection`
trả null) và request lỗi bằng chính exception đó chứ không trả 401.

Đăng ký:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.Issuer = "my-api";
    o.SigningKey = builder.Configuration["Tokens:Key"]!;    // >= 32 byte
    // Session thường 7 ngày, session "ghi nhớ đăng nhập" 30 ngày (Persistent null = bằng Normal). Mặc định: 30 ngày.
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);          // tùy chọn, giới hạn tuyệt đối cho cả hai loại; mặc định: không giới hạn
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer(jwt =>
{
    // Tùy chọn: tự viết body 401 kèm lý do reject do ISessionAccessSource trả về.
    jwt.Events.OnChallenge = async context =>
    {
        if (SessionTokenValidator.GetSessionRejection(context.HttpContext) is not { } rejection) return;
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "session_rejected", reason = rejection.Reason });
    };
});
```

`SessionTokenValidator.GetSessionRejection(HttpContext)` trả về `SessionRejection(Reason, Message)` (lưu dạng một
feature trong `HttpContext.Features`) hoặc null; `IsSessionRejected` vẫn giữ làm lối tắt. Thư viện tự gán lý do
`no_session` (token không có session/user id) và `user_mismatch` (record của user khác).

Cấp token và refresh (bên trong auth service của ứng dụng):

```csharp
// Đăng nhập
var refresh = RefreshTokenCodec.Create(sessionId);            // lưu refresh.SecretHash cùng với session
var expires = slidingPolicy.GetInitialExpiry(now, persistent: model.RememberMe);   // lưu cờ "persistent" cùng session
var access = accessTokenFactory.Create(userId, sessionId, now);

// Refresh: trả lại chính refresh token cũ, chỉ có hạn của session được trượt (sliding)
if (!RefreshTokenCodec.TryParse(token, out var sid, out var secret) || !RefreshTokenCodec.Verify(secret, session.SecretHash)) return Unauthorized();
var next = slidingPolicy.GetNextExpiry(session.CreatedAt, now, session.IsPersistent);   // null = đã chạm giới hạn tuyệt đối
```

`SlidingSessionPolicy` không `sealed`, hai hàm đều `virtual`. `AddTqkApiAuth` đăng ký nó bằng `TryAddSingleton`, nên
lớp con đăng ký TRƯỚC sẽ được dùng:

```csharp
builder.Services.AddSingleton<SlidingSessionPolicy, MySlidingSessionPolicy>();
builder.Services.AddTqkApiAuth(o => { ... });
```

Sau khi ghi dữ liệu, hãy invalidate `SessionAccessCache`: `InvalidateSession` (đăng xuất; chỉ session đó), `InvalidateUser`
(đổi mật khẩu/role, khóa tài khoản; chỉ các session của user đó), `InvalidateAll` (permission của role thay đổi). Lượt tra
cứu đang chạy dở lúc invalidate sẽ không bao giờ được dùng lại sau đó.

## Phụ thuộc

Target net10.0, dùng ASP.NET Core shared framework và phụ thuộc `Microsoft.AspNetCore.Authentication.JwtBearer`. Phía web front end tương ứng là [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
