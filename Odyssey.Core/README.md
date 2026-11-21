# Odyssey.Core

All business logic, for both hosts: the API (`Odyssey.Api`) and the background-job host (`Odyssey.Worker`). A
controller calls a service here; a job here runs in the worker. One project, split by folder and namespace.

## What lives where

Each top-level folder is its own namespace, `Odyssey.Core.<Folder>`. Most subfolders add a segment
(`Journal/Thumbnails/` → `Odyssey.Core.Journal.Thumbnails`), but some deliberately do not
(`Journal/Calendar/`, `Journal/Lookups/`, `Finance/Lookups/` and `Finance/Queue/` stay in the module namespace) —
read the file before assuming.

| Folder | Holds |
|---|---|
| *(root)* | `Odyssey.Core` — the `DomainException` hierarchy `GlobalExceptionHandler` maps, the tag colour/icon guards |
| `Finance/` | Accounts, transactions, budgets, contracts, properties, exchange-rate sync, instruments, file analysis |
| `Journal/` | Entries, tasks, calendars, photos (originals and thumbnails), contact avatars, `.ics` interop (`Interop/`) |
| `Locations/`, `Weather/` | Places (`LocationResolver`) and weather points |
| `Internet/` | The news reader (`Internet/News/…`) |
| `FileStorage/`, `Imaging/` | The object store and storage features (`Files/` for the file areas); the one image-validation pipeline |
| `Jobs/`, `SystemSettings/` | What both hosts need: `IPeriodicJob`, `JobCatalogue`, the run log; settings resolution and the secret reader |
| `Configuration/`, `Pagination/` | Config plumbing and startup guards; the `ListQuery` clamps |
| `UserAdministration/`, `Profiles/`, `Preferences/`, `Legal/`, `FileExport/` | Account-level services |
| `Email/`, `Identity/` | Interfaces only — the implementations live in `Odyssey.Api` |

Several folders carry a `CLAUDE.md` that imports the matching rules file from
[`docs/architecture/`](../docs/architecture/); read it before changing that area.

## Where new code goes

- **A new business service goes here, never in `Odyssey.Api`** — the worker cannot reference a web project.
- **Something only a host can provide** (mail delivery, the send throttle, display-name resolution): put the
  **interface** here and the implementation in the host.
- **A background job** is an `IPeriodicJob` here, registered in `Odyssey.Worker/WorkerHost.cs` —
  [background-jobs.md](../docs/architecture/background-jobs.md).
- **Something both hosts need** is one registration extension here (`AddWeatherFetching`, `AddFileStorage`, …)
  that each host calls, never a copy.

## Testing

```bash
dotnet test Odyssey.Core.Tests   # EF InMemory, no Docker
ODYSSEY_REQUIRE_TIER=perf dotnet test Odyssey.Core.Tests --filter Category=Perf   # volume/time budgets
```

Relational behaviour (foreign keys, `CHECK`s, locking, `ExecuteUpdate`/`ExecuteDelete`) is only exercised by
`Odyssey.IntegrationTests`.

## Rules that matter most

- **Finance must never depend on Journal**; `StoredFilesSourceLintTests` fails on a new `using Odyssey.Core.Journal`
  in Finance — root [`CLAUDE.md`](../CLAUDE.md) → *`Odyssey.Core` layout*.
- **A same-named type in a module namespace silently shadows a root one**; `MergedProjectNamespaceLayoutTests`
  (`Odyssey.Api.Tests`) guards it — root [`CLAUDE.md`](../CLAUDE.md) → *`Odyssey.Dtos` and `Odyssey.Context` layout*.
- **Use tracked `RemoveRange`, not `ExecuteDeleteAsync`/`ExecuteUpdateAsync`, for anything the fast tiers must run**
  (the InMemory provider throws) — root [`CLAUDE.md`](../CLAUDE.md) → *One EF Core context, one database*.
- **Keep the lookup services** (`IContactLookup`, `IFileLookup`, …) even where a foreign key exists — same section.
- **Bytes never go in the database**; files go through `IObjectStore` and a `StorageFeature` —
  [file-storage.md](../docs/architecture/file-storage.md).
- **Settings are read through `IntSettingResolver` and the shared lookups**, never a private parse/clamp copy —
  [settings.md](../docs/architecture/settings.md).
