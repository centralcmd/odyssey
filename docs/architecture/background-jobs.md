# Background jobs and the worker

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Background jobs run in `Odyssey.Worker`, never in the API.** The worker is a host and nothing more: a
job is an `Odyssey.Core.Jobs.IPeriodicJob`, written and tested in `Odyssey.Core` like any other business
service, and registered with one `services.AddPeriodicJob<TJob>()` line in `Odyssey.Worker/WorkerHost.cs`.
`PeriodicJobService` owns the loop — first run at once, one run per interval, a failed run logged and the
next one going ahead, runs never overlapping. Rules:

- **Every replica runs every job.** A job that must not run twice at once coordinates through the rows it
  works on (a lease, a concurrency token — `WeatherScheduleJob`'s claim on `WeatherPointSchedule.Version`
  is the working pattern), never through process state. That is also why the worker has no in-memory
  database mode: a private, empty copy would coordinate with nothing.
- **Share a dependency, don't copy it.** When a job needs something the API already has, move it into
  `Odyssey.Core` behind one registration both hosts call (`AddWeatherFetching`, `AddSecretSettingsReading`).
  `WorkerHostTests` builds the production graph with `ValidateOnBuild`, so a missing registration fails a
  test rather than the container.
- **The worker reads the API's Data Protection key ring, read-only** (a product decision for issue #348):
  the keys volume is mounted `:ro` and automatic key generation is off, so the API stays the only key
  writer. Holding the ring means the worker can decrypt every stored credential — see
  `docs/deployment.md` → *Two containers hold the key ring*. The migrations job still holds none.
- **Every catalogued job records into one shared run log** (issues #409/#410). `JobRuns` (TPH by `JobType`)
  holds every run, burst and held period, and `JobHeartbeats` one row per process and job; `JobCatalogue`
  (`Odyssey.Core/Jobs/`) declares each recorded job once, and `JobRunRecorder` is the only writer a job calls.
  `Generation` is rotated on every save, so the worker, the stale sweep and the API lose a race cleanly; the
  `ExclusiveGuard`/`OpenHeldGuard` generated unique indexes are the real guards on MariaDB. `JobRunMaintenanceJob`
  sweeps stale rows every 5 minutes and prunes past `JobRunRetentionDays` hourly. The Admin page is
  `/background-jobs` over `GET/POST /api/background-jobs/...`, gated on `system-settings.read`/`.update` plus
  each job's own claims, which never leave the server.
- **The worker mounts and verifies the file volumes** (issue #346 A3). `FileStorageMaintenanceJob` (ticking
  every 30 s, one pass per 1 h `PassInterval`) erases released files, sweeps abandoned writes, scrubs integrity and reconciles the disk, elected per tick
  by the single-row `FileStorageMaintenanceLeases` lease. **The job runs the worker's storage checks itself**
  (`FileStorageWorkerVerification`, the API's checks in verify-only mode, waiting up to 2 minutes for the API to
  initialise a new volume), so **a failing volume verdict holds that job alone** — weather and exchange-rate
  sync keep running, and `Program.cs` verifies nothing. Because the container no longer exits, the verdict is
  the signal: each worker process writes its **own** row in `FileStorageWorkerVerdicts` (a check name, never
  a path) — never a shared slot, which one healthy replica would clear for another — refreshed on every
  10-minute re-check, deleted when its checks pass, and ignored after 30 minutes unrefreshed. The panel calls
  clean-up *stopped* only when no pass has completed since the failure; otherwise another worker is doing it.
  The panel's thresholds (2 h overdue, 3 h stale, 10 min re-check) travel on the status DTO — never restate
  them in the client.
  `api` and `worker` must get identical `FileStorage__Volumes__*`/`__Bindings__*` and mounts in both
  compose files and the AppHost; `FileStorageDeploymentParityTests` enforces it.
- **Statement extraction is a queue, not a request** (issue #444). `POST …/analyze` only enqueues (`202` +
  `Location`); `FileAnalysisQueueJob` claims one job per tick per process, re-checks requester, file, uncached
  settings, destination, disclosure, template hash, credential and the kill switch (last) **before** it stamps
  `TransferStartedAt` and sends, and never re-sends: a lost lease is `Failed / interrupted`, an unclaimed row
  `Failed / not_started`. `DocumentSent` is derived from that stamp alone. The sweep also runs inline in the API
  for the one file or job a request touches, and under `Testing` the API runs the job inline. It has no manual
  run (`ManualRunSupported: false` → `409 job-run.manual-run-unsupported`). Match and import stay synchronous.
  **AI recipe import has its own queue with the same shape** (`RecipeImportQueueJob`, `JobType` 13, issue #602) and its own
  named client and circuit breaker, so neither flow waits behind or trips the other; `RecipeImportRetentionJob` (14) purges
  finished imports hourly and may be run on demand by a `recipes.import` holder. See [recipes.md](recipes.md).
- **Settings reach the worker within the cache TTL, not instantly.** A `/settings` save evicts the API's
  `IMemoryCache`, not the worker's, so a changed value applies to jobs on the next read after the 30 s TTL.
  A setting that must bind on the very next job run is read uncached, as `FileAnalysisEnabled` is.
