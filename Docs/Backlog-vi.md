# Backlog

Việc còn treo và đề xuất của thư viện. Mỗi mục: vấn đề, vị trí, vì sao nên làm, ngày ghi. Xử lý xong thì xoá mục trong cùng commit.

### [Thấp] ViewModels.AspNetCore: các điểm nhỏ còn lại sau review ApiErrorMapping

- **Vị trí**: `src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/Middleware/ApiExceptionWrapperMiddleware.cs` (`HandleExceptionAsync`, nhánh 499), `Models/ApiErrorMapping.cs`, `TqkApiEnvelopeOptions.TransformError`.
- **Vấn đề**: (1) log 5xx có hai câu khác nhau tuỳ `LogLevel` null hay không, nên gộp về một lệnh `Log`; (2) chưa có test `IOException` khi client chưa abort phải ra 500; (3) XML doc của `TransformError` chưa nói trả null là giữ mapping gốc; (4) `Headers` là `Dictionary<string,string>` khả biến, không mang được header nhiều giá trị, cân nhắc `IReadOnlyDictionary<string, StringValues>`; (5) cân nhắc chuyển `TransformError` sang interface DI cho đồng bộ với `IApiExceptionMapper`, và cho `IApiExceptionMapper.Map` nhận `HttpContext` để app khỏi cần `IHttpContextAccessor`.
- **Vì sao**: gói chưa publish (1.0.0), đổi API bây giờ rẻ hơn sau khi phát hành.
- **Ngày ghi**: 2026-11-04.
