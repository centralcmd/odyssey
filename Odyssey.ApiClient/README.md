# Odyssey.ApiClient

The typed HTTP client for `Odyssey.Api`, shared by every .NET consumer: the Blazor client (`Odyssey.Client`) and
the API-over-HTTP test tier (`Odyssey.E2ETests.Api`). A plain `Microsoft.NET.Sdk` library that references
`Odyssey.Dtos` and nothing else in the solution.

## What lives where

| Path | Holds |
|---|---|
| `IOdysseyApi.cs` | The transport core (`OdysseyApi`) every resource client sends through. |
| `ApiResult.cs`, `ApiProblem.cs` | `ApiResult` / `ApiResult<T>` (value, status, problem) and the parsed `ProblemDetails`. |
| `ApiFile.cs` | `ApiUpload` (what an upload takes), `ApiFile` (what a download returns) and the export wrappers. |
| `PagedQuery.cs` | The list-query builder, with `Sort(string key, bool ascending)`. |
| `Auth/` | `AuthApiClient`, the session model and `AntiforgeryHandler` (attaches `X-XSRF-TOKEN`). |
| `Resources/` | One typed client per API resource (`AccountsApiClient`, `PhotosApiClient`, …). |
| `Contracts/` | Request/response models the auth endpoints use that are not DTOs. |
| `ServiceCollectionExtensions.cs` | `AddOdysseyApiClient()`: registers the transport, the antiforgery pipeline and every typed client. |

## Using it

`AddOdysseyApiClient()` does not register the `HttpClient` — its base address and how it carries the auth cookie
differ per host:

- **Browser (`Odyssey.Client/Program.cs`):** `BrowserCredentialsHandler` (WASM-only, lives in the client) in front
  of `AntiforgeryHandler`.
- **Anything else** (a console app, a test fixture): an `HttpClientHandler { UseCookies = true }` in front of
  `AntiforgeryHandler`. `Odyssey.E2ETests.Api/ApiStackFixture.cs` is the working example.

A new resource client goes in `Resources/` with its interface, one line in `ServiceCollectionExtensions.cs`, and
a test in `Odyssey.ApiClient.Tests`.

## Testing

```bash
dotnet test Odyssey.ApiClient.Tests   # HttpMessageHandler stubs, no server
```

## Rules that matter most

- **No web/UI dependency**: never MudBlazor, `Microsoft.AspNetCore.Components.*` or the WebAssembly packages, and
  none of their types in a public signature — root [`CLAUDE.md`](../CLAUDE.md) →
  *`Odyssey.ApiClient` stays free of web/UI dependencies*. Check with:

  ```bash
  grep -rnE "^using (MudBlazor|Microsoft\.AspNetCore\.Components)|IBrowserFile |ISnackbar " Odyssey.ApiClient/ --include=*.cs
  ```

- **Return results, never present them**: a failure becomes a toast at the page, through `Odyssey.Client`'s
  `ApiInteropExtensions` (`OrToast`, `ValueOrToast`, …) — same section.
- **Uploads take `ApiUpload`, not `IBrowserFile`**; the client adapts with `file.ToApiUpload(maxSize)` — same section.
- **The `OdsTableSort` overload of `Sort` is a client extension** (`Odyssey.Client/Services/PagedQueryOdsExtensions.cs`),
  not part of this library — same section.
- **Never hold a server cap as a constant here or in a consumer**; read it from the limits endpoints
  (`UploadLimitsApiClient`, `ImportLimitsApiClient`) — root [`CLAUDE.md`](../CLAUDE.md) → *Cross-cutting rules*.
