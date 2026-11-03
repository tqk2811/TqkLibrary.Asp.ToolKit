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
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, string userId, CancellationToken ct)
    {
        // Trả về null khi session đã bị thu hồi/hết hạn, thuộc user khác, hoặc tài khoản không dùng được.
        ...
        return new SessionAccessRecord { UserId = userId, UserName = name, Roles = roles, Permissions = permissions };
    }
}
```

Đăng ký:

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.Issuer = "my-api";
    o.SigningKey = builder.Configuration["Tokens:Key"]!;    // >= 32 byte
    o.MaxSessionLifetime = TimeSpan.FromDays(90);          // tùy chọn, mặc định: không giới hạn
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer();
```

Cấp token và refresh (bên trong auth service của ứng dụng):

```csharp
// Đăng nhập
var refresh = RefreshTokenCodec.Create(sessionId);            // lưu refresh.SecretHash cùng với session
var expires = slidingPolicy.GetInitialExpiry(now);
var access = accessTokenFactory.Create(userId, sessionId, now);

// Refresh: trả lại chính refresh token cũ, chỉ có hạn của session được trượt (sliding)
if (!RefreshTokenCodec.TryParse(token, out var sid, out var secret) || !RefreshTokenCodec.Verify(secret, session.SecretHash)) return Unauthorized();
var next = slidingPolicy.GetNextExpiry(session.CreatedAt, now);   // null = đã chạm giới hạn tuyệt đối
```

Sau khi ghi dữ liệu, hãy invalidate `SessionAccessCache`: `InvalidateSession` (đăng xuất), `InvalidateUser` (đổi mật khẩu/role,
khóa tài khoản), `InvalidateAll` (permission của role thay đổi).

## Phụ thuộc

Target net10.0, dùng ASP.NET Core shared framework và phụ thuộc `Microsoft.AspNetCore.Authentication.JwtBearer`. Phía web front end tương ứng là [TqkLibrary.Asp.ToolKit.Auth.Web](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web).

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
