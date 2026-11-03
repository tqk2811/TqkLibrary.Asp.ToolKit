# TqkLibrary.Asp.ToolKit.Integration

**English** · [Tiếng Việt](#tiếng-việt)

HTTP integration helpers (typed HttpClient factory, download/post helpers, cookie persistence) for ASP.NET Core services.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.Integration
```

## Usage

### Typed HTTP clients

`AddApiHttpClientFactory` registers `IHttpClientFactory<T>` / `IHttpMessageHandlerFactory<T>` and sets the default
primary handler to a `SocketsHttpHandler` with `UseCookies = false` and `AllowAutoRedirect = false`. The primary
handler is pooled and shared by the whole process, so it must not hold cookies or follow redirects (these are only
defaults: a later per-client `ConfigurePrimaryHttpMessageHandler` overrides them).

`AddApiHttpClient<TService>` / `AddApiHttpClient<TService, TImplementation>` register the service as transient plus a
named `HttpClient`. The client is always named after the CONCRETE type (`typeof(TImplementation).FullName`), so the
implementation must take `IHttpClientFactory<TImplementation>` (not the interface type) in its constructor;
otherwise you silently get an unconfigured default client.

```csharp
builder.Services.AddApiHttpClientFactory();
builder.Services.AddApiHttpClient<IMyApi, MyApi>((sp, client) => client.BaseAddress = new Uri(apiUrl));

public sealed class MyApi(IHttpClientFactory<MyApi> factory) : IMyApi
{
    // using var client = factory.CreateClient();
}
```

### Contracts

- `IBaseAddress`: `Uri? BaseAddress { get; }`.
- `IDownloadFile`: `Task<Stream> DownloadFileAsync(string url, CancellationToken)` (full URL or relative path).
- `IPostData`: `PostAsJsonAsync<TData>` and `PostAsJsonAsync<TData, TResult>`.

### Cookie persistence

`CookieModel` is a serializable copy of a cookie (name, value, domain, path, secure, http-only, expires). Extensions
in `IntegrationExtensions`:

```csharp
IReadOnlyList<CookieModel> saved = cookieContainer.ToCookieModels();   // also on CookieCollection
CookieContainer restored = saved.ToCookieContainer();
existingContainer.LoadCookies(saved);
```

## Dependencies

Targets net10.0. Depends on `Microsoft.AspNetCore.Http.Extensions`, `Microsoft.AspNetCore.Identity`,
`Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Http`, `Newtonsoft.Json`,
`System.Security.Cryptography.Xml`, `TqkLibrary.Http` and `TqkLibrary.Utils`.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)

---

## Tiếng Việt

### Cài đặt

```
dotnet add package TqkLibrary.Asp.ToolKit.Integration
```

### Cách dùng

#### HTTP client có kiểu (typed)

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

#### Các contract

- `IBaseAddress`: `Uri? BaseAddress { get; }`.
- `IDownloadFile`: `Task<Stream> DownloadFileAsync(string url, CancellationToken)` (URL đầy đủ hoặc đường dẫn tương đối).
- `IPostData`: `PostAsJsonAsync<TData>` và `PostAsJsonAsync<TData, TResult>`.

#### Lưu cookie

`CookieModel` là bản sao có thể serialize của một cookie (name, value, domain, path, secure, http-only, expires). Các extension
trong `IntegrationExtensions`:

```csharp
IReadOnlyList<CookieModel> saved = cookieContainer.ToCookieModels();   // cũng có trên CookieCollection
CookieContainer restored = saved.ToCookieContainer();
existingContainer.LoadCookies(saved);
```

### Phụ thuộc

Target net10.0. Phụ thuộc `Microsoft.AspNetCore.Http.Extensions`, `Microsoft.AspNetCore.Identity`,
`Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Http`, `Newtonsoft.Json`,
`System.Security.Cryptography.Xml`, `TqkLibrary.Http` và `TqkLibrary.Utils`.

### Liên kết

- Mã nguồn và issue: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Hướng dẫn đầy đủ tiếng Việt: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Các package khác: [danh sách package](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
