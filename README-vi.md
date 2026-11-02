# TqkLibrary.Asp.ToolKit — hướng dẫn sử dụng (tiếng Việt)

Bản tiếng Anh ngắn gọn: [README.md](README.md). Tài liệu này giải thích đầy đủ cách dùng ba package ASP.NET Core:
`TqkLibrary.Asp.ToolKit.Caching`, `TqkLibrary.Asp.ToolKit.Auth.Web`, `TqkLibrary.Asp.ToolKit.Auth.Api`.

## Mục lục

- [Cài đặt và chọn package](#cài-đặt-và-chọn-package)
- [Cơ chế tổng quan của Auth](#cơ-chế-tổng-quan-của-auth)
- [Auth.Web — host Web (MVC/Razor)](#authweb--host-web-mvcrazor)
- [Auth.Api — host API (JWT)](#authapi--host-api-jwt)
- [Điều kiện bắt buộc phía API](#điều-kiện-bắt-buộc-phía-api)
- [Caching](#caching)
- [Queue](#queue)
- [Các bẫy thường gặp](#các-bẫy-thường-gặp)

## Cài đặt và chọn package

```
dotnet add package TqkLibrary.Asp.ToolKit.Caching
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Web
dotnet add package TqkLibrary.Asp.ToolKit.Auth.Api
dotnet add package TqkLibrary.Asp.ToolKit.Queue
```

| Package | Cài vào host nào | Dùng để làm gì |
|---|---|---|
| `TqkLibrary.Asp.ToolKit.Auth.Web` | Web front end (MVC/Razor) đăng nhập qua một API riêng | Giữ access/refresh token của API trong cookie đã mã hoá, tự gắn bearer, tự refresh, dựng lại user hiện tại cho UI |
| `TqkLibrary.Asp.ToolKit.Auth.Api` | API phát hành JWT | Phát access token gọn, refresh token không xoay vòng, kiểm phiên mỗi request (có cache ngắn) |
| `TqkLibrary.Asp.ToolKit.Caching` | Web hoặc API (hoặc cả hai) | Cache `stale-while-refresh` theo nhóm (group), vô hiệu hoá theo group, kênh vô hiệu hoá giữa hai process qua HTTP |
| `TqkLibrary.Asp.ToolKit.Queue` | Bất kỳ host nào cần xếp hàng việc nền | Hàng đợi điều phối trong process: chính sách cho chạy (trần song song), chia lượt công bằng theo khoá, vé await kết quả |

Web và API là hai app tách biệt: Web cài `Auth.Web`, API cài `Auth.Api`. `Caching` và `Queue` độc lập với các package còn lại.

## Cơ chế tổng quan của Auth

- **Web** không có database người dùng. Sau khi đăng nhập qua API, access token + refresh token (và hạn của chúng) được
  nhét vào ticket của auth cookie. Ticket được Data Protection mã hoá, cookie là `HttpOnly`, nên trình duyệt không đọc được token.
  Server không lưu gì theo từng phiên.
- `HttpContext.User` ở Web chỉ mang **một claim duy nhất: mã phiên của API** (`sid`). Tên, role, permission được lấy
  từ API `me` mỗi request (có cache ngắn, mặc định 10 giây) rồi **thay** `HttpContext.User`, nên đổi quyền ở API có hiệu lực
  trên UI sau tối đa chừng đó giây.
- `SessionBearerHandler` (một `DelegatingHandler`) gắn bearer vào mọi lời gọi API: token sắp hết hạn thì refresh trước khi gửi,
  API trả 401 thì refresh đúng một lần rồi gửi lại, vẫn 401 thì coi như phiên chết và ném `SessionExpiredException`.
  `SessionExpiredMiddleware` bắt exception đó, xoá cookie và chuyển về trang đăng nhập (AJAX nhận 401 JSON).
- **API không xoay vòng (rotate) refresh token**: cùng một refresh token dùng lại được nhiều lần, mỗi lần refresh chỉ
  làm trượt (sliding) hạn của phiên. Nhờ vậy nhiều request/nhiều instance Web refresh song song mà không giẫm chân nhau.
- API kiểm phiên **ở mỗi request**: `SessionTokenValidator` gọi `ISessionAccessSource` (qua `SessionAccessCache`, mặc định 30 giây),
  rồi thay claim trong token bằng dữ liệu hiện tại (tên, email, role, permission). Phiên bị thu hồi thì request nhận 401.

## Auth.Web — host Web (MVC/Razor)

### Các interface app phải cài

| Interface | Ai gọi | Việc phải làm |
|---|---|---|
| `IAuthApiClient` (`Auth.Web`) | Thư viện, khi refresh token và khi dựng user hiện tại | Gọi API `refresh`, `me`, `logout`. API từ chối `refresh`/`me` bằng 401/403 thì **phải** ném `SessionExpiredException`; mọi exception khác coi là lỗi tạm và **không** đăng xuất |
| `ISessionAccessSource` (`Auth.Api`) | Thư viện, ở host API | Xem [Auth.Api](#authapi--host-api-jwt) |

Ví dụ cài `IAuthApiClient` tối thiểu bằng `HttpClient`, dùng helper `ThrowIfSessionRejected`
(401/403 thì ném `SessionExpiredException`, status khác không làm gì):

```csharp
using TqkLibrary.Asp.ToolKit.Auth.Web;
using TqkLibrary.Asp.ToolKit.Auth.Web.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

public sealed class MyAuthApiClient(HttpClient http) : IAuthApiClient
{
    public async Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("auth/refresh", new { refreshToken }, ct);
        response.ThrowIfSessionRejected();       // 401/403 -> SessionExpiredException -> đăng xuất
        response.EnsureSuccessStatusCode();      // 5xx, ... -> lỗi tạm, giữ cookie
        var dto = await response.Content.ReadFromJsonAsync<MyTokenDto>(ct);
        return new AuthTokenPair
        {
            SessionId = dto!.SessionId,
            AccessToken = dto.AccessToken,
            AccessTokenExpiresAt = dto.AccessTokenExpiresAt,     // UTC
            RefreshToken = dto.RefreshToken,
            RefreshTokenExpiresAt = dto.RefreshTokenExpiresAt,   // UTC
        };
    }

    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("auth/me", ct);
        response.ThrowIfSessionRejected();
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<MyMeDto>(ct);
        return new CurrentUserInfo { Id = dto!.Id, UserName = dto.UserName, Roles = dto.Roles, Permissions = dto.Permissions };
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("auth/logout", content: null, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

Lưu ý:

- `RefreshAsync` được thư viện chạy trong `AnonymousApiScope`, nên `HttpClient` có gắn `SessionBearerHandler` cũng không bị đệ quy.
- Ngược lại, **đừng** biến lỗi tạm (5xx, timeout, mất mạng) thành `SessionExpiredException` — người dùng sẽ bị đăng xuất oan.
- `SessionBearerHandler` cũng tự ném `SessionExpiredException` khi API trả 401 sau khi đã refresh.

### Đăng ký trong `Program.cs` (Web)

```csharp
builder.Services.AddTqkDataProtectionKeyRing(builder.Environment);   // giữ phiên qua lần restart; nhiều instance dùng chung thư mục

builder.Services.AddTqkWebAuth(
    cookie =>
    {
        cookie.Cookie.Name = "app_auth";
        cookie.LoginPath = "/account/login";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
    },
    options =>
    {
        options.AnonymousPathSuffixes.Add("/auth/login");      // endpoint API ẩn danh: không gắn bearer
        options.AnonymousPathSuffixes.Add("/auth/refresh");
    });

builder.Services
    .AddHttpClient<IAuthApiClient, MyAuthApiClient>(c => c.BaseAddress = new Uri(apiUrl))
    .AddTqkSessionBearer()          // gắn bearer + refresh
    .AddTqkClientIpForwarding();    // tuỳ chọn: chuyển IP người xem sang API qua X-Forwarded-For

var app = builder.Build();
app.UseAuthentication();
app.UseTqkWebSession();             // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();
```

Đăng nhập / đăng xuất (token lấy từ API login):

```csharp
await sessionManager.SignInAsync(HttpContext, tokens, persistent: model.RememberMe);   // WebSessionManager
await sessionManager.SignOutAsync(HttpContext);                                        // gọi API logout rồi xoá cookie
```

Loader của cache dùng chung cho mọi người xem phải chạy ẩn danh để không dính token của người đang xem:
`AnonymousApiScope.RunAsync(() => api.GetNewsAsync(ct))`.

Về cookie:

- Thư viện đặt sẵn `HttpOnly`, `SameSite=Lax` và **`SlidingExpiration = false`**: ticket đã tự trượt theo hạn refresh token mỗi khi
  làm mới token (không bao giờ vượt hạn refresh token); sliding của cookie handler có thể kéo hạn vượt hạn refresh. Vẫn ghi đè được trong delegate `cookie => ...`.
- Ngoài môi trường Development, cookie chỉ đi qua HTTPS (`SecurePolicy = Always`).
- `WebAuthOptions.Scheme` **chỉ** đặt được qua tham số `configure` của `AddTqkWebAuth` (cần biết lúc đăng ký). Delegate `configure` được gọi **hai lần**
  (một lần trên bản snapshot lúc đăng ký, một lần qua options), nên không được có side-effect ngoài đối tượng options.

### Options của Web (`WebAuthOptions`)

| Option | Mặc định | Ý nghĩa |
|---|---|---|
| `Scheme` | `Cookies` | Tên scheme cookie |
| `SessionIdClaimType` | `tqk_sid` | Claim duy nhất trong cookie: mã phiên API |
| `PermissionClaimType` | `permission` | Claim permission trên principal dựng từ `me` |
| `AccessTokenName` / `AccessTokenExpiresName` / `RefreshTokenName` / `RefreshTokenExpiresName` | `api_access_token` / `api_access_expires` / `api_refresh_token` / `api_refresh_expires` | Tên các mục token trong ticket |
| `RefreshSkew` | 30 giây | Token hết hạn trong khoảng này thì refresh trước khi gửi |
| `RefreshTimeout` | 30 giây | Timeout riêng của một lần refresh (không dùng token huỷ của request) |
| `CurrentUserTimeout` | 15 giây | Hạn chờ lấy user hiện tại (`me`); phải đủ cho refresh + API khởi động nguội |
| `CurrentUserCacheLifetime` | 10 giây | Cache `me` theo phiên |
| `AnonymousPathSuffixes` | rỗng | Đuôi đường dẫn API ẩn danh (login, refresh, reset mật khẩu...) |
| `SessionExpiredMessage` | "Your session has expired. Please sign in again." | Thông báo trả cho AJAX / trong exception |

### Hành vi cần biết

- Refresh **chủ động** (token sắp hết hạn) gặp lỗi tạm (5xx, mạng) mà access token hiện tại **còn hạn** thì ghi warning và gửi tiếp bằng token cũ,
  không ném lỗi. Refresh **sau 401** hoặc khi token đã hết hạn thì lỗi tạm vẫn được ném ra, cookie vẫn giữ.
- `me` lỗi tạm (mạng, 5xx, quá `CurrentUserTimeout`) → người dùng vẫn đăng nhập (chỉ có mã phiên) nhưng **không có permission nào**: trang admin trả "forbidden"
  thay vì mở ra bằng dữ liệu cũ.
- Body upload (multipart/stream) không được buffer nên không gửi lại được: 401 với body kiểu này coi như phiên hết hạn.
- Một khi response đã bắt đầu gửi thì không ghi được header nữa: không xoá được cookie, cookie mới cũng không ghi được. Lần refresh ở request sau sẽ sửa lại.

## Auth.Api — host API (JWT)

### Interface app phải cài: `ISessionAccessSource`

Chạy (qua `SessionAccessCache`) cho **mỗi request có xác thực**. Trả `null` khi phiên bị thu hồi, hết hạn, thuộc user khác hoặc tài khoản không dùng được:

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, string userId, CancellationToken ct)
    {
        // null khi phiên đã bị thu hồi / hết hạn / không thuộc userId / tài khoản bị khoá
        ...
        return new SessionAccessRecord { UserId = userId, UserName = name, Roles = roles, Permissions = permissions };
    }
}
```

`SessionAccessRecord` được cache và dùng chung giữa các request: không sửa nó.

### Đăng ký trong `Program.cs` (API)

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.Issuer = "my-api";
    o.SigningKey = builder.Configuration["Tokens:Key"]!;       // tối thiểu 32 byte (UTF-8)
    o.MaxSessionLifetime = TimeSpan.FromDays(90);              // tuỳ chọn; mặc định không giới hạn
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer();
```

Phát hành và refresh (trong auth service của app):

```csharp
// Đăng nhập
var refresh = RefreshTokenCodec.Create(sessionId);              // lưu refresh.SecretHash cùng phiên
var expires = slidingPolicy.GetInitialExpiry(now);              // hạn phiên
var access = accessTokenFactory.Create(userId, sessionId, now);

// Refresh: trả lại CÙNG refresh token, chỉ trượt hạn phiên
if (!RefreshTokenCodec.TryParse(token, out var sid, out var secret)
    || !RefreshTokenCodec.Verify(secret, session.SecretHash)) return Unauthorized();
var next = slidingPolicy.GetNextExpiry(session.CreatedAt, now);  // null = đã chạm trần MaxSessionLifetime
```

Refresh token có dạng `<sessionId>.<secret>`; chỉ lưu hash của `secret`. Trả 401 cho cả refresh sai lẫn phiên đã chết,
để phía Web đăng xuất đúng (xem `ThrowIfSessionRejected`).

### Options của API (`ApiAuthOptions`)

| Option | Mặc định | Ý nghĩa |
|---|---|---|
| `Issuer` | bắt buộc | Issuer của token |
| `Audience` | `Issuer` | Audience (rỗng = như issuer) |
| `SigningKey` | bắt buộc | Khoá HMAC-SHA256, tối thiểu 32 byte |
| `UserIdClaimType` / `SessionIdClaimType` / `PermissionClaimType` | `sub` / `sid` / `permission` | Loại claim |
| `AccessTokenLifetime` | 15 phút | Hạn access token |
| `RefreshTokenLifetime` | 30 ngày | Hạn trượt của phiên / refresh token |
| `MaxSessionLifetime` | không | Trần tuyệt đối tính từ lúc đăng nhập |
| `ClockSkew` | 0 | Độ lệch đồng hồ cho phép |
| `SessionAccessCacheLifetime` | 30 giây | Cache kết quả `ISessionAccessSource` theo phiên |

Options được kiểm khi khởi động (`ValidateOnStart`).

### Lưu ý khi cấu hình JwtBearer

- `AddTqkJwtBearer(configure)` chạy `configure` **sau** khi thư viện đã đặt `TokenValidationParameters`. App gán lại
  `TokenValidationParameters = new(...)` sẽ **ghi đè** cấu hình thư viện (khoá ký, issuer, audience...). Hãy chỉnh trên instance có sẵn.
- **Không dùng `JwtBearerOptions.EventsType`.** Khi có `EventsType`, handler lấy events từ container và bỏ qua `JwtBearerOptions.Events` — nơi thư viện gắn bước kiểm phiên,
  nên phiên bị thu hồi vẫn được chấp nhận. Thư viện chặn tình huống này: options ném `InvalidOperationException` ở lần dùng đầu tiên.
  Cần events riêng thì gọi `SessionTokenValidator.ValidateAsync(context)` từ `OnTokenValidated` của events type đó (sau các kiểm tra của app).

## Điều kiện bắt buộc phía API

Web và API chỉ chạy đúng khi API tuân thủ các điều kiện sau:

1. **Refresh token không dùng một lần.** Không xoay vòng, không vô hiệu hoá refresh token cũ khi refresh. Nhiều request/instance Web có thể refresh
   cùng lúc bằng cùng một refresh token; xoay vòng sẽ làm các request thua cuộc bị đăng xuất.
2. **Refresh chỉ trượt hạn phiên** (`SlidingSessionPolicy.GetNextExpiry`), trả lại cùng refresh token, và trả access token mới.
3. **Endpoint refresh và `me` trả 401/403 khi phiên đã chết** (hết hạn, thu hồi, tài khoản bị khoá). Lỗi 5xx chỉ dùng cho lỗi tạm.
4. **Thu hồi / đổi quyền phải gọi invalidate `SessionAccessCache` ngay sau khi ghi thành công:**
   - `InvalidateSession(sessionId)` khi đăng xuất / thu hồi một phiên;
   - `InvalidateUser(userId)` khi đổi mật khẩu, khoá/xoá tài khoản, đổi role hoặc tên/email;
   - `InvalidateAll()` khi permission của role đổi, role bị đổi tên/xoá.

   Hiện tại cả ba hàm đều xoá **toàn bộ** entry trong cache (một bộ đếm generation duy nhất); ba tên được giữ riêng để nói rõ ý định ở nơi gọi.
   Không gọi invalidate thì phiên bị thu hồi vẫn hợp lệ tới hết `SessionAccessCacheLifetime`.
5. **Nhiều instance API:** cache là theo process, invalidate chỉ tới instance xử lý lần ghi; các instance khác trễ tối đa `SessionAccessCacheLifetime`
   (hoặc dùng thêm kênh chung như Redis pub/sub, hoặc kênh HTTP của `Caching` nếu phù hợp).

## Caching

Cache trong process cho dữ liệu dùng chung, đọc nhiều, vô hiệu hoá theo nhóm (group) mà app đặt tên bằng enum riêng.

### `StaleWhileRefreshCache<T>`

- Một giá trị + hạn (TTL). Khi hết hạn, **đúng một** caller làm mới; các caller khác nhận ngay giá trị cũ (không xếp hàng). Chỉ lần nạp đầu và lần nạp sau `Invalidate` mới phải chờ.
- Làm mới lỗi mà còn giá trị cũ → ghi warning, trả giá trị cũ và kéo dài thêm `min(ttl, maxStaleBackoff)`. Chưa có giá trị cũ → ném lỗi và nhớ lỗi trong `failureHoldFor`
  để các caller đang xếp hàng nhận cùng lỗi thay vì mỗi người nạp lại. Mặc định `StaleWhileRefreshCache<T>.FailureHoldFor` = 2 giây và `MaxStaleBackoff` = 10 giây;
  chỉnh được bằng tham số tuỳ chọn `failureHoldFor` / `maxStaleBackoff` của constructor.
- Hàm `load` **phải đọc dữ liệu bên trong lambda** (không capture giá trị đã tính sẵn), nếu không `Invalidate` không an toàn (lần nạp sau ghi lại dữ liệu cũ).
- Hàm `load` **phải tự có timeout.** Lần nạp đầu (và lần nạp sau `Invalidate`) giữ lock và chỉ nhìn token của caller; thư viện không đặt timeout nội bộ,
  nên `load` treo vĩnh viễn sẽ chặn mọi caller chưa có giá trị cũ.
- Giá trị được chia sẻ cho mọi caller: không sửa nó.

### `KeyedStaleWhileRefreshCache<TKey, T>`

Mỗi key một cache con (hai key không chờ nhau). `maxEntries` là giới hạn **xấp xỉ**: dưới tải song song số key có thể vượt nhẹ vì kiểm tra kích thước và chèn không atomic;
mỗi lần miss một key mới khi cache đầy sẽ quét toàn bộ key để loại key ít dùng nhất (O(n)), nên giữ `maxEntries` vừa phải.

### `CacheRegistry<TGroup>` và vô hiệu hoá theo group

```csharp
public enum CacheGroup { News, Settings }

builder.Services.AddTqkCaching<CacheGroup>();

public sealed class NewsCache
{
    public NewsCache(CacheRegistry<CacheGroup> registry)
    {
        // Tạo cache MỘT LẦN (singleton), không tạo theo request: registry giữ tham chiếu mãi mãi.
        Latest = registry.CreateKeyed<int, IReadOnlyList<NewsItem>>(TimeSpan.FromSeconds(60), 8, CacheGroup.News);
    }
    public KeyedStaleWhileRefreshCache<int, IReadOnlyList<NewsItem>> Latest { get; }
}

var items = await newsCache.Latest.GetAsync(10, ct => repository.GetLatestAsync(10, ct));

// Sau khi lần ghi thành công:
invalidator.Invalidate(CacheGroup.News);       // ICacheInvalidator<CacheGroup>
```

- Một cache có thể gắn nhiều group (`Create<T>(ttl, groupA, groupB)`): invalidate **bất kỳ** group nào cũng làm nó stale.
- `CacheInvalidator<TGroup>` invalidate local trước rồi báo cho mọi `ICacheInvalidationNotifier<TGroup>`; notifier lỗi chỉ ghi warning, không làm hỏng lần ghi đã thành công.

### Kênh vô hiệu hoá qua HTTP giữa hai process (ví dụ API -> Web)

```csharp
// Bên gửi (process ghi dữ liệu)
builder.Services.AddTqkCacheInvalidationSender<CacheGroup>(o =>
{
    o.TargetUrl = "http://localhost:5000/_internal/cache/invalidate";
    o.Key = builder.Configuration["Caching:InternalKey"]!;
});

// Bên nhận (process giữ cache); chỉ loopback, có khoá, còn lại trả 404
builder.Services.AddTqkCacheInvalidationEndpoint<CacheGroup>(o => o.Key = builder.Configuration["Caching:InternalKey"]);
app.UseTqkCacheInvalidationEndpoint<CacheGroup>();   // đặt TRƯỚC UseForwardedHeaders / UseHttpsRedirection / status code pages
```

Bên gửi (`HttpCacheInvalidationSender<TGroup>`, một `BackgroundService`):

- Gom các group trong cửa sổ `Debounce` (200 ms) thành một lần gọi `POST {"groups":["News"]}` kèm header khoá (`X-Cache-Key`).
- Lỗi tạm thì thử lại theo `RetryDelays` (mặc định 0,5 s / 1 s / 2 s); 401/403/404 là cấu hình sai, không retry và ghi error.
- `TargetUrl` không phải URL tuyệt đối http/https → ghi error lúc khởi động và **không chạy vòng gửi** (khỏi retry vô ích).
- Options được đọc qua `IOptionsMonitor` mỗi lô, nên đổi URL/khoá/retry không cần restart. Việc bật/tắt (thiếu `TargetUrl` hoặc `Key`) và tính hợp lệ của URL chỉ xét lúc khởi động.
- Hàng đợi có sức chứa `QueueCapacity` (256); đầy thì bỏ group cũ nhất (bên nhận vẫn hết hạn theo TTL).
- **Lô đang chờ debounce hoặc retry sẽ bị bỏ khi app tắt**; bên nhận chỉ còn dựa vào TTL.

Bên nhận (`CacheInvalidationEndpoint<TGroup>`):

- Chỉ nhận khi **đồng thời**: đã cấu hình `Key`; kết nối TCP thật là loopback và **không có** header proxy (`ForwardingHeaders`); header khoá khớp (`RequireLoopback` mặc định `true`). Còn lại trả 404 như thể endpoint không tồn tại.
- Gọi thẳng `CacheRegistry` (không qua `ICacheInvalidator`) nên hai process không phát lại tín hiệu cho nhau.
- Body tối đa `MaxBodyBytes` (4096); group chỉ nhận theo **tên** enum, số hoặc tên lạ → 400.

Options của kênh:

| Option | Mặc định |
|---|---|
| `CacheInvalidationSenderOptions.TargetUrl` / `Key` | rỗng (rỗng = tắt) |
| `KeyHeader` | `X-Cache-Key` |
| `Debounce` | 200 ms |
| `Timeout` (mỗi lần gọi HTTP) | 5 giây |
| `RetryDelays` | `null` = `DefaultRetryDelays` (0,5 s, 1 s, 2 s); mảng rỗng đặt bằng code = không retry |
| `QueueCapacity` | 256 |
| `CacheInvalidationEndpointOptions.Path` | `/_internal/cache/invalidate` |
| `Key` | `null` (tắt endpoint) |
| `RequireLoopback` | `true` |
| `MaxBodyBytes` | 4096 |
| `ForwardingHeaders` | `null` = `DefaultForwardingHeaders` (`X-Forwarded-For`, `Forwarded`, `X-Real-IP`, `CF-Connecting-IP`, `X-Original-For`) |

Về `RetryDelays` và `ForwardingHeaders`: binder cấu hình (`IConfiguration`) **nối thêm** phần tử vào mọi collection/mảng đã có sẵn giá trị, nên hai option này mặc định là `null`
và giá trị mặc định nằm ở property tĩnh riêng (`Default*`, đọc giá trị hiệu lực qua `Effective*`). Cấu hình danh sách trong `appsettings` sẽ **thay thế** mặc định. Muốn tắt retry
phải đặt mảng rỗng bằng code (`o.RetryDelays = [];`) vì binder bỏ qua section rỗng.

## Queue

Hàng đợi điều phối trong process, không phụ thuộc ASP.NET Core. Việc được nộp không chặn; chỉ bắt đầu chạy khi chính sách cho phép (`IAdmissionPolicy<TItem>`, vd trần song song).
Các việc đang chờ được xét xoay vòng theo khoá công bằng, mỗi lần nộp trả về một vé (`DispatchTicket<TResult>`) để await kết quả.

```csharp
var policy = new ConcurrencyCapPolicy<Job>(job => [new CapRule("global", 3), new CapRule(("customer", job.CustomerId), 1)]);

await using var queue = new DispatchQueue<Job>(new DispatchQueueOptions<Job>
{
    Policy = policy,
    FairnessKey = job => job.CustomerId,   // các khách hàng được xét lần lượt, không ai chiếm hết lượt
    MaxPendingPerKey = 100,                // một khoá chờ quá 100 việc thì việc mới bị DispatchQueueFullException
});

var ticket = queue.Submit(job, async (item, ct) => await RunAsync(item, ct), isPriority: false, cancellationToken);
var result = await ticket.Completion;      // ticket.QueuePosition cho biết vị trí đang chờ
```

- `ConcurrencyCapPolicy<TItem>`: nhiều trần chồng nhau (`CapRule`), việc chỉ chạy khi lọt tất cả trần; `CompositeAdmissionPolicy<TItem>` ghép nhiều chính sách.
- `RateLimitPolicy<TItem>`: giãn nhịp BẮT ĐẦU việc (không giới hạn số việc chạy cùng lúc), dùng chung cho mọi việc. Ghép với `ConcurrencyCapPolicy` qua `CompositeAdmissionPolicy`; việc bị chặn nhận `Delay` nên hàng đợi tự xét lại đúng lúc, không cần `Wake()`:

  ```csharp
  // "Mỗi thư cách nhau 2 phút" và "tối đa 20 việc / 2 phút", kèm trần song song như thường.
  var policy = new CompositeAdmissionPolicy<Job>(
      new RateLimitPolicy<Job>(new RateLimitOptions
      {
          MinInterval = TimeSpan.FromMinutes(2),   // khoảng cách tối thiểu giữa hai lần bắt đầu (Zero = tắt)
          MaxPerWindow = 20,                       // cửa sổ trượt (null = tắt)
          Window = TimeSpan.FromMinutes(2),
          PriorityBypass = true,                   // việc ưu tiên luôn chạy nhưng vẫn được tính vào giới hạn
      }),
      new ConcurrencyCapPolicy<Job>(_ => [new CapRule("global", 3)]));
  ```
- `isPriority: true` dùng trần chờ riêng (`MaxPriorityPendingPerKey`) để hàng thường đầy không chặn việc ưu tiên.
- Huỷ bằng `cancellationToken` được cả lúc còn chờ (gỡ khỏi hàng) lẫn lúc đang chạy.
- Khi điều kiện của chính sách đổi từ bên ngoài (đổi cấu hình trần) phải gọi `queue.Wake()`, nếu không việc đang chờ chỉ được xét lại khi có việc mới hoặc việc khác xong.
- `OnInternalError` nhận lỗi ném từ chính sách hoặc vòng điều phối (đã được nuốt để hàng đợi không chết): gắn logger vào đó.
- Thư viện kéo theo gói `Nito.AsyncEx.Context`.

## Các bẫy thường gặp

| Bẫy | Hậu quả | Cách xử lý |
|---|---|---|
| Hạn chờ `me` (`CurrentUserTimeout`) quá ngắn | Request ngẫu nhiên chạy không có permission khi API khởi động nguội hoặc đang refresh token | Giữ >= 15 giây; phải đủ cho một lần refresh + cold start |
| IIS cho app "ngủ" (idle timeout, recycle) | Cache và hàng đợi gửi vô hiệu hoá mất; lô đang chờ bị bỏ; lần đầu sau khi ngủ rất chậm | Đặt Start Mode `AlwaysRunning`, tắt idle timeout / bật preload cho app pool của cả Web lẫn API |
| Dùng `JwtBearerOptions.EventsType` | Bước kiểm phiên bị bỏ qua | Thư viện ném `InvalidOperationException`; gọi `SessionTokenValidator.ValidateAsync` từ events type của app |
| App gán lại `TokenValidationParameters` | Mất khoá ký/issuer/audience của thư viện | Chỉnh instance có sẵn, đừng `new` |
| Token (hoặc ticket) to | Cookie bị chia chunk, có thể vượt giới hạn header của proxy/IIS/nginx (lỗi 400/431) | Giữ access token gọn (chỉ `sub`, `sid`, `jti`), không nhét claim/permission vào token; nới giới hạn header của proxy nếu cần |
| Nhiều instance Web | Cookie giải mã được ở instance này mà không giải mã được ở instance kia | Dùng chung key ring Data Protection và cùng `ApplicationName` (`AddTqkDataProtectionKeyRing`); mất key ring = mọi người phải đăng nhập lại |
| Nhiều instance API | Invalidate `SessionAccessCache` chỉ tới instance xử lý lần ghi | Chấp nhận trễ tới `SessionAccessCacheLifetime`, hoặc thêm kênh chung |
| Nhiều instance Web + cache | Một instance bị invalidate, instance khác thì không | Gửi tín hiệu tới từng instance qua kênh HTTP (mỗi instance có endpoint riêng) hoặc chấp nhận chờ TTL |
| Refresh token bị API xoay vòng một lần dùng | Request song song bị đăng xuất | Xem [Điều kiện bắt buộc phía API](#điều-kiện-bắt-buộc-phía-api) |
| `IAuthApiClient` biến lỗi tạm thành `SessionExpiredException` (hoặc nuốt 401/403) | Đăng xuất oan, hoặc phiên chết mà không đăng xuất | Chỉ 401/403 -> `SessionExpiredException` (dùng `ThrowIfSessionRejected`), lỗi khác để nguyên |
| Hàm `load` của cache không có timeout | Lần nạp đầu treo sẽ chặn mọi caller chưa có giá trị cũ | Tự đặt timeout (ví dụ `CancellationTokenSource.CancelAfter`) trong `load` |
| `load` capture giá trị đã tính | `Invalidate` không an toàn, dữ liệu cũ được ghi lại | Đọc dữ liệu bên trong lambda |
| Gọi `registry.Create`/`CreateKeyed` theo request | Rò rỉ bộ nhớ (registry giữ tham chiếu mãi) | Tạo một lần trong singleton; registry cảnh báo khi một group có hơn 100 cache |
| Đặt `UseTqkCacheInvalidationEndpoint` sau `UseForwardedHeaders` / HTTPS redirect | Địa chỉ nguồn bị đổi hoặc response bị viết lại, endpoint từ chối | Đặt endpoint trước các middleware đó |
| Gọi `AddTqkWebAuth` với `configure` có side-effect | Side-effect chạy hai lần | Delegate chỉ được đụng tới đối tượng options |

## Phát hành

Mỗi bản phát hành là một tag `vM.N.P` gắn trên commit của `master`; CI sẽ push toàn bộ package lên nuget.org.

```
git tag v1.2.3
git push origin v1.2.3      # kích hoạt .github/workflows/release.yml
```

Tag phải lớn hơn version cao nhất đang có trên nuget.org. Commit sau tag build ra `M.N.P-dev.<số commit kể từ tag>`.

Cấu hình một lần trên nuget.org (NuGet Trusted Publishing, không cần secret API key): vào tài khoản `tqk2811`, thêm trusted publishing policy với repository owner `tqk2811`, repository `TqkLibrary.Asp.ToolKit`, workflow file `release.yml`, chọn các package của repo này.
