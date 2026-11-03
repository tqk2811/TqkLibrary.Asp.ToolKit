# TqkLibrary.Asp.ToolKit

Small, independent building blocks for ASP.NET Core applications. Vietnamese: [README-vi.md](README-vi.md) (full guide).

## Packages

| Package | Description |
| --- | --- |
| [Caching](src/TqkLibrary.Asp.ToolKit.Caching/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.Caching.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Caching) | In-process stale-while-refresh caches with group-based invalidation, optional cross-process invalidation. |
| [Auth.Web](src/TqkLibrary.Asp.ToolKit.Auth.Web/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.Auth.Web.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Web) | Web front end (BFF) sign-in against a separate JWT API; tokens kept in the encrypted auth cookie. |
| [Auth.Api](src/TqkLibrary.Asp.ToolKit.Auth.Api/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.Auth.Api.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Auth.Api) | JWT API side: session-bound access tokens, non-rotating refresh tokens, per-request session check. |
| [Queue](src/TqkLibrary.Asp.ToolKit.Queue/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.Queue.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Queue) | In-process dispatch queue with admission policies, fair scheduling and awaitable tickets. |
| [Integration](src/TqkLibrary.Asp.ToolKit.Integration/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.Integration.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.Integration) | Typed HttpClient factory, download/post helpers and cookie persistence. |
| [ViewModels](src/TqkLibrary.Asp.ToolKit.ViewModels/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.ViewModels.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.ViewModels) | Common API result, validation result, exception info and page result view models (no ASP.NET Core dependency). |
| [ViewModels.AspNetCore](src/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore/README.md) [![NuGet](https://img.shields.io/nuget/v/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.svg)](https://www.nuget.org/packages/TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore) | Server-side `ModelState` to `ApiErrorResult` / `ValidationResult` conversions for ViewModels. |

Each package has its own README (shown on nuget.org) with install command and usage.

## Releasing

Every release is a tag `vM.N.P` on a commit of `master`; the tag pushes all packages to nuget.org through CI.

```
git tag v1.2.3
git push origin v1.2.3      # triggers .github/workflows/release.yml
```

The tag must be higher than the highest version already on nuget.org. A commit after a tag builds as `M.N.P-dev.<commits since tag>`.

One-time setup on nuget.org (NuGet Trusted Publishing, no API key secret): in the account of `tqk2811`, add a trusted publishing policy with repository owner `tqk2811`, repository `TqkLibrary.Asp.ToolKit`, workflow file `release.yml`, and select the packages of this repository.
