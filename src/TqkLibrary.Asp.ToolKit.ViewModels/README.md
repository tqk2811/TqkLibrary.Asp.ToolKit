# TqkLibrary.Asp.ToolKit.ViewModels

**English** · [Tiếng Việt](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/src/TqkLibrary.Asp.ToolKit.ViewModels/README-vi.md)

API envelope view models shared by API servers and clients: every success is `ApiResponse<T>`, every error is
`ApiErrorResponse`.

## Install

```
dotnet add package TqkLibrary.Asp.ToolKit.ViewModels
```

## Usage

All types live in the `TqkLibrary.Asp.ToolKit.ViewModels` namespace.

| Type | Shape |
| --- | --- |
| `ApiResponse<T>` | `{ requestId, data, meta }`; `Ok(data, requestId, meta?)`, `Ok(data, requestId, page, pageSize, total)` |
| `ApiResponseMetadata` | `{ page, pageSize, total, totalPages }` |
| `ApiErrorResponse` | `{ requestId, error }`; `Create(requestId, error)` |
| `ApiError` | `{ code, message, details, retryable, fields }`; `Create(code, message, details?)` |
| `FieldError` | `{ field, code, params, message }`: `field` is the client path (`items[0].name`) |
| `PagedResult<T>` | `{ items, page, pageSize, total, totalPages }`; `Create(items, page, pageSize, total)` |
| `ApiErrorCodes` | `VALIDATION_FAILED`, `INTERNAL_ERROR`, `UNAUTHORIZED`, `FORBIDDEN`, `NOT_FOUND`, `CONFLICT`, `FIELD_REQUIRED`, `FIELD_INVALID_VALUE`, `FIELD_INVALID_FORMAT`, `FIELD_OUT_OF_RANGE`, `FIELD_TOO_LONG`, `FIELD_TOO_SHORT` |

`totalPages` is 0 when `pageSize` is not positive (no divide by zero).

```csharp
ApiResponse<List<ProductDto>> page = ApiResponse<List<ProductDto>>.Ok(items, requestId, page: 1, pageSize: 20, total: 45);
// meta.totalPages == 3

ApiErrorResponse error = ApiErrorResponse.Create(requestId, ApiError.Create("PRODUCT_NOT_FOUND", "Product not found."));
```

The types carry no JSON attributes. Configure the serializer of the app to use camelCase and to ignore nulls
(System.Text.Json: `DefaultIgnoreCondition = WhenWritingNull`; Newtonsoft: `NullValueHandling.Ignore`), so optional
members such as `retryable`, `fields` or `meta` are not written when empty.

Server side (result filter, middlewares, validation response): `TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore`.
Client side (`ExecuteApiAsync<T>`, `ApiBase`): `TqkLibrary.Asp.ToolKit.Integration`.

## Dependencies

Targets net10.0 with NO ASP.NET Core and NO JSON library dependency, so any client (WPF, console, worker...) can use
it. No dependency on other packages of this toolkit.

## Links

- Source and issues: [https://github.com/tqk2811/TqkLibrary.Asp.ToolKit](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit)
- Full guide in Vietnamese: [README-vi.md](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit/blob/master/README-vi.md)
- Other packages: [package list](https://github.com/tqk2811/TqkLibrary.Asp.ToolKit#packages)
