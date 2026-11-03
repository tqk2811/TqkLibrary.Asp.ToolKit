# TqkLibrary.Asp.ToolKit.ViewModels

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels/README.md) · **Tiếng Việt**

Các view model "vỏ" (envelope) của API, dùng chung cho server và client: mọi phản hồi thành công là `ApiResponse<T>`,
mọi phản hồi lỗi là `ApiErrorResponse`.

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels
```

## Cách dùng

Mọi kiểu đều nằm trong namespace `TqkLibrary.Asp.ToolKit.ViewModels`.

| Kiểu | Hình dạng JSON |
| --- | --- |
| `ApiResponse<T>` | `{ requestId, data, meta }`; `Ok(data, requestId, meta?)`, `Ok(data, requestId, page, pageSize, total)` |
| `ApiResponseMetadata` | `{ page, pageSize, total, totalPages }` |
| `ApiErrorResponse` | `{ requestId, error }`; `Create(requestId, error)` |
| `ApiError` | `{ code, message, details, retryable, fields }`; `Create(code, message, details?)` |
| `FieldError` | `{ field, code, params, message }`: `field` là đường dẫn phía client (`items[0].name`) |
| `PagedResult<T>` | `{ items, page, pageSize, total, totalPages }`; `Create(items, page, pageSize, total)` |
| `ApiErrorCodes` | `VALIDATION_FAILED`, `INTERNAL_ERROR`, `UNAUTHORIZED`, `FORBIDDEN`, `NOT_FOUND`, `CONFLICT`, `FIELD_REQUIRED`, `FIELD_INVALID_VALUE`, `FIELD_INVALID_FORMAT`, `FIELD_OUT_OF_RANGE`, `FIELD_TOO_LONG`, `FIELD_TOO_SHORT` |

`totalPages` bằng 0 khi `pageSize` không dương (không chia cho 0).

```csharp
ApiResponse<List<ProductDto>> page = ApiResponse<List<ProductDto>>.Ok(items, requestId, page: 1, pageSize: 20, total: 45);
// meta.totalPages == 3

ApiErrorResponse error = ApiErrorResponse.Create(requestId, ApiError.Create("PRODUCT_NOT_FOUND", "Product not found."));
```

Các kiểu không gắn attribute JSON nào. Hãy cấu hình serializer của app dùng camelCase và bỏ qua giá trị null
(System.Text.Json: `DefaultIgnoreCondition = WhenWritingNull`; Newtonsoft: `NullValueHandling.Ignore`), để các
thành viên tuỳ chọn như `retryable`, `fields`, `meta` không bị ghi ra khi rỗng.

Phía server (result filter, middleware, phản hồi validation): `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore`.
Phía client (`ExecuteApiAsync<T>`, `ApiBase`): `TqkLibrary.Asp.ToolKit.Integration`.

## Phụ thuộc

Target net10.0, KHÔNG phụ thuộc ASP.NET Core hay thư viện JSON nào, nên mọi client (WPF, console, worker...) đều
dùng được. Không phụ thuộc package nào khác của toolkit.

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
