# TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/README-vi.md)

ASP.NET Core server side of the `TqkLibrary.Asp.ToolKit.ViewModels` API envelope: actions return their data type
directly and every response becomes `ApiResponse<T>` or `ApiErrorResponse`. Install it on the API host only; clients
use `ViewModels` (or `Integration`).

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
```

## Usage

```csharp
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;

builder.Services.AddControllers()
    .AddTqkApiEnvelope(options =>
    {
        // all optional, defaults shown
        options.RequestIdHeader = "X-Request-Id";
        options.RequestIdPrefix = "req_";
        options.UseFieldErrorCodes = true; // default; false = every field error is FIELD_INVALID_VALUE
        options.EnableValidationResponse = true;
        // options.SerializeError = x => JsonConvert.SerializeObject(x, mySettings); // when the app uses Newtonsoft
    });
builder.Services.AddSingleton<IApiExceptionMapper, MyDomainExceptionMapper>(); // optional
builder.Services.AddSingleton<IApiErrorTransformer, MyErrorTransformer>(); // optional

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

    [HttpGet]           // already an envelope: kept as is (paging meta)
    public ApiResponse<List<ProductDto>> List(int page, int pageSize)
        => ApiResponse<List<ProductDto>>.Ok(service.List(page, pageSize), HttpContext.GetRequestId(), page, pageSize, service.Count());

    [HttpGet("export"), RawResponse] // not wrapped
    public Stream Export() => service.Export();
}
```

- `AddTqkApiEnvelope` (on `IMvcBuilder`): adds `Filters.ApiEnvelopeResultFilter`, the 400 `VALIDATION_FAILED`
  response and the field error messages. The filter wraps 2xx object results, `EmptyResult` and 200/204 status
  results into `ApiResponse<object>` (an `ObjectResult`, so the app's formatter serializes it); it skips values
  that are already `ApiResponse<T>` / `ApiErrorResponse`, endpoints with `[RawResponse]`, non 2xx results and other
  result kinds. The original `ObjectResult` is kept and only its value is replaced, so 201/202 status codes, the
  `Location` header of `CreatedAtAction` / `Created` and content types survive. A returned `Stream` becomes a
  `FileStreamResult`.
- `UseTqkRequestId()`: reads `X-Request-Id` (printable ASCII, up to 128 chars) or generates `req_<guid N>`, echoes it
  on the response. Read it with `HttpContext.GetRequestId()` (falls back to `TraceIdentifier`).
- `UseTqkApiExceptionWrapper()`: client abort (cancellation or `IOException` once `RequestAborted` is set, or `ConnectionResetException`) -> 499 without body; response already started -> connection aborted;
  `ApiErrorException` -> its status and code; then every registered `IApiExceptionMapper` in order
  (`ApiErrorMapping? Map(HttpContext, Exception)`); otherwise 500 `INTERNAL_ERROR` without exception details. Logs Error for 5xx,
  Warning otherwise (override per mapping with `LogLevel`; the exception is attached at Error and above). A mapping
  can also carry `Fields`, `Retryable` (written to the error body) and response `Headers` (`IReadOnlyDictionary<string, StringValues>`, multi-value allowed, e.g. `Retry-After`).
  Every registered `IApiErrorTransformer` then post-processes the mapping, whatever its source, in registration order (null keeps it; one that throws is logged and skipped). The body is written with `SerializeError` (default System.Text.Json, camelCase, nulls ignored).
  Apps whose MVC output uses Newtonsoft must set `SerializeError` to their own serializer so these bodies match the
  MVC responses (`o.SerializeError = v => JsonConvert.SerializeObject(v, settings)`); null properties (`meta`,
  `fields`, `details`...) are omitted only if that serializer ignores nulls.
- Validation: `UseFieldErrorCodes` (default `true`) gives each `FieldError` a code (`FIELD_REQUIRED`,
  `FIELD_TOO_LONG` with `params.max`...) for the framework DataAnnotations attributes and the model binding errors,
  plus the `message` MVC produced ("The DisplayName field is required."). Messages are not rewritten: the code is
  attached to the message string by wrapping the validators and the binding message accessors, so Razor views,
  client-side validation and resource localization are unaffected. App-set binding messages (global
  `ModelBindingMessageProvider`) are wrapped too; per-type overrides from an `IBindingMetadataProvider` are not. The
  code follows the message string MVC stored, so a message rebuilt from its text loses it. `[StringLength]` with a
  minimum reports `FIELD_OUT_OF_RANGE` (`params.min`, `params.max`). Field paths follow `[JsonPropertyName]` (and Newtonsoft `[JsonProperty]`,
  read by reflection) and are camelCased otherwise. Off, or for custom attributes and JSON reader errors, the field
  error is `FIELD_INVALID_VALUE` with its message.
- `ModelState.ToFieldErrors()` / `ModelState.ToApiErrorResponse(requestId)` for manual checks.

## Dependencies

Targets net10.0, uses the ASP.NET Core shared framework and depends on `TqkLibrary.Asp.ToolKit.ViewModels`.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
