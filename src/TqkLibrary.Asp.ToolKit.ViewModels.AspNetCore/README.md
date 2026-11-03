# TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/README-vi.md)

ASP.NET Core server-side helpers for `TqkLibrary.Asp.ToolKit.ViewModels`: convert `ModelStateDictionary` to
`ApiErrorResult` / `ValidationResult` and back. Install it on the API host only; clients use `ViewModels` alone.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
```

## Usage

Extensions live in the `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions` namespace.

- `ModelState.ToApiErrorResult()` / `ToApiErrorResult<T>()`: error result with one validation entry per invalid key.
  Throws `InvalidOperationException` when the model state is valid.
- `ModelState.ToValidationResults()`: keys without errors are skipped; a binding error that has only an exception
  gets the generic message `ModelStateExtensions.DefaultErrorMessage` (exception details are not sent).
- `ModelState.AddErrors(validations)`: copies validation results (for example from an API response) into the model state.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    if (!ModelState.IsValid) return ModelState.ToApiErrorResult<ProductDto>();
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}
```

## Dependencies

Targets net10.0, uses the ASP.NET Core shared framework and depends on `TqkLibrary.Asp.ToolKit.ViewModels`.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
