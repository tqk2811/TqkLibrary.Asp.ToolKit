# TqkLibrary.Asp.ToolKit — hướng dẫn sử dụng (tiếng Việt)

Bản tiếng Anh ngắn gọn: [README.md](README.md). Tài liệu này giải thích đầy đủ cách dùng ba package ASP.NET Core:
`TqkLibrary.Asp.ToolKit.Caching`, `TqkLibrary.Asp.ToolKit.Auth.Web`, `TqkLibrary.Asp.ToolKit.Auth.Api`.

## Mục lục

- [Cài đặt và chọn package](#cài-đặt-và-chọn-package)
- [Cơ chế tổng quan của Auth](#cơ-chế-tổng-quan-của-auth)
- [Auth.Web — host Web (MVC/Razor)](#authweb--host-web-mvcrazor)
- [Auth.Api — host API (session token)](#authapi--host-api-session-token)
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
| `TqkLibrary.Asp.ToolKit.Auth.Web` | Web front end (MVC/Razor) đăng nhập qua một API riêng | Giữ session token của API trong cookie đã mã hoá, tự gắn bearer, dựng lại user hiện tại cho UI |
| `TqkLibrary.Asp.ToolKit.Auth.Api` | API sở hữu phiên đăng nhập | Session token mờ không xoay vòng, kiểm phiên mỗi request (có cache ngắn), hạn trượt ghi có giới hạn tần suất |
| `TqkLibrary.Asp.ToolKit.Caching` | Web hoặc API (hoặc cả hai) | Cache `stale-while-refresh` theo nhóm (group), vô hiệu hoá theo group, kênh vô hiệu hoá giữa hai process qua HTTP |
| `TqkLibrary.Asp.ToolKit.Queue` | Bất kỳ host nào cần xếp hàng việc nền | Hàng đợi điều phối trong process: chính sách cho chạy (trần song song), chia lượt công bằng theo khoá, vé await kết quả |

Web và API là hai app tách biệt: Web cài `Auth.Web`, API cài `Auth.Api`. `Caching` và `Queue` độc lập với các package còn lại.

## Cơ chế tổng quan của Auth

- **Không có JWT, không có refresh.** Lúc đăng nhập API tạo một dòng phiên và phát **session token mờ (opaque)**
  `{sessionId}.{secret}` (`secret` là 256 bit ngẫu nhiên). API chỉ lưu SHA-256 của `secret`; token không bao giờ xoay
  vòng, nên request song song hay nhiều instance Web không phải phối hợp gì.
- **Web** không có database người dùng. Ticket của auth cookie (Data Protection mã hoá, `HttpOnly`) chỉ chứa dữ liệu bất
  biến: session token, session id và user id. Tên, email, role, permission KHÔNG nằm trong cookie: Web lấy từ API `me`
  mỗi request (cache ngắn, mặc định 10 giây) rồi **thay** `HttpContext.User`. Server Web không lưu gì theo phiên.
- `SessionBearerHandler` (một `DelegatingHandler`) gắn `Authorization: Bearer {sessionId}.{secret}` vào mọi lời gọi API và
  gửi đúng một lần. API trả 401 thì ném `SessionExpiredException`; `SessionExpiredMiddleware` bắt nó, xoá cookie và chuyển
  về trang đăng nhập (AJAX nhận 401 JSON).
- **API là bên quyết định về phiên.** `SessionTokenAuthenticationHandler` kiểm token ở **mỗi request**: lấy dòng phiên qua
  `SessionAccessCache` (mặc định 30 giây) → `ISessionAccessSource`, so hash secret trong thời gian cố định, kiểm thu hồi,
  hạn và trần tuyệt đối, rồi dựng principal từ dữ liệu hiện tại. Hạn phiên trượt (sliding) do API ghi, tối đa một lần mỗi
  `SessionTouchInterval` (mặc định 1 giờ) cho mỗi phiên.
- Cookie hết hạn ở **trần tuyệt đối** của phiên (giá trị API trả lúc đăng nhập) và không bao giờ được cấp lại; trước mốc
  đó, phiên sống hay chết là do API trả lời.
- Đổi tên/email/role/permission của user chỉ có hiệu lực sau thời gian cache (API 30 giây, Web 10 giây), trừ khi app gọi
  invalidate (`SessionAccessCache` ở API, `CurrentUserCache` ở Web).

## Auth.Web — host Web (MVC/Razor)

### Các interface app phải cài

| Interface | Ai gọi | Việc phải làm |
|---|---|---|
| `IAuthApiClient` (`Auth.Web`) | Thư viện, khi dựng user hiện tại và khi đăng xuất | Gọi API `me`, `logout`. API từ chối `me` bằng 401/403 thì **phải** ném `SessionExpiredException`; mọi exception khác coi là lỗi tạm và **không** đăng xuất |
| `ISessionAccessSource` (`Auth.Api`) | Thư viện, ở host API | Xem [Auth.Api](#authapi--host-api-session-token) |

Ví dụ cài `IAuthApiClient` tối thiểu bằng `HttpClient`, dùng helper `ThrowIfSessionRejected`
(401/403 thì ném `SessionExpiredException`, status khác không làm gì):

```csharp
using TqkLibrary.Asp.ToolKit.Auth.Web;
using TqkLibrary.Asp.ToolKit.Auth.Web.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

public sealed class MyAuthApiClient(HttpClient http) : IAuthApiClient
{
    public async Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("auth/me", ct);
        response.ThrowIfSessionRejected();       // 401/403 -> SessionExpiredException -> đăng xuất
        response.EnsureSuccessStatusCode();      // 5xx, ... -> lỗi tạm, giữ cookie
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

- **Đừng** biến lỗi tạm (5xx, timeout, mất mạng) thành `SessionExpiredException` — người dùng sẽ bị đăng xuất oan.
- `SessionBearerHandler` tự ném `SessionExpiredException` khi API trả 401 cho bất kỳ lời gọi nào có gắn token.

### Đăng ký trong `Program.cs` (Web)

```csharp
builder.Services.AddDataProtection()                                 // giữ phiên qua lần restart; nhiều instance dùng chung thư mục
    .SetApplicationName("my-web").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddTqkWebAuth(
    cookie =>
    {
        cookie.Cookie.Name = "app_auth";
        cookie.LoginPath = "/account/login";
    },
    options => options.AnonymousPathSuffixes.Add("/auth/login"));   // endpoint API ẩn danh: không gắn token

builder.Services
    .AddHttpClient<IAuthApiClient, MyAuthApiClient>(c => c.BaseAddress = new Uri(apiUrl))
    .AddTqkSessionBearer();         // gắn session token làm bearer

var app = builder.Build();
app.UseAuthentication();
app.UseTqkWebSession();             // SessionExpiredMiddleware + CurrentUserMiddleware
app.UseAuthorization();
```

Đăng nhập / đăng xuất (giá trị lấy từ API login):

```csharp
await sessionManager.SignInAsync(HttpContext, new AuthSession                     // WebSessionManager
{
    SessionToken = login.SessionToken,     // {sessionId}.{secret}
    UserId = login.UserId,
    ExpiresAt = login.ExpiresAt,           // trần tuyệt đối của phiên (DateTimeOffset, bắt buộc)
}, persistent: model.RememberMe);
await sessionManager.SignOutAsync(HttpContext);                                  // gọi API logout rồi xoá cookie
```

#### Gọi API ẩn danh: `AnonymousApiScope`

Mọi request đi qua client có `AddTqkSessionBearer()` đều được `SessionBearerHandler` gắn token của người đang xem.
`AnonymousApiScope.RunAsync(...)` tắt việc đó cho các lời gọi nằm bên trong: request đi ra nguyên trạng, không bearer,
401 được trả nguyên thay vì thành `SessionExpiredException`.

Vì sao cần: hàm tải của cache dùng chung chạy với token của request nào kích hoạt nó trước. Nếu đó là admin, dữ liệu
riêng của admin bị cache và hiện cho khách; nếu session của người đó vừa hết hạn, lỗi `SessionExpiredException` bị trao
cho mọi request đang chờ cùng cache. Bọc lời gọi trong scope thì ai kích hoạt cũng ra cùng một lời gọi ẩn danh.

```csharp
public Task<IReadOnlyList<NewsItem>> GetLatestAsync(CancellationToken ct)
    => cache.GetOrCreateAsync("news:latest", entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
        return AnonymousApiScope.RunAsync(() => api.GetLatestNewsAsync(ct));   // bọc BÊN TRONG hàm tải
    })!;
```

So với `AnonymousPathSuffixes`: endpoint **luôn** ẩn danh (login, quên mật khẩu) thì khai một lần ở đó; scope dành cho
endpoint lúc cần token, lúc không.

Cách hoạt động và nhiều người dùng cùng lúc:

- Cờ nằm trong `AsyncLocal<bool>`, được gán bên trong một method `async`. Giá trị thật nằm trong `ExecutionContext` của
  từng luồng async, không phải biến dùng chung, nên request của người khác chạy song song không thấy cờ.
- Không có dòng nào gán lại `false`: khi `RunAsync` return, runtime trả lại `ExecutionContext` cũ cho nơi gọi, nên code
  sau `RunAsync` lại có token như thường, kể cả khi `call()` ném lỗi.
- Việc chạy nền tạo **bên trong** scope (`Task.Run`, fire-and-forget) mang theo cờ cả sau khi scope đã return.

Về cookie:

- Thư viện đặt sẵn `HttpOnly`, `SameSite=Lax` và **`SlidingExpiration = false`**: ticket hết hạn ở `AuthSession.ExpiresAt`
  (trần tuyệt đối của phiên, bắt buộc) và không bao giờ được cấp lại, vì API tự trượt phiên. Vẫn ghi đè được trong delegate `cookie => ...`.
- Ngoài môi trường Development, cookie chỉ đi qua HTTPS (`SecurePolicy = Always`).
- `WebAuthOptions.Scheme` **chỉ** đặt được qua tham số `configure` của `AddTqkWebAuth` (cần biết lúc đăng ký). Delegate `configure` được gọi **hai lần**
  (một lần trên bản snapshot lúc đăng ký, một lần qua options), nên không được có side-effect ngoài đối tượng options.

### Options của Web (`WebAuthOptions`)

| Option | Mặc định | Ý nghĩa |
|---|---|---|
| `Scheme` | `Cookies` | Tên scheme cookie |
| `SessionIdClaimType` | `tqk_sid` | Claim session id trong cookie (cookie còn có user id ở `ClaimTypes.NameIdentifier`) |
| `PermissionClaimType` | `permission` | Claim permission trên principal dựng từ `me` |
| `CurrentUserTimeout` | 15 giây | Hạn chờ lấy user hiện tại (`me`); phải đủ cho API khởi động nguội |
| `CurrentUserCacheLifetime` | 10 giây | Cache `me` theo phiên |
| `AnonymousPathSuffixes` | rỗng | Đuôi đường dẫn API ẩn danh (login, reset mật khẩu...) |
| `SessionExpiredMessage` | "Your session has expired. Please sign in again." | Thông báo trả cho AJAX / trong exception |

### Hành vi cần biết

- Mỗi lời gọi API là **đúng một lần gửi**: không refresh, không gửi lại. API chậm chỉ làm chậm đúng lời gọi đó.
- `me` lỗi tạm (mạng, 5xx, quá `CurrentUserTimeout`) → người dùng vẫn đăng nhập (chỉ có session id và user id) nhưng **không có permission nào**: trang admin trả "forbidden"
  thay vì mở ra bằng dữ liệu cũ.
- Cookie do bản 1.x cấp không có session token: user bị coi như đã đăng xuất ở lời gọi API đầu tiên và phải đăng nhập lại.
- Một khi response đã bắt đầu gửi thì không ghi được header nữa: không xoá được cookie; request sau nhận 401 từ API và sẽ xoá.

## Auth.Api — host API (session token)

### Interface app phải cài: `ISessionAccessSource`

`GetAsync(sessionId, ct)` chạy (qua `SessionAccessCache`) cho **mỗi request có token**. Trả về dòng phiên kèm dữ liệu
hiện tại của user, **kể cả khi phiên đã thu hồi/hết hạn** (thư viện tự kiểm); trả `null` khi phiên không tồn tại; tài khoản
không dùng được thì đặt `UserRejectionReason` (chỉ báo cho người gọi có secret đúng). `TouchAsync` lưu hạn trượt.

```csharp
public sealed class SessionAccessSource(AppDbContext db) : ISessionAccessSource
{
    public async Task<SessionAccessRecord?> GetAsync(string sessionId, CancellationToken ct)
    {
        // ... đọc dòng phiên + user; không có -> null; khoá -> UserRejectionReason = SessionRejectionReasons.UserLocked
        return new SessionAccessRecord
        {
            SessionId = session.Id,                  // id ĐÚNG NHƯ ĐÃ LƯU; cách viết khác trong token bị từ chối
            UserId = user.Id, SecretHash = session.SecretHash,
            CreatedAt = session.CreatedAt, LastAccessAt = session.LastAccessAt, ExpiresAt = session.ExpiresAt,
            IsPersistent = session.IsPersistent, IsRevoked = session.RevokedAt != null,
            UserName = user.UserName, Email = user.Email, Roles = roles, Permissions = permissions,
        };
    }

    public Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken ct)
        => db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAccessAt, lastAccessAt).SetProperty(s => s.ExpiresAt, expiresAt), ct);
}
```

`SessionAccessRecord` được cache và dùng chung giữa các request: không sửa nó. Bảng phiên và entity nằm ở app, không ở
thư viện. Muốn về sau gắn thêm dữ liệu theo phiên (vd public key của thiết bị) thì thêm cột ở app; định dạng token không đổi.

### Đăng ký trong `Program.cs` (API)

```csharp
builder.Services.AddTqkApiAuth(o =>
{
    o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), Persistent: TimeSpan.FromDays(30));
    o.MaxSessionLifetime = TimeSpan.FromDays(90);              // tuỳ chọn; mặc định không giới hạn
});
builder.Services.AddScoped<ISessionAccessSource, SessionAccessSource>();
builder.Services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(o =>
{
    // Tuỳ chọn: body 401 riêng kèm lý do (context.Rejection?.Reason), rồi context.HandleResponse().
    o.OnChallenge = context => Task.CompletedTask;
});
```

Đăng nhập (trong auth service của app):

```csharp
var issued = SessionTokenCodec.Create(sessionId);               // lưu issued.SecretHash cùng phiên, KHÔNG lưu token
var expires = slidingPolicy.GetInitialExpiry(now, persistent: model.RememberMe);
// ... thêm dòng phiên (CreatedAt = now, ExpiresAt = expires, IsPersistent = RememberMe)
return new { sessionToken = issued.Token, userId, expiresAt = slidingPolicy.GetAbsoluteExpiry(now) };   // hạn cookie cho Web
```

Không còn endpoint refresh. Đăng xuất: đánh dấu phiên đã thu hồi rồi `SessionAccessCache.InvalidateSession(sessionId)`.

Lý do từ chối (`invalid_token` cho token sai dạng hoặc sai secret, `revoked`, `expired`, hoặc lý do app trả) đọc được
trong `OnChallenge` qua `context.Rejection` và qua `HttpContext.GetSessionRejection()`. Token không bao giờ bị ghi log.

### Options của API (`ApiAuthOptions`)

| Option | Mặc định | Ý nghĩa |
|---|---|---|
| `UserIdClaimType` / `SessionIdClaimType` / `PermissionClaimType` | `sub` / `sid` / `permission` | Loại claim trên principal |
| `SessionLifetimes` | `new(30 ngày)` | Hạn trượt của phiên: `Normal` và `Persistent` ("ghi nhớ đăng nhập", null = như `Normal`), chọn theo cờ `IsPersistent` của phiên |
| `MaxSessionLifetime` | không | Trần tuyệt đối tính từ lúc đăng nhập |
| `SessionTouchInterval` | 1 giờ | Khoảng tối thiểu giữa hai lần ghi hạn trượt của một phiên (mỗi process); phải ngắn hơn mọi lifetime. 0 = mỗi request |
| `SessionAccessCacheLifetime` | 30 giây | Cache kết quả `ISessionAccessSource` theo phiên |

Options được kiểm khi khởi động (`ValidateOnStart`).

## Điều kiện bắt buộc phía API

Web và API chỉ chạy đúng khi API tuân thủ các điều kiện sau:

1. **Session token không xoay vòng.** Phát một lần lúc đăng nhập, dùng tới khi phiên chết.
2. **Mọi endpoint có xác thực (kể cả `me`) trả 401 khi phiên đã chết** (hết hạn, thu hồi, tài khoản bị khoá) — handler
   của thư viện làm việc này. Lỗi 5xx chỉ dùng cho lỗi tạm.
3. **Thu hồi / đổi quyền phải gọi invalidate `SessionAccessCache` ngay sau khi ghi thành công:**
   - `InvalidateSession(sessionId)` khi đăng xuất / thu hồi một phiên;
   - `InvalidateUser(userId)` khi đổi mật khẩu, khoá/xoá tài khoản, đổi role hoặc tên/email;
   - `InvalidateAll()` khi permission của role đổi, role bị đổi tên/xoá.

   `InvalidateSession` / `InvalidateUser` chỉ bỏ entry của đúng phiên / user đó; lượt tra cứu đang chạy dở lúc invalidate không được dùng lại.
   Không gọi invalidate thì thay đổi chỉ có hiệu lực sau `SessionAccessCacheLifetime`.
4. **Nhiều instance API:** cache là theo process, invalidate chỉ tới instance xử lý lần ghi; các instance khác trễ tối đa `SessionAccessCacheLifetime`
   (hoặc dùng thêm kênh chung như Redis pub/sub, hoặc kênh HTTP của `Caching` nếu phù hợp).
5. **Job dọn bảng phiên:** chạy nền định kỳ, xoá theo lô (vd 1000 dòng một lượt) các phiên đã hết hạn hoặc đã thu hồi
   lâu hơn một khoảng lưu giữ (vd 30 ngày), để bảng không phình mãi.

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

Mỗi key một cache con (hai key không chờ nhau). Các key nằm trong một `MemoryCache` riêng với `SizeLimit = maxEntries`
(mỗi key tính 1): khi đầy, `MemoryCache` compact ở nền và bỏ các key lâu không dùng.
- Key mới đi qua một bảng phụ chứa các lượt load đang chạy, nên nhiều caller cùng một key mới chỉ load **một** lần, kể cả khi cache đầy.
- Load xong key mới được chuyển vào cache. Lúc cache đầy, lượt chuyển này có thể bị từ chối tới khi compact xong: kết quả vẫn trả về, chỉ là lần gọi sau phải load lại.
- `Invalidate(key)`/`InvalidateAll()` bỏ key khỏi cache và chặn lượt load đang chạy ghi kết quả cũ.
- Cache này giữ `MemoryCache` nên là `IDisposable`; cache tạo qua `CacheRegistry.CreateKeyed` được registry dispose theo.

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
| Hạn chờ `me` (`CurrentUserTimeout`) quá ngắn | Request ngẫu nhiên chạy không có permission khi API khởi động nguội | Giữ >= 15 giây; phải đủ cho cold start |
| IIS cho app "ngủ" (idle timeout, recycle) | Cache và hàng đợi gửi vô hiệu hoá mất; lô đang chờ bị bỏ; lần đầu sau khi ngủ rất chậm | Đặt Start Mode `AlwaysRunning`, tắt idle timeout / bật preload cho app pool của cả Web lẫn API |
| Nhét thêm dữ liệu vào ticket cookie | Cookie bị chia chunk, có thể vượt giới hạn header của proxy/IIS/nginx (lỗi 400/431) | Ticket chỉ giữ session token, session id, user id; dữ liệu khác lấy từ `me` |
| Nhiều instance Web | Cookie giải mã được ở instance này mà không giải mã được ở instance kia | Dùng chung key ring Data Protection và cùng `ApplicationName` (`AddDataProtection().SetApplicationName(...).PersistKeysToFileSystem(...)`); mất key ring = mọi người phải đăng nhập lại |
| Nhiều instance API | Invalidate `SessionAccessCache` chỉ tới instance xử lý lần ghi | Chấp nhận trễ tới `SessionAccessCacheLifetime`, hoặc thêm kênh chung |
| Nhiều instance Web + cache | Một instance bị invalidate, instance khác thì không | Gửi tín hiệu tới từng instance qua kênh HTTP (mỗi instance có endpoint riêng) hoặc chấp nhận chờ TTL |
| Ghi log header `Authorization` hoặc session token | Lộ token dùng được tới khi phiên hết hạn | Không log token; thư viện chỉ log session id |
| Không có job dọn bảng phiên | Bảng phiên phình mãi | Xem [Điều kiện bắt buộc phía API](#điều-kiện-bắt-buộc-phía-api) |
| `IAuthApiClient` biến lỗi tạm thành `SessionExpiredException` (hoặc nuốt 401/403) | Đăng xuất oan, hoặc phiên chết mà không đăng xuất | Chỉ 401/403 -> `SessionExpiredException` (dùng `ThrowIfSessionRejected`), lỗi khác để nguyên |
| Hàm `load` của cache không có timeout | Lần nạp đầu treo sẽ chặn mọi caller chưa có giá trị cũ | Tự đặt timeout (ví dụ `CancellationTokenSource.CancelAfter`) trong `load` |
| `load` capture giá trị đã tính | `Invalidate` không an toàn, dữ liệu cũ được ghi lại | Đọc dữ liệu bên trong lambda |
| Gọi `registry.Create`/`CreateKeyed` theo request | Rò rỉ bộ nhớ (registry giữ tham chiếu mãi) | Tạo một lần trong singleton; registry cảnh báo khi một group có hơn 100 cache |
| Đặt `UseTqkCacheInvalidationEndpoint` sau `UseForwardedHeaders` / HTTPS redirect | Địa chỉ nguồn bị đổi hoặc response bị viết lại, endpoint từ chối | Đặt endpoint trước các middleware đó |
| Gọi `AddTqkWebAuth` với `configure` có side-effect | Side-effect chạy hai lần | Delegate chỉ được đụng tới đối tượng options |

## Phát hành

Mỗi package có version riêng, đặt bằng `<VersionPrefix>` trong csproj của nó. Muốn phát hành một package: tăng `<VersionPrefix>` ngay trong commit sửa package đó (thay đổi breaking chỉ tăng major của riêng package đó), merge vào `master`, rồi chạy tay workflow **Release** trên `master` (Actions -> Release -> Run workflow).

Workflow pack mọi package, chỉ push lên nuget.org những version nuget.org chưa có, rồi gắn tag `<Package>/vM.N.P` cho từng version vừa phát hành (vd `Auth.Web/v2.0.0`). Package không đổi version thì bỏ qua. Version mới phải lớn hơn version cao nhất đang có trên nuget.org. Mọi bản build khác ngoài workflow phát hành (`-p:TqkRelease=true`) đều ra `M.N.P-dev`.

Cấu hình một lần trên nuget.org (NuGet Trusted Publishing, không cần secret API key): vào tài khoản `tqk2811`, thêm trusted publishing policy với repository owner `tqk2811`, repository `TqkLibrary.Asp.ToolKit`, workflow file `release.yml`, chọn các package của repo này.
