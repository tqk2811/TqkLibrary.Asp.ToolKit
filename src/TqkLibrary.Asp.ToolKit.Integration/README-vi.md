# TqkLibrary.Asp.ToolKit.Integration

[English](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Integration/README.md) · **Tiếng Việt**

## Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Integration
```

## Cách dùng

### HTTP client có kiểu (typed)

`AddApiHttpClientFactory` đăng ký `IHttpClientFactory<T>` / `IHttpMessageHandlerFactory<T>` và đặt primary handler
mặc định là `SocketsHttpHandler` với `UseCookies = false` và `AllowAutoRedirect = false`. Primary handler được
pool và dùng chung cho cả process, nên không được giữ cookie hay tự follow redirect (đây chỉ là giá trị mặc định:
`ConfigurePrimaryHttpMessageHandler` đặt riêng cho từng client về sau sẽ ghi đè chúng).

`AddApiHttpClient<TService>` / `AddApiHttpClient<TService, TImplementation>` đăng ký service dạng transient cùng một
`HttpClient` có tên. Client luôn được đặt tên theo kiểu CỤ THỂ (`typeof(TImplementation).FullName`), nên phần cài đặt
phải nhận `IHttpClientFactory<TImplementation>` (không phải kiểu interface) trong constructor; nếu không bạn sẽ
âm thầm nhận về một client mặc định chưa được cấu hình.

```csharp
builder.Services.AddApiHttpClientFactory();
builder.Services.AddApiHttpClient<IMyApi, MyApi>((sp, client) => client.BaseAddress = new Uri(apiUrl));

public sealed class MyApi(IHttpClientFactory<MyApi> factory) : IMyApi
{
    // using var client = factory.CreateClient();
}
```

### Client cho "vỏ" API

Với API dựng bằng `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore` (thành công là `ApiResponse<T>`, lỗi là
`ApiErrorResponse`), kế thừa `ApiBase` (`BaseApi` của `TqkLibrary.Http` + `IBaseAddress`; lớp này không sở hữu
`HttpClient`) rồi gọi các extension `RequestBuilderApiExtensions` trong `TqkLibrary.Asp.ToolKit.Integration.Extensions`:

- `ExecuteApiAsync<T>()`: trả về `data`; ném `ApiException` khi thân hoặc `data` rỗng. `T` phải là reference type; với value type dùng `ExecuteApiResponseAsync<T>()` rồi đọc `Data`.
- `ExecuteApiResponseAsync<T>()`: trả nguyên `ApiResponse<T>` (`meta`, `requestId`).
- `ExecuteApiVoidAsync()`: cho endpoint không có dữ liệu.
- Mã lỗi: ném `ApiException<ApiErrorResponse>`, `Body` là lỗi của server (`Error.Code`, `Error.Fields`...).

Cấu hình JSON mặc định (`ApiBase.CreateDefaultJsonSerializerSettings()`) là camelCase, bỏ null; truyền
`JsonSerializerSettings` riêng vào constructor thứ hai để khớp với server.

```csharp
public sealed class ProductApi(IHttpClientFactory<ProductApi> factory) : ApiBase(factory.CreateClient())
{
    public Task<ProductDto> GetAsync(int id, CancellationToken ct = default)
        => Build().WithUrlGet($"api/products/{id}").ExecuteApiAsync<ProductDto>(ct);
}

try { var product = await api.GetAsync(1); }
catch (ApiException<ApiErrorResponse> ex) when (ex.Body?.Error.Code == "PRODUCT_NOT_FOUND") { /* ... */ }
```

### Các contract

- `IBaseAddress`: `Uri? BaseAddress { get; }`.
- `IDownloadFile`: `Task<Stream> DownloadFileAsync(string url, CancellationToken)` (URL đầy đủ hoặc đường dẫn tương đối).
- `IPostData`: `PostAsJsonAsync<TData>` và `PostAsJsonAsync<TData, TResult>`.

### Lưu cookie

`CookieModel` là bản sao có thể serialize của một cookie (name, value, domain, path, secure, http-only, expires). Các extension
trong `IntegrationExtensions`:

```csharp
IReadOnlyList<CookieModel> saved = cookieContainer.ToCookieModels();   // cũng có trên CookieCollection
CookieContainer restored = saved.ToCookieContainer();
existingContainer.LoadCookies(saved);
```

## Phụ thuộc

Target net10.0. Phụ thuộc `Microsoft.AspNetCore.Http.Extensions`, `Microsoft.Extensions.Configuration`,
`Microsoft.Extensions.Http`, `Newtonsoft.Json`, `System.Security.Cryptography.Xml`, `TqkLibrary.Http`,
`TqkLibrary.Utils` và `TqkLibrary.Asp.ToolKit.ViewModels`.

## Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
