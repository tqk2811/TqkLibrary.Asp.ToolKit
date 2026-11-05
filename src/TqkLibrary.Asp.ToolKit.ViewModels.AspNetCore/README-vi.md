# TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/README.md) · **Tiếng Việt**

Phía server ASP.NET Core của "vỏ" API trong `TqkLibrary.Asp.ToolKit.ViewModels`: action trả thẳng kiểu dữ liệu,
mọi phản hồi đều thành `ApiResponse<T>` hoặc `ApiErrorResponse`. Chỉ cài trên host API; client dùng `ViewModels`
(hoặc `Integration`).

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
```

## Cách dùng

```csharp
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;

builder.Services.AddControllers()
    .AddTqkApiEnvelope(options =>
    {
        // đều tuỳ chọn, đây là giá trị mặc định
        options.RequestIdHeader = "X-Request-Id";
        options.RequestIdPrefix = "req_";
        options.UseFieldErrorCodes = true; // mặc định; false = mọi lỗi ô là FIELD_INVALID_VALUE
        options.EnableValidationResponse = true;
        // options.SerializeError = x => JsonConvert.SerializeObject(x, mySettings); // khi app dùng Newtonsoft
    });
builder.Services.AddSingleton<IApiExceptionMapper, MyDomainExceptionMapper>(); // tuỳ chọn
builder.Services.AddSingleton<IApiErrorTransformer, MyErrorTransformer>(); // tuỳ chọn

app.UseTqkRequestId();
app.UseTqkApiExceptionWrapper();
app.MapControllers();
```

```csharp
[ApiController, Route("api/products")]
public sealed class ProductsController(IProductService service) : ControllerBase
{
    [HttpGet("{id}")]   // -> { "requestId": "req_...", "data": { ... } }
    public async Task<ProductDto> Get(int id)
        => await service.FindAsync(id) ?? throw ApiErrorException.NotFound("PRODUCT_NOT_FOUND", "Product not found.");

    [HttpGet]           // đã là vỏ sẵn: giữ nguyên (có meta phân trang)
    public ApiResponse<List<ProductDto>> List(int page, int pageSize)
        => ApiResponse<List<ProductDto>>.Ok(service.List(page, pageSize), HttpContext.GetRequestId(), page, pageSize, service.Count());

    [HttpGet("export"), RawResponse] // không bọc
    public Stream Export() => service.Export();
}
```

- `AddTqkApiEnvelope` (trên `IMvcBuilder`): thêm `Filters.ApiEnvelopeResultFilter`, phản hồi 400 `VALIDATION_FAILED`
  và message lỗi theo ô. Filter bọc object result 2xx, `EmptyResult` và status result 200/204 vào
  `ApiResponse<object>` (vẫn là `ObjectResult`, formatter của app serialize); bỏ qua giá trị đã là `ApiResponse<T>` /
  `ApiErrorResponse`, endpoint gắn `[RawResponse]`, kết quả không phải 2xx và các loại result khác. Action trả
  `Stream` thì thành `FileStreamResult`. `ObjectResult` gốc được giữ lại, chỉ thay giá trị, nên status 201/202, header
  `Location` của `CreatedAtAction` / `Created` và content type vẫn nguyên.
- `UseTqkRequestId()`: đọc `X-Request-Id` (ASCII in được, tối đa 128 ký tự) hoặc sinh `req_<guid N>`, ghi lại vào
  header phản hồi. Đọc bằng `HttpContext.GetRequestId()` (không có thì dùng `TraceIdentifier`).
- `UseTqkApiExceptionWrapper()`: client ngắt kết nối (huỷ hoặc `IOException` khi `RequestAborted` đã bật, hoặc
  `ConnectionResetException`) -> 499 không thân; phản hồi đã ghi dở -> cắt kết nối;
  `ApiErrorException` -> đúng status và code của nó; sau đó thử lần lượt các `IApiExceptionMapper` đã đăng ký
  (`ApiErrorMapping? Map(HttpContext, Exception)`); còn lại 500 `INTERNAL_ERROR`, không lộ chi tiết exception. Log Error cho 5xx,
  Warning cho các mã khác (mỗi mapping đổi được bằng `LogLevel`; từ mức Error trở lên log kèm exception). Mapping còn
  mang được `Fields`, `Retryable` (ghi vào thân lỗi) và `Headers` của phản hồi (`IReadOnlyDictionary<string, StringValues>`, được nhiều giá trị, vd `Retry-After`). Sau đó
  từng `IApiErrorTransformer` đã đăng ký xử lý lại mapping, bất kể nguồn, theo thứ tự đăng ký (trả null là giữ nguyên; cái nào ném lỗi thì ghi log và bỏ qua). Thân phản hồi ghi bằng `SerializeError` (mặc định System.Text.Json, camelCase, bỏ null).
  App có MVC output dùng Newtonsoft phải đặt `SerializeError` bằng serializer của app để thân lỗi khớp phản hồi MVC
  (`o.SerializeError = v => JsonConvert.SerializeObject(v, settings)`); các thuộc tính null (`meta`, `fields`,
  `details`...) chỉ bị bỏ khi serializer đó bỏ qua null.
- Validation: `UseFieldErrorCodes` (mặc định `true`) cho mỗi `FieldError` một code (`FIELD_REQUIRED`,
  `FIELD_TOO_LONG` kèm `params.max`...) với các attribute DataAnnotations có sẵn và lỗi model binding, kèm `message`
  do MVC sinh ("The DisplayName field is required."). Message không bị sửa: code được gắn vào chính chuỗi message
  bằng cách bọc validator và các accessor message của model binding, nên Razor view, validation phía client và
  localization dựa trên resource không bị ảnh hưởng. Message binding app tự đặt (`ModelBindingMessageProvider` toàn
  cục) cũng được bọc; message đặt riêng theo type qua `IBindingMetadataProvider` thì không. Code đi theo chính chuỗi
  message MVC lưu, nên message dựng lại từ nội dung chữ sẽ mất code. `[StringLength]` có độ dài tối thiểu báo
  `FIELD_OUT_OF_RANGE` (`params.min`, `params.max`). Đường dẫn ô theo `[JsonPropertyName]` (và `[JsonProperty]` của Newtonsoft, đọc bằng
  reflection), còn lại đổi sang camelCase. Tắt, hoặc với attribute tự viết và lỗi đọc JSON, thì lỗi ô là
  `FIELD_INVALID_VALUE` kèm message của nó.
- `ModelState.ToFieldErrors()` / `ModelState.ToApiErrorResponse(requestId)` cho các chỗ kiểm tay.

## Phụ thuộc

Target net10.0, dùng ASP.NET Core shared framework và phụ thuộc `TqkLibrary.Asp.ToolKit.ViewModels`.

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
