# TqkLibrary.Asp.ToolKit.ViewModels

**English** · [Tiếng Việt](#tiếng-việt)

Common API result, validation result, exception info and page result view models for ASP.NET Core APIs.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels
```

## Usage

All types live in the `TqkLibrary.Asp.ToolKit.ViewModels` namespace.

- `ApiResult` / `ApiResult<T>` (and `IApiResult`, `IApiResult<out T>`): `IsSuccessed`, `Message`, `Validations`,
  `ExceptionInfo` and, for the generic one, `ResultObj`. `GetErrorMsg(bool isDebug)` builds a display message;
  `ApiResult<T>` also has `Map<TDest>(mapper)` and `As<TBase>()`.
- `ApiSuccessResult` / `ApiSuccessResult<T>` and `ApiErrorResult` / `ApiErrorResult<T>`: ready-made success and error
  results. Errors can be built from a message, an `ExceptionInfo`, a list of `ValidationResult` or an invalid
  `ModelStateDictionary`.
- `ValidationResult` (`Name`, `Messages`) and `ModelStateDictionary.Parse()` / `AddErrors(...)` to convert between
  ASP.NET Core model state and validation results.
- `ExceptionInfo` (`ExceptionType`, `Message`, `StackTrace`): implicit conversion from `Exception`.
- `PageResult<T>`: `PageIndex`, `PageSize`, `TotalCount`, `TotalPages`, `Items`.
- `ApiException`: thrown by clients when an `IApiResult` reports failure; carries the `ApiResult`.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    if (!ModelState.IsValid) return ModelState;                  // ApiErrorResult<ProductDto> with validations
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}

// Client side
var result = await api.CreateAsync(model);
if (!result.IsSuccessed) throw new ApiException(result);
```

## Dependencies

Targets net10.0 and uses the ASP.NET Core shared framework. No dependency on other packages of this toolkit.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)

---

## Tiếng Việt

### Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels
```

### Cách dùng

Mọi kiểu đều nằm trong namespace `TqkLibrary.Asp.ToolKit.ViewModels`.

- `ApiResult` / `ApiResult<T>` (cùng `IApiResult`, `IApiResult<out T>`): `IsSuccessed`, `Message`, `Validations`,
  `ExceptionInfo` và, với bản generic, `ResultObj`. `GetErrorMsg(bool isDebug)` dựng thông báo để hiển thị;
  `ApiResult<T>` còn có `Map<TDest>(mapper)` và `As<TBase>()`.
- `ApiSuccessResult` / `ApiSuccessResult<T>` và `ApiErrorResult` / `ApiErrorResult<T>`: result thành công và lỗi dựng sẵn.
  Lỗi có thể được dựng từ một message, một `ExceptionInfo`, một danh sách `ValidationResult` hoặc một
  `ModelStateDictionary` không hợp lệ.
- `ValidationResult` (`Name`, `Messages`) và `ModelStateDictionary.Parse()` / `AddErrors(...)` để chuyển đổi giữa
  model state của ASP.NET Core và validation result.
- `ExceptionInfo` (`ExceptionType`, `Message`, `StackTrace`): chuyển đổi ngầm định từ `Exception`.
- `PageResult<T>`: `PageIndex`, `PageSize`, `TotalCount`, `TotalPages`, `Items`.
- `ApiException`: được client ném khi một `IApiResult` báo thất bại; mang theo `ApiResult`.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    if (!ModelState.IsValid) return ModelState;                  // ApiErrorResult<ProductDto> kèm validations
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}

// Phía client
var result = await api.CreateAsync(model);
if (!result.IsSuccessed) throw new ApiException(result);
```

### Phụ thuộc

Target net10.0 và dùng ASP.NET Core shared framework. Không phụ thuộc vào các package khác của toolkit này.

### Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
