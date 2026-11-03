# TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/README.md) · **Tiếng Việt**

Phần dành cho server ASP.NET Core của `TqkLibrary.Asp.ToolKit.ViewModels`: chuyển `ModelStateDictionary` sang
`ApiErrorResult` / `ValidationResult` và ngược lại. Chỉ cài ở host API; client chỉ cần `ViewModels`.

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
```

## Cách dùng

Các extension nằm trong namespace `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions`.

- `ModelState.ToApiErrorResult()` / `ToApiErrorResult<T>()`: kết quả lỗi, mỗi key không hợp lệ là một validation.
  Ném `InvalidOperationException` nếu model state hợp lệ.
- `ModelState.ToValidationResults()`: bỏ qua key không có lỗi; lỗi binding chỉ có exception (không có message) được
  thay bằng câu chung `ModelStateExtensions.DefaultErrorMessage` (không gửi chi tiết exception ra ngoài).
- `ModelState.AddErrors(validations)`: chép validation result (vd lấy từ phản hồi API) vào model state.

```csharp
[HttpPost]
public ApiResult<ProductDto> Create(CreateProductModel model)
{
    if (!ModelState.IsValid) return ModelState.ToApiErrorResult<ProductDto>();
    var product = service.Create(model);
    return new ApiSuccessResult<ProductDto>(product, "Created");
}
```

## Phụ thuộc

Target net10.0, dùng ASP.NET Core shared framework và phụ thuộc `TqkLibrary.Asp.ToolKit.ViewModels`.

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
