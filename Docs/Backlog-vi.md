# Backlog

Việc còn treo và đề xuất của thư viện. Mỗi mục: vấn đề, vị trí, vì sao nên làm, ngày ghi. Xử lý xong thì xoá mục trong cùng commit.

### [Thấp] Sender đọc CurrentValue hai lần mỗi lô, không kiểm lại TargetUrl lúc chạy

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.Caching/Remote/HttpCacheInvalidationSender.cs:84`, `:92`.
- **Vấn đề**: debounce và phần gửi lấy từ hai bản options khác nhau. `TargetUrl` và bật/tắt chỉ xét lúc khởi động; reload sang URL sai thì `new HttpRequestMessage` ném ở mỗi lần thử và retry vô ích.
- **Đề xuất**: đọc `CurrentValue` một lần mỗi lô; kiểm lại URL mỗi lô (sai thì LogError, bỏ lô, không retry). Hoặc ghi rõ trong README chỉ Debounce/Timeout/RetryDelays/Key được reload.
- **Ngày ghi**: 2026-10-28.

### [Thấp] README chưa cảnh báo ForwardingHeaders = [] tắt bước chặn request qua proxy

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.Caching/Remote/CacheInvalidationEndpointOptions.cs:30`, `README-vi.md:360`.
- **Vấn đề**: đặt `ForwardingHeaders = []` bằng code thì endpoint chỉ còn kiểm loopback, mà sau reverse proxy cùng máy thì loopback luôn đúng → ai gọi qua proxy cũng xoá được cache.
- **Đề xuất**: thêm cảnh báo vào README (cả `README.md`), hoặc log warning lúc khởi động khi mảng rỗng.
- **Ngày ghi**: 2026-10-28.

### [Thấp] Auth/Caching chưa có test

- **Vấn đề**: repo mới có project test cho Queue; chưa có test cho các hành vi đã sửa: config danh sách thay thế mặc định (bind từ JSON), `SessionExpiredMiddleware` giữ header xoá cookie, `SessionAccessCache` vô hiệu theo thế hệ.
- **Vì sao**: refactor sau dễ làm lỗi cũ quay lại mà không ai biết.
- **Ngày ghi**: 2026-10-28.

### [Thấp] Queue: DisposeAsync không dispose `_shutdown`/`_wake`, và bỏ sót vé khi vòng điều phối chết

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.Queue/DispatchQueue.cs` `DisposeAsync` (~dòng 105-121).
- **Vấn đề**: hai đối tượng đồng bộ nội bộ không được dispose. Nếu task điều phối faulted thì `await _dispatcher` ném lại lỗi, `CancelInbox()` phía sau không chạy, vé đang chờ/đang chạy treo mãi.
- **Vì sao**: hiện chưa rò handle (không ai đọc `AvailableWaitHandle`) và `LoopAsync` đã bắt hết lỗi, nên chỉ là phòng xa; giữ nguyên lúc port để không đổi logic so với bản gốc.
- **Đề xuất**: bọc `await _dispatcher` trong try/finally, luôn huỷ vé còn lại rồi dispose hai đối tượng.
- **Ngày ghi**: 2026-11-03.

### [Thấp] Queue: hai bản code song song với repo gốc

- **Vấn đề**: package Queue được chép từ một repo khác, repo đó vẫn giữ bản riêng. Sửa lỗi ở một bên dễ quên bên kia.
- **Đề xuất**: cho repo gốc chuyển sang dùng package này rồi xoá bản riêng.
- **Ngày ghi**: 2026-11-03.

### [Vừa] Integration kéo theo System.Security.Cryptography.Pkcs có cảnh báo lỗ hổng

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.Integration/TqkLibrary.Asp.ToolKit.Integration.csproj` (dependency `System.Security.Cryptography.Xml` 10.0.12 kéo theo `Pkcs` 10.0.12).
- **Vấn đề**: restore báo NU1902/NU1903 (mức moderate và high); package publish ra mang theo dependency này.
- **Đề xuất**: nâng `System.Security.Cryptography.Xml` lên bản đã vá, hoặc bỏ nếu Integration không thật sự cần.
- **Ngày ghi**: 2026-11-03.

### [Thấp] ViewModels.AspNetCore: mã lỗi field dựa vào ghi đè ErrorMessage toàn app

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/Validation/FieldErrorMessageProvider.cs`, `FieldValidationErrors.cs`.
- **Vấn đề**: muốn có mã lỗi chính xác (vd `FIELD_REQUIRED`) phải bật `UseFieldErrorMessages`, tuỳ chọn này nhét mã vào chuỗi `ErrorMessage` của mọi attribute trong app (ảnh hưởng Razor, validate phía client, bản dịch). Mặc định tắt thì `[Required]` chỉ ra `FIELD_INVALID_VALUE`.
- **Đề xuất**: khi dựng response 400, lần từ `ModelError` về attribute qua `ModelMetadata.ValidatorMetadata` (hoặc `IModelValidatorProvider` riêng) để lấy mã và tham số, không sửa message.
- **Ngày ghi**: 2026-11-03.

### [Thấp] ViewModels.AspNetCore: các điểm nhỏ còn lại sau review ApiErrorMapping

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/Middleware/ApiExceptionWrapperMiddleware.cs` (`HandleExceptionAsync`, nhánh 499), `Models/ApiErrorMapping.cs`, `TqkApiEnvelopeOptions.TransformError`.
- **Vấn đề**: (1) log 5xx có hai câu khác nhau tuỳ `LogLevel` null hay không, nên gộp về một lệnh `Log`; (2) chưa có test `IOException` khi client chưa abort phải ra 500; (3) XML doc của `TransformError` chưa nói trả null là giữ mapping gốc; (4) `Headers` là `Dictionary<string,string>` khả biến, không mang được header nhiều giá trị, cân nhắc `IReadOnlyDictionary<string, StringValues>`; (5) cân nhắc chuyển `TransformError` sang interface DI cho đồng bộ với `IApiExceptionMapper`, và cho `IApiExceptionMapper.Map` nhận `HttpContext` để app khỏi cần `IHttpContextAccessor`.
- **Vì sao**: gói chưa publish (1.0.0), đổi API bây giờ rẻ hơn sau khi phát hành.
- **Ngày ghi**: 2026-11-04.
