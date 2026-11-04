# Backlog

Việc còn treo và đề xuất của thư viện. Mỗi mục: vấn đề, vị trí, vì sao nên làm, ngày ghi. Xử lý xong thì xoá mục trong cùng commit.

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

### [Thấp] ViewModels.AspNetCore: các điểm nhỏ còn lại sau review ApiErrorMapping

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/Middleware/ApiExceptionWrapperMiddleware.cs` (`HandleExceptionAsync`, nhánh 499), `Models/ApiErrorMapping.cs`, `TqkApiEnvelopeOptions.TransformError`.
- **Vấn đề**: (1) log 5xx có hai câu khác nhau tuỳ `LogLevel` null hay không, nên gộp về một lệnh `Log`; (2) chưa có test `IOException` khi client chưa abort phải ra 500; (3) XML doc của `TransformError` chưa nói trả null là giữ mapping gốc; (4) `Headers` là `Dictionary<string,string>` khả biến, không mang được header nhiều giá trị, cân nhắc `IReadOnlyDictionary<string, StringValues>`; (5) cân nhắc chuyển `TransformError` sang interface DI cho đồng bộ với `IApiExceptionMapper`, và cho `IApiExceptionMapper.Map` nhận `HttpContext` để app khỏi cần `IHttpContextAccessor`.
- **Vì sao**: gói chưa publish (1.0.0), đổi API bây giờ rẻ hơn sau khi phát hành.
- **Ngày ghi**: 2026-11-04.
