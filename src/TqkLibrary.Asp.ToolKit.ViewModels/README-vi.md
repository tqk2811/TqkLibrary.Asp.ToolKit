# TqkLibrary.Asp.ToolKit.ViewModels

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels/README.md) · **Tiếng Việt**

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels
```

## Cách dùng

Mọi kiểu đều nằm trong namespace `TqkLibrary.Asp.ToolKit.ViewModels`.

- `ApiResult` / `ApiResult<T>` (cùng `IApiResult`, `IApiResult<out T>`): `IsSuccessed`, `Message`, `Validations`,
  `ExceptionInfo` và, với bản generic, `ResultObj`. `GetErrorMsg(bool isDebug)` dựng thông báo để hiển thị;
  `ApiResult<T>` còn có `Map<TDest>(mapper)` và `As<TBase>()`.
- `ApiSuccessResult` / `ApiSuccessResult<T>` và `ApiErrorResult` / `ApiErrorResult<T>`: result thành công và lỗi dựng sẵn.
  Lỗi có thể được dựng từ một message, một `ExceptionInfo`, một danh sách `ValidationResult` hoặc một map
  `IReadOnlyDictionary<string, string[]>` (cùng dạng `ValidationProblemDetails.Errors`).
- `ValidationResult` (`Name`, `Messages`); `ValidationResult.FromErrors(errors)` dựng từ map trên.
- Chuyển từ `ModelState` của ASP.NET Core nằm ở package riêng `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore`.
- `ExceptionInfo` (`ExceptionType`, `Message`, `StackTrace`): chuyển đổi ngầm định từ `Exception`.
- `PageResult<T>`: `PageIndex`, `PageSize`, `TotalCount`, `TotalPages`, `Items`.
- `ApiException`: được client ném khi một `IApiResult` báo thất bại; mang theo `ApiResult`.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    // ToApiErrorResult<T>() nằm ở package TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
    if (!ModelState.IsValid) return ModelState.ToApiErrorResult<ProductDto>();
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}

// Phía client
var result = await api.CreateAsync(model);
if (!result.IsSuccessed) throw new ApiException(result);
```

## Phụ thuộc

Target net10.0, KHÔNG phụ thuộc ASP.NET Core, nên dùng được ở mọi client (WPF, console, worker...). Không phụ thuộc
vào các package khác của toolkit này.

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
