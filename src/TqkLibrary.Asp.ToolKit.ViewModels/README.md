# TqkLibrary.Asp.ToolKit.ViewModels

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels/README-vi.md)

Common API result, validation result, exception info and page result view models shared by API servers and clients.

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
  results. Errors can be built from a message, an `ExceptionInfo`, a list of `ValidationResult` or an
  `IReadOnlyDictionary<string, string[]>` map (same shape as `ValidationProblemDetails.Errors`).
- `ValidationResult` (`Name`, `Messages`); `ValidationResult.FromErrors(errors)` builds them from that map.
- Conversions from ASP.NET Core `ModelState` live in the separate package `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore`.
- `ExceptionInfo` (`ExceptionType`, `Message`, `StackTrace`): implicit conversion from `Exception`.
- `PageResult<T>`: `PageIndex`, `PageSize`, `TotalCount`, `TotalPages`, `Items`.
- `ApiException`: thrown by clients when an `IApiResult` reports failure; carries the `ApiResult`.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    // ToApiErrorResult<T>() comes from TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
    if (!ModelState.IsValid) return ModelState.ToApiErrorResult<ProductDto>();
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}

// Client side
var result = await api.CreateAsync(model);
if (!result.IsSuccessed) throw new ApiException(result);
```

## Dependencies

Targets net10.0 with NO ASP.NET Core dependency, so any client (WPF, console, worker...) can use it. No dependency
on other packages of this toolkit.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
