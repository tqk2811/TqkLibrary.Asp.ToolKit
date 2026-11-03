# Caching: so sánh `TqkLibrary.Asp.ToolKit.Caching` với thư viện có sẵn

Package `TqkLibrary.Asp.ToolKit.Caching` (`StaleWhileRefreshCache<T>`, `KeyedStaleWhileRefreshCache<TKey, T>`, `CacheRegistry`, kênh invalidate qua HTTP) từng bị bỏ rồi được khôi phục. Ghi chú này giữ lại bài học khi thử dựng trên `MemoryCache`, và so sánh với hai thư viện có sẵn:

- `Microsoft.Extensions.Caching.Hybrid` (`HybridCache`) của Microsoft, có từ .NET 9.
- `ZiggyCreatures.FusionCache`, thư viện mã nguồn mở bên ngoài.

Thông tin ghi ngày 2026-11-03 theo tài liệu và release notes lúc đó. Trước khi chọn, đọc lại tài liệu bản đang dùng vì tính năng hai bên thay đổi khá nhanh.

## Vì sao không dựng `KeyedStaleWhileRefreshCache` trên `MemoryCache`

Yêu cầu là thêm giới hạn dung lượng: cache quá lớn hoặc RAM căng thì xoá phần cũ, giống `IMemoryCache`. Thử thay bảng key của `KeyedStaleWhileRefreshCache` bằng `MemoryCache` có `SizeLimit` thì gặp lỗi:

- `MemoryCache.Set` khi vượt `SizeLimit` **từ chối entry mới**, rồi mới compact ở nền.
- Hệ quả là key mới không được giữ lại, nên nhiều request cùng key lại load song song (mất single-flight).
- Đổi size của entry sau khi load cũng có thể bị từ chối, và entry cũ của key đó bị xoá theo.

Bản hiện tại vẫn dùng `MemoryCache` nhưng né được các lỗi trên:

- Key mới đi qua một bảng phụ chứa lượt load đang chạy rồi mới `Set` vào cache, nên single-flight không phụ thuộc việc `Set` có được nhận hay không.
- Mỗi key có `Size = 1` cố định, không đổi size sau khi load.
- `CompactionPercentage` mặc định 5% làm compact xoá `(long)(số entry × 5%)` = 0 entry khi dưới 20 key, và cache đầy sẽ không bao giờ nhận key mới. Phải đặt tối thiểu `1.5 / maxEntries`.

Hai thư viện dưới đây đã giải quyết sẵn việc chặn stampede trên nền `IMemoryCache`; cần giới hạn dung lượng thì cân nhắc dùng chúng thay vì vá cache tự viết.

## So sánh

| Tiêu chí | `HybridCache` (Microsoft) | `FusionCache` |
|---|---|---|
| Nguồn | Gói chính thức của Microsoft, nằm trong `dotnet/extensions` | Thư viện ngoài (ZiggyCreatures), MIT |
| Target | .NET 9 trở lên (gói chạy được cả trên một số bản cũ hơn) | .NET Standard 2.0 |
| Tầng bộ nhớ (L1) | `IMemoryCache` | `IMemoryCache` |
| Tầng phân tán (L2) | `IDistributedCache` tuỳ chọn (Redis, SQL…) | `IDistributedCache` tuỳ chọn |
| Chặn stampede (một key một lần load) | Có | Có |
| Trả giá trị cũ khi đang nạp lại (stale-while-refresh) | Không: hết hạn thì mọi request chờ một lần load | Có: *eager refresh* nạp lại ở nền trước khi hết hạn; *soft timeout* trả giá trị cũ nếu factory chậm |
| Trả giá trị cũ khi load lỗi | Không | Có: *fail-safe* |
| Timeout cho factory | Không có sẵn, tự đặt trong factory | Có: soft và hard timeout |
| Xoá theo nhóm | Tag (`RemoveByTagAsync`) | Tag |
| Đồng bộ invalidate giữa nhiều instance | Không có sẵn cho L1: mỗi instance giữ L1 riêng tới hết hạn | Có: *backplane* (vd gói Redis riêng) |
| API trừu tượng chung | Là chính abstraction `HybridCache` | Có adapter cài đặt `HybridCache`, code viết theo `HybridCache` có thể đổi sang FusionCache sau |
| Giới hạn dung lượng | Qua `IMemoryCache` dùng chung trong DI: bật `SizeLimit` thì mọi entry của app phải khai `Size` | Có thể dùng `MemoryCache` riêng cho FusionCache |

## Khớp với hành vi của cache cũ

| Hành vi của `StaleWhileRefreshCache` | `HybridCache` | `FusionCache` |
|---|---|---|
| Một lần load cho nhiều request | Có | Có |
| Trả giá trị cũ trong lúc nạp lại | Không | Eager refresh / soft timeout |
| Load lỗi thì trả giá trị cũ, gia hạn ngắn (`MaxStaleBackoff`) | Không | Fail-safe (có throttle) |
| Invalidate theo nhóm (`CacheRegistry` + enum group) | Tag | Tag |
| Invalidate từ process khác (kênh HTTP) | Tự làm | Backplane |

## Chọn cái nào

- **Chỉ muốn dùng gói của Microsoft, chấp nhận mất stale-while-refresh và fail-safe:** `HybridCache`. Phù hợp khi load nhanh, ổn định, và việc chờ một lần load lúc hết hạn không đáng kể.
- **Cần giữ đủ hành vi của cache cũ** (không để request chờ khi nạp lại, backend lỗi vẫn trả dữ liệu cũ, đồng bộ nhiều instance): `FusionCache`.
- Muốn để ngỏ đường đổi về sau thì viết code theo abstraction `HybridCache`. Cần thêm tính năng thì đăng ký FusionCache làm cài đặt qua adapter, khỏi sửa chỗ gọi. Lưu ý: chỉ API của `HybridCache` không bật được eager refresh hay fail-safe theo từng lần gọi; phải cấu hình mặc định ở phía FusionCache.

## Tham khảo

- `HybridCache`: https://learn.microsoft.com/aspnet/core/performance/caching/hybrid
- `FusionCache`: https://github.com/ZiggyCreatures/FusionCache (các mục `docs/EagerRefresh.md`, `docs/FailSafe.md`, `docs/Timeouts.md`, `docs/Backplane.md`, `docs/MicrosoftHybridCache.md`)
