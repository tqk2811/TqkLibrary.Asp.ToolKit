# TqkLibrary.Asp.ToolKit.Integration

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.Integration/README-vi.md)

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

### Clients for the API envelope

For APIs built with `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore` (success `ApiResponse<T>`, error
`ApiErrorResponse`), derive from `ApiBase` (a `TqkLibrary.Http` `BaseApi` + `IBaseAddress`; the `HttpClient` is not
owned) and call the `RequestBuilderApiExtensions` of `TqkLibrary.Asp.ToolKit.Integration.Extensions`:

- `ExecuteApiAsync<T>()`: returns `data`; throws `ApiException` when the body or `data` is missing. `T` must be a reference type; for value types use `ExecuteApiResponseAsync<T>()` and read `Data`.
- `ExecuteApiResponseAsync<T>()`: returns the whole `ApiResponse<T>` (`meta`, `requestId`).
- `ExecuteApiVoidAsync()`: for endpoints without data.
- Error status: `ApiException<ApiErrorResponse>` whose `Body` is the server's error (`Error.Code`, `Error.Fields`...).

The default JSON settings (`ApiBase.CreateDefaultJsonSerializerSettings()`) are camelCase with nulls ignored; pass
your own `JsonSerializerSettings` to the second constructor to match the server.

```csharp
public sealed class ProductApi(IHttpClientFactory<ProductApi> factory) : ApiBase(factory.CreateClient())
{
    public Task<ProductDto> GetAsync(int id, CancellationToken ct = default)
        => Build().WithUrlGet($"api/products/{id}").ExecuteApiAsync<ProductDto>(ct);
}

try { var product = await api.GetAsync(1); }
catch (ApiException<ApiErrorResponse> ex) when (ex.Body?.Error.Code == "PRODUCT_NOT_FOUND") { /* ... */ }
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

Targets net10.0. Depends on `Microsoft.AspNetCore.Http.Extensions`, `Microsoft.Extensions.Configuration`,
`Microsoft.Extensions.Http`, `Newtonsoft.Json`, `System.Security.Cryptography.Xml`, `TqkLibrary.Http`,
`TqkLibrary.Utils` and `TqkLibrary.Asp.ToolKit.ViewModels`.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
