# Odyssey.Worker

The background-job host: a .NET Worker Service in its own container, with no HTTP surface. It hosts jobs and
nothing more — every job is an `IPeriodicJob` written and tested in `Odyssey.Core`. The API never runs one.

## What lives where

| Path | Holds |
|---|---|
| `Program.cs` | Startup guards (placeholder values, time-zone data, ICU) and a warning when no key-ring path is set. It verifies no file volume itself. |
| `WorkerHost.cs` | The whole service graph (`AddOdysseyWorker`), one method so `WorkerHostTests` builds exactly what production runs. |
| `Hosting/PeriodicJobService.cs` | The loop every job runs in, and `AddPeriodicJob<TJob>()`. |
| `DemoData/` | `DemoFileSeeder` and its one-shot hosted service: the demo files and photos (Development/Testing only). |
| `Thumbnails/` | The Magick.NET renderer, its runtime setup, the coder `policy.xml` and the startup self-test. |
| `Dockerfile` | Alpine runtime with ICU and tzdata, runs as the non-root `app` user. |

## Adding a job

1. Write the job as an `Odyssey.Core.Jobs.IPeriodicJob` in `Odyssey.Core`, with its tests in `Odyssey.Core.Tests`.
2. Add one `services.AddPeriodicJob<TJob>()` line (plus the job's shared registration) in `WorkerHost.cs`.
3. If its runs belong in the shared run log, declare it once in
   [`Odyssey.Core/Jobs/JobCatalogue.cs`](../Odyssey.Core/Jobs/JobCatalogue.cs) — that file is the list of
   recorded jobs, and `WorkerHostTests` checks it against the registrations here.

## Running and testing

```bash
dotnet test Odyssey.Worker.Tests   # host graph, job loop, demo files, Magick.NET, compose parity and topology
```

The worker always uses a real MariaDB (no in-memory mode), so run it through Compose or Aspire (root
[`CLAUDE.md`](../CLAUDE.md) → *Commands*) rather than on its own.

## Rules that matter most

- **Every replica runs every job**; coordinate through rows (leases, concurrency tokens), never process state —
  [background-jobs.md](../docs/architecture/background-jobs.md).
- **The API's Data Protection key ring is mounted read-only** and key generation is off; the API is the only key
  writer — [deployment.md → Two containers hold the key ring](../docs/deployment.md#two-containers-hold-the-key-ring).
- **File volumes are verified by `FileStorageMaintenanceJob`, not at startup**, so a failing verdict holds that job
  alone; volumes and bindings must match the API's in both compose files and the AppHost
  (`FileStorageDeploymentParityTests`) — [background-jobs.md](../docs/architecture/background-jobs.md).
- **Magick.NET is referenced by this project alone, and used only in `Thumbnails/`** (`ThumbnailSourceLintTests`).
  A hung decode makes the worker exit with code 70 for its supervisor to restart it —
  [file-storage.md](../docs/architecture/file-storage.md), [deployment.md → Photo thumbnails](../docs/deployment.md#photo-thumbnails).
- **The worker shares no network with the API or the client**: it sits on its own database network and `egress`
  only (`ComposeNetworkTopologyTests`) — [deployment.md → Network topology](../docs/deployment.md#network-topology).
- **Demo files appear after the stack is healthy**: `DemoFileSeeder` waits for the API to initialise the volumes,
  gated by `DemoDataGate` — root [`CLAUDE.md`](../CLAUDE.md) → *Testing & Demo Data*.
