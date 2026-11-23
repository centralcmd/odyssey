# Upgrade notes

What an operator must know, or do, when upgrading **across** a particular release: data migrations,
one-time steps, renamed or retired configuration, wire changes and the order to deploy in. How a deployment
works now is in [`deployment.md`](deployment.md); this file is the history of how it got there.

Read every section between the version you run and the one you deploy, **newest first**, before you change
`IMAGE_TAG`. The rules that hold for every upgrade are in
[Deploying / upgrading](deployment.md#deploying--upgrading) and [Rollback](deployment.md#rollback): take a
database dump first, migrations are forward-only, and a data migration is undone only by restoring that dump.
A new or revoked permission claim never needs a sign-out
([why](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)).

Versions are those in [`CHANGELOG.md`](../CHANGELOG.md). A note whose change predates the changelog (which
starts at 0.16.0) is filed under *Before 0.16.0*.

## Unreleased

### Migration history rebaselined at v0.51.0: upgrade to v0.51.0 first

The 95 migrations up to and including v0.51.0's `20261122104734_AddRecipes` were folded into one baseline that
keeps that id (issue #581, [`migration-rebaseline.md`](migration-rebaseline.md)).

- **A database on v0.51.0 needs nothing.** Its history already holds the baseline's id, so the migrations job finds
  nothing to apply and changes nothing. The old history rows stay; they are harmless.
- **A database older than v0.51.0 is refused** by the migrations job, which names v0.51.0 as the release to upgrade
  through. Deploy v0.51.0, let its migrations job complete, then deploy this release. Nothing is changed by the
  refusal.
- **Before deploying**, take a dump and run `--schema-fingerprint` against each database (the runbook's step 4);
  after deploying, run it again. Both diffs should be empty. Run the mode from **this release's** build or migrations
  image: v0.51.0 has no fingerprint mode, and a build from before this release prints its output in a different
  normalisation that does not diff against the committed file.
- Gone with the old chain: the one-time location conversion, the non-positive exchange-rate pre-check and the
  property/vehicle account retirement report. All three ran during upgrades that every supported database has
  already completed.

### Sign-in broke behind the client nginx at 0.50.0: larger request-header buffers

0.50.0 added 18 permission claims (books, recipes and their tags, podcast audio). The Identity auth cookie carries
every claim, so it grew past nginx's default request-header buffer of `4 8k` — about 8.3 KB for Admin and 7.6 KB
for Owner before any other cookie. The client nginx then answered **every** request after sign-in, static files
included, with `400 Request Header Or Cookie Too Large`. Sessions already signed in broke within a minute of the
deploy, when the security-stamp check rewrote their cookie with the new claims.

- **The shipped client image now sets `large_client_header_buffers 4 32k;`.** Pull the new image; nothing else to do
  with the shipped compose files and `Caddyfile` (Caddy's own header limit is far larger).
- **If you run your own reverse proxy in front of the client or the API**, raise its request-header limit to match:
  at least 16 KB for a single header line, 32 KB for all headers. 32 KB is Kestrel's `MaxRequestHeadersTotalSize`,
  so more buys nothing. In nginx that is `large_client_header_buffers 4 32k;` in the `server` (default server) or
  `http` block that receives the browser's request.
- This only buys headroom: each new module adds roughly 8 claims. A structural fix that stops the cookie growing
  with the claim catalogue is tracked in issue #583.

### API documentation moved to Scalar; `SWAGGER_ENABLED` became `API_DOCS_ENABLED`

Issue #569 replaced Swashbuckle with the built-in OpenAPI generator and Swagger UI with Scalar.

- **New URLs:** the reference is `/scalar/v1` and the OpenAPI 3.1 document is `/openapi/v1.json` (through the
  client's proxy, `/api/scalar/v1` and `/api/openapi/v1.json`). `/swagger` serves nothing; there is no redirect.
- **`SWAGGER_ENABLED` / `Swagger:Enabled` is no longer read**, and nothing falls back to it. Set
  `API_DOCS_ENABLED` (`ApiDocs:Enabled`) instead. On Compose the default is now `false`; a stack run as
  Development still serves the docs whatever it says, but any other non-Production stack needs
  `API_DOCS_ENABLED=true`. A host that still sets the old key logs one warning naming the new one at startup.
- **New: `API_DOCS_REQUIRE_LOGIN` (`ApiDocs:RequireLogin`), default `true` everywhere, Development included.**
  Sign in to the client first, then open the docs; without a session they answer `401`. Set it to `false` for
  the previous anonymous behaviour. A value other than `true`/`false` stops the API at startup, in every
  environment.
- **Production is unchanged:** the docs are never served there.

### Legacy file storage removed (`RemoveLegacyFileBlobStorage`)

Issues #428 and #448 moved every photo original and file out of the `FileBlob` table with two worker jobs (the
photo-original move and the file move). The migration **`RemoveLegacyFileBlobStorage`** then removes the jobs,
their endpoints and tables, `FileBlob`, `FileMetadata.FileBlobId` and `Photos.FileId`.

- **Upgrading from a release that still had the moves:** the migration **refuses to run** — before any drop, since
  MariaDB commits DDL implicitly — while any photo still has `FileId` set, any file still has `FileBlobId` set, or
  `FileBlobBackfillFailures` has a row; the error names which. Roll back to the previous release, let the moves
  finish on its `/background-jobs` page, and upgrade again.
- **It destroys data, and `Down()` restores schema only.** The run history of the two moves (`JobType` 5 and 7) is
  deleted; those ordinals and `JobHeldReason` 6 (`BackingOff`) are retired for good. The only undo is restoring the
  database backup taken before the upgrade.

## 0.48.0

### Fetched exchange rates are stored in one direction (issue #472)

This ships in **two releases, and their order is the whole safety argument.** Nothing in the code enforces
it, so check it before upgrading.

**Release 1 (0.46.0, PR #478) — readers.** Every conversion, status and `/latest` read now uses a fetched
`X → NOK` row as `1 / Rate` for `NOK → X`; manual rates are never inverted. The sync still stores both
directions, and on a tie the stored row wins, so results are unchanged. It adds two migrations:
`CK_ExchangeRates_RatePositive` and a `(From, To, Provider, AsOf)` index. **If any `Rate <= 0` row
exists, the migration service stops before migrating** and names up to 20 row ids; correct each rate
or delete the row, then start it again. Also new: `GET /api/exchange-rates/resolved`, and `/latest`
answers a malformed currency code with `400` (it used to be a `404` echoing the input). An inverted
`/latest` response carries the id and dates of the opposite pair's stored row.

**Release 2 (0.48.0, PR #480) — storage.** The sync stores only the published `X → NOK` row, and the
`StoreFetchedRatesInPublishedDirectionOnly` migration deletes every fetched `NOK → X` row that has its
fetched `X → NOK` partner on the same date. Manual rows and partnerless fetched rows are kept.

1. **Deploy release 1 everywhere first.** A release-1 reader is what keeps `NOK → X` conversions
   working once those rows are gone. An API or worker still on an older release would find no
   `NOK → X` rate after the migration.
2. **Take a database backup before upgrading to release 2.** This is a data migration. Its `Down`
   re-derives the deleted rows from their partners, so it is **approximate**: a rate can differ in
   the last decimal where the published value needed more than eight. The backup is the exact restore.
3. **Stop the API and the worker, run the migration, then start them.** A release-1 worker left running
   through the migration may write `NOK → X` rows again; they are harmless (readers prefer the stored
   row on a tie, which is today's value) but are not removed until the next purge of that currency.
4. **Expect the numbers to halve:** a sync run's inserted rows, and the purge preview's fetched-row
   count. The Exchange rates list stops showing fetched `NOK → X` lines; the Inverse column already
   shows the reciprocal.
5. **A warning to know about:** `Exchange-rate sync stored N rounded XXX rate(s) between … and …`
   means Norges Bank published a value needing more than eight decimals, which it has not done so far.
   The stored rate is rounded, so its reciprocal is off in the last place.

## 0.46.0

### News reader (issue #470)

- **A new storage binding.** The `NewsArticles` feature keeps every article's source item, plain text and images on the
  file store, under `news/<feedId>/<bucket>/<articleId>/<storedObjectId>.<ext>` (opaque names: no publisher or user text
  is ever in a path). Both compose files' base and the AppHost bind it to `primary`. **An operator with their own
  bindings must add `FileStorage__Bindings__NewsArticles` to both `api` and `worker`**, or startup check B1 fails with
  `UnboundFeature`.
- **The worker image gains ICU** (`icu-libs`, `icu-data-full`) and asserts it at startup (`GlobalizationGuard`, now in
  `Odyssey.Core` and called by both hosts): the API and the worker must map an IDN feed host to the same punycode key.
- **The compose network topology changed** — see [Network topology](deployment.md#network-topology). If you run your own compose overrides that
  attach networks, read it before upgrading.
- **The worker gets `stop_grace_period: 30s`**, so a deploy stops its fetches cleanly (leases cleared, nothing counted
  as a crash). Keep it if you write your own compose file.
- **Nine settings** under **`/settings` → News** (`NewsDefaultFetchIntervalMinutes`, `NewsMaxFeeds`, the image and text
  quotas and caps, and `NewsPrivateHostAllowList`, which needs `system-settings.security.update`). They reach the worker
  within the 30 s settings TTL; the allow-list is read uncached, so a revoked grant binds on the next fetch.
- **Rolling back** past this release drops every news table (the migration's `Down()` deletes the job's run-log rows
  first). The news `StoredObjects` rows remain and log the B4 Critical ("a live row of an unknown feature"). Delete every
  feed and wait out the deletion grace (24 h by default, at most 168 h) **before** rolling back, so the files are gone too.

### Task and journal `.ics` files carry their attachments, and their limits rise

`RaiseTaskIcsLimits` and `RaiseJournalIcsLimits` raise the task and journal import/export defaults to
10240 MB and unlimited counts (only where no administrator saved a value). A deployment whose `.env` still pins
`FILE_UPLOAD_MAX_BYTES=67108864`, or whose own reverse proxy still caps bodies at 65 MB, keeps the old ceiling:
a large import then fails at the proxy with an HTML `413` although the settings page offers 10 GB. Raise
both (outermost first), or lower the import/export sizes at `/settings` to match.

### Calendar `.ics` streams (issue #516)

- The migration moves the calendar import and export settings to **no event-count limit and 10240 MB**,
  only on rows no administrator has saved.
- Rolling the migration back restores the old settings rows and drops `ImportSourceUid`, the key that makes a
  second import of another calendar's export update rather than duplicate.

### Calendar recurrence keys and time zones (issue #519)

- Existing events stay floating and display exactly as before until they are edited with a zone; floating
  times now export without `Z`.
- `RecurrenceKeyBackfillJob` in the worker keys every series written before this release, detaching duplicate
  or rescheduled rows into standalone events (see
  [Calendar recurrence keys and time zones](deployment.md#calendar-recurrence-keys-and-time-zones-issue-519)).
  A past deletion in a backfilled legacy series is not remembered, so such a series exports flat.
- The import now sorts the file by UID on temporary disk first; budget the free space by the formula in the
  reference section.
- `removeCancelled` is renamed `allowRemovals`, and it now also gates discarding a series' edits.
- Every host retries MariaDB deadlocks (`1205`, `1213`).
- A tzdata change moves no stored instant; a series' future rows follow it on its next write.
- **`Down()` loses every exclusion, override flag, key and zone, and leaves detached rows detached** — only a
  backup undoes it.

### Calendar attachments (issue #521)

- New setting `CalendarEventMaxLinksPerKind` (default 50, range 1–100).
- Rolling the `CalendarEventAttachments` migration back drops the links; the files and photos stay.

## 0.45.0

### Statement analysis runs in the worker (issue #444)

Extraction moved out of the API request and into `Odyssey.Worker`. What an operator needs to know:

- **The worker now needs egress to the analysis provider** (the base URL host, `api.anthropic.com` by
  default). The API still needs it for the match step. Neither compose file restricts egress today; a
  deployment that firewalls containers must open it for both.
- **`POST /api/accounts/{id}/files/{fileId}/analyze` answers `202 Accepted`** with a `Location` to
  `GET /api/file-analysis/{jobId}`, which a client polls (it sends `Retry-After: 2` while the job is open and
  `Cache-Control: no-store` always). A second request while a job is open on the file is a `409` with
  `code: analysis_in_progress` and the open job's `analysisJobId`. Third-party consumers that read
  candidates from the old `200` body must poll instead.
- **A statement above 32 MiB is refused with `422`** (`code: file_too_large_for_analysis`, `limitBytes`),
  a fixed ceiling, before anything is queued.
- **`FileAnalysis:PromptVersion` and `FileAnalysis:PromptTemplatePath` are retired.** The prompt is embedded
  in the build and every job records its SHA-256; a configured value is ignored.
- **`FileAnalysis:TimeoutSeconds` must be 10–140** and is validated at startup in both the API and the
  worker; keep the two equal (the shipped `appsettings.json` files are).
- **An unset API key now refuses before sending.** A gateway that authenticates by network position
  rather than by key still needs *a* key stored — any placeholder value — or every job fails with
  `provider_credential_error` and nothing is sent.
- **Nothing is ever re-sent automatically.** A worker that dies mid-call leaves the job
  `Failed / interrupted`, reported to the user as "possibly sent"; a job no worker claims within 15
  minutes fails as `not_started`, "not sent". Jobs left `Running` by the old synchronous path when you
  upgrade are failed the same way by the first sweep.
- The run log at `/background-jobs` gains a **File analysis** entry (counts of completed, failed and
  refused analyses). It has no *Run now*.

### File-analysis matching sends more, and the vocabulary cap is gone (issue #433)

- **The match vocabulary cap is retired.** The `FileAnalysisMatchMaxVocabulary` setting (default 500) no
  longer exists, and matching is never skipped for vocabulary size: every active contact and tag is sent.
  Rolling the migration back re-inserts the row at **500**, not at an administrator's previous value.
- **Matching sends contact aliases and tag descriptions** for analyses started after the upgrade. Alias
  labels, notes and organisation numbers are still never sent. An analysis started before the upgrade
  keeps matching on names only for its whole life.
- **Users consent again once.** The consent disclosure now lists the data fields sent and whether
  administrator guidance is in force, and its version covers both.
- **Two new settings, `FileAnalysisExtractionPromptAddition` and `FileAnalysisMatchPromptAddition`**,
  hold administrator guidance (up to 4000 characters each, empty by default) that is added to the
  extraction and match prompts. Each change is logged with the acting user, the length and a digest,
  never the text. Turning either on or off makes users consent again; turning **match** guidance on or
  off, or changing the processor, region, lawful basis or privacy-notice URL, also refuses matches on
  analyses that are already open (`409`, "start a new analysis").
- **Two new deploy-time limits**, per user: `RateLimiting:FileAnalysisAnalyze` (default 10 per hour) and
  `RateLimiting:FileAnalysisMatch` (default 30 per hour). A household importing many statements at once
  may want to raise the analyze limit. Like every `RateLimiting:*` key they are configuration, not
  settings, because the limiter is cached per user.
- **A payload budget** of 200 000 characters bounds each match request: over it, aliases are left out
  for that run, then tag descriptions. Names are always sent, and are never shed: a household whose
  names alone exceed the model's context gets a `Failed` match (review the candidates by hand), not a
  partial one.
- **Wire change for API consumers.** `fileAnalysisMatchMaxVocabulary` is gone from the system-settings
  DTOs, and `maxVocabulary` from the file-analysis payloads. The web client in this release no longer
  reads either; a third-party consumer that did must drop them. Matches refused by the new limits
  answer `429` with `Retry-After`, and the destination refusal answers `409` with
  `code: file_analysis_destination_changed`.

## 0.44.0

### The exchange-rate sync's run log moved (data migration, issue #409)

Two migrations run in order, both automatically before the API starts:

1. **`AddJobRuns`** validates every `ExchangeRateSyncRuns` row against the new table's constraints first — a
   row that cannot be copied aborts the migration *naming its id*, with nothing created — then creates
   `JobRuns`, `JobHeartbeats`, the `PhotoThumbnailJobs.ManualRequeues` column and the `JobRunRetentionDays`
   setting (90 days), and copies every row across **with its id** (`INSERT … WHERE NOT EXISTS`, so a replay
   after an interruption copies nothing twice).
2. **`DropExchangeRateSyncRuns`** checks that every legacy row is in `JobRuns` with an equal status, trigger,
   timestamps and row counters, aborts (dropping nothing) if one is not, and only then drops the old table.

Because MariaDB DDL is not atomic, they are two migrations on purpose: an interruption in the first leaves the
source intact. If either aborts, follow [`migration-history-drift.md`](migration-history-drift.md).

**Rollback is a backup restore.** `Down()` restores schema, not rows: rolling back past
`DropExchangeRateSyncRuns` recreates an *empty* `ExchangeRateSyncRuns`. Take the usual pre-upgrade database
dump. The existing exchange-rate sync endpoints and the Currencies page keep their wire shape.

**Deploy order: migrations, then the worker and the API together.** A worker from before this release would
write into a table that no longer exists, and an API from before it would read one; both images come from the
same release tag, so the normal `pull` + `up -d` does this.

## 0.43.0

### Unified locations (issue #380)

- **Take and verify a backup first.** The upgrade converts every contact address, every property
  address and every free-text place on calendar events, recurring series and journal entries into
  locations, once, and drops the old address columns in the same release. No copy of the old values is
  kept, so **rollback is a database restore** — a migration `Down()` restores schema, not rows. The
  migration service logs the conversion's row counts at start and its created and linked counts at the
  end (counts only).
- **A contact address with no usable text is removed.** An `Addresses` row whose six fields are all
  empty once control characters and whitespace are cleaned has no address to link to a place. The
  conversion deletes such rows before the contract migration and logs how many (a warning, count only);
  the contact itself is kept. This is an accepted loss, chosen so the upgrade never stops on it.
- **Link-like values stay text.** A place that looks like a meeting link or carries a passcode is never
  turned into a location (locations are readable by every `locations.read` holder, Guest included).
- **New claim `locations.delete`** (Admin, Owner, User; not Guest). It reaches live sessions within a
  minute ([no sign-out needed](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)).
- **Converted places have no coordinates** until someone geocodes them on the Locations page.
- **An event or entry at a place linked to a weather point uses that point** (issue #387), with no
  fallback to the caller's preference or the household default even when the point's forecast is off; a
  place with no link falls through to the preference and then the household default.
- **`locations.read` sees every address in the app** (product decision 3), including those that used to
  sit behind `contacts.read` or `properties.read` alone.

### Weather points (issue #387)

- **Back up the database before deploying.** Two migrations, `WeatherPointsExpand` and
  `WeatherPointsContract`, turn every per-place weather row into a point **under the same id**, re-key
  every weather table onto it, link each place to its point, delete orphaned weather rows, rename the
  settings keys `WeatherDefaultLocationId` → `WeatherDefaultPointId` and `WeatherMaxTrackedLocations` →
  `WeatherMaxTrackedPoints` (values kept) and rewrite each weather preference's field. They are data
  migrations and **not replay-safe**: `Down()` restores the schema only, and a failure is fixed forward,
  with the backup as the fallback (see [`migration-history-drift.md`](migration-history-drift.md)).
- **Stop `api` and `worker` before the migrations job runs** when upgrading a running stack: an old
  worker against the contracted schema fails every tick. Compose and the AppHost already order a fresh
  start correctly.
- **Renamed claims.** The three Admin weather claims are now `weather.points.read`,
  `weather.points.update` and `weather.points.delete`. `RoleClaimSeeder` adds the new values and removes
  the old ones; an existing session picks the new values up at its next security-stamp revalidation —
  within about **one minute**, without signing out — and until then the point endpoints answer `403`.
- **Linking** a place to a point needs `locations.update` and `weather.read` (Admin, Owner, User). It
  sends nothing to MET; only an Admin creates or moves a point.
- `/api/weather/locations` is gone; points are `/api/weather/points`, and a place's link is
  `PUT /api/locations/{id}/weather-point`.

## 0.42.0

### File storage on filesystem volumes (issue #346)

- **A third named volume, `files_primary`**, is mounted read-write into **`api` and `worker`** at
  `/var/odyssey/files/primary`, with `FileStorage__Volumes__primary__RootPath` set on both. Both come
  from the base `docker-compose.yml`, so the production overlay needs no edit. `docker compose pull` and
  `up -d` create the volume, and the API **initialises** it on first start: it writes
  `.odyssey-volume.json` and records the volume in the new `FileStorageVolumes` table. The worker waits
  (up to 2 minutes) for the API to do that, then verifies it.
- **One migration**, `AddFileSystemObjectStorage`: four new tables (`FileStorageVolumes`,
  `StoredObjects`, `FileStorageOrphanSightings`, `FileStorageMaintenanceLeases`) and one settings row.
  Schema only, additive; `Down()` drops all of it.
- **One new setting**, **System settings → Storage → Deletion grace period** (default 24 h, 1–168 h). It
  takes the security claim and every change is audited.
- **One new claim**, `file-storage.read`, Admin only, reconciled onto the role by the migrations job. It
  gates **System settings → Storage health**. It reaches a signed-in administrator within a
  minute ([no sign-out needed](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)).
- **A file volume that fails verification in the worker stops file clean-up only.** Weather and
  exchange-rate sync keep running, and the worker container stays up — so `docker compose ps` no longer
  shows the fault. The Storage health panel does: *File clean-up is stopped: the worker's storage check
  failed*, naming the check. The worker re-runs the checks every 10 minutes and resumes on its own once
  they pass. Once a release adds a storage feature (photo thumbnails), a worker with no `FileStorage__*`
  configuration at all reports the same way.
- **A second migration**, `RecordFileStorageWorkerVerdicts`: one new table, `FileStorageWorkerVerdicts`
  (a row per worker whose storage check is failing). Schema only, additive; `Down()` drops it.

### Photo thumbnails (issue #362)

- **One migration**, `AddPhotoThumbnails`: five tables (`PhotoThumbnails`, `PhotoThumbnailJobs`,
  `PhotoThumbnailBuckets`, `PhotoThumbnailIncidents`, `PhotoThumbnailWorkerHeartbeats`) and the one bucket
  row. Schema only, additive; `Down()` drops all of it (the files are then unreferenced and the hourly
  sweep erases them).
- **The backfill runs on its own.** The worker queues every existing photo at background priority, 500
  per pass, behind new uploads. Until a photo's two thumbnails exist the client shows the original, exactly
  as before.
- **A new native dependency, ImageMagick (Magick.NET), in the worker image only.** The API never loads it;
  `Odyssey.Worker.Tests` fails if any other project references it.
- **Worker container limits**, in both compose files: `mem_limit: 1g`, `pids_limit: 256`, and `/tmp` as a
  64 MB `tmpfs`. ImageMagick itself is capped at 320 MB of pixel cache with disk spill off.
- **`FileStorage__Bindings__PhotoThumbnails=primary`** on `api` and `worker`. Keep the two identical.
- **No new claim and no new setting.** Thumbnails are served by `GET /api/photos/{id}/thumbnail` under
  `photos.read`. The worker state on `GET /api/photos/stats` is filled only for `system-settings.read`.
- **Renaming a photo re-renders its thumbnails**, because the stored files carry the name. (A Files-store rename once
  did too, for a photo that still wrapped a file, with a `409 rename-busy` on a collision; that went with the legacy
  photo shape — see [Legacy file storage removed](deployment.md#legacy-file-storage-removed).)

## 0.41.0

### Household locations (issue #350)

- **New claims.** `locations.read` (every role, Guest included), `locations.create` and
  `locations.update` (Admin, Owner, User). Issue #380 later added `locations.delete` (0.43.0). They reach live sessions
  within a minute ([no sign-out needed](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)).
- **Address search sends text to a third party** — see [The geocoding data flow](deployment.md#the-geocoding-data-flow).
- **The runtime image needs `tzdata`.** The API Dockerfile installs it, and the API refuses to start
  if `Europe/Oslo`, `Europe/Kyiv`, `America/New_York` or `Etc/UTC` does not resolve. If you build your
  own image, keep it.
- Two new deploy-time limits: `RateLimiting:LocationSearch` (per user, default 30 per 60 s) and
  `RateLimiting:LocationSearchGlobal` (whole instance, default 120 per 60 s).

### Weather (issue #348)

- **New claims.** `weather.read` and `weather.preferences.update` (every role, Guest included), and
  three Admin-only management claims — renamed by issue #387 to `weather.points.read`,
  `weather.points.update` and `weather.points.delete` (see 0.43.0 above).
  They reach live sessions within a minute ([no sign-out needed](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)).
- **Nothing is fetched until an administrator sets it up.** MET's terms require a contact in the
  `User-Agent`, so set **`/settings` → Weather → Contact for MET Norway** (an email address or an
  `https://` URL). Until then every weather read reports *not configured* and no request leaves the
  deployment. Then turn weather on per place on the Locations page.
- **Observations are optional.** They need a Frost client id (free, from `frost.met.no`), entered in
  **`/settings` → Weather** as a secret. Without it, past days show the forecast only. An id that
  cannot be decrypted (a lost Data Protection key ring) stops observations and logs an error hourly.
- Four new settings: the contact, the refresh interval (default 1440 min, i.e. once a day; 30–1440), the cap on scheduled
  places (default 5, 1–20) and the household default place. No configuration keys are added or moved.
- Two new deploy-time limits: `RateLimiting:WeatherRead` (per user, default 120 per 60 s) and
  `RateLimiting:WeatherRefresh` (per user, default 6 per 300 s, covering refresh, history purge and
  point delete).
- **A new container, `worker`** (image `odyssey-worker`), runs the schedule: once a minute, at most one
  MET request per second. It is where every background job runs from now on; it has no port and serves
  nothing. Every worker replica runs the schedule, and a lease on each place keeps two from fetching the
  same one. The API still fetches on demand and for the Admin refresh.
- **The worker mounts `dataprotection_keys` read-only** so it can read the Frost client id. That makes it
  the second container able to decrypt every stored credential; see
  [Two containers hold the key ring](deployment.md#two-containers-hold-the-key-ring).

### Exchange-rate sync (issue #349)

- **Existing direct rates can lose to a NOK hop.** A pair with no direct rate now converts through NOK
  (`A → NOK → B`), and when both exist the one dated later wins — the direct rate keeps the tie on the
  same UTC date. So a hand-entered `USD → EUR` dated 2025 stops being used once USD and EUR sync. That
  is intended.
- **A date-only manual rate loses to the same day's fetched rate.** Fetched rates are stamped at 16:00
  Norwegian time (14:00 or 15:00 UTC); a manual rate entered for a date is stamped 00:00. To override a
  fetched day, enter a manual rate with a later time.
- **Future-dated rates now count only from their date** and list with the status *Upcoming*. Before,
  a rate dated next week was already used for conversions.
- **Fetched rates cannot be edited or deleted one by one.** `PUT` and `DELETE /api/exchange-rates/{id}`
  on one are a `422`, whoever asks: the sync never refetches a day removed from inside what it already
  holds, so a single delete would leave a gap that conversions silently bridge with an older rate.
  Fetched rates leave per currency, through *Delete fetched rates* once its sync is off (Admin). Owner
  keeps editing and deleting manual rates.
- **Rolling back the migration** (`AddExchangeRateSync`) keeps the rows but drops the column that says
  they were fetched, so they then read as manual. `Down()` restores schema, not provenance.
- **No new claims**, so nobody needs to sign out. No new configuration: the start date and interval are
  System settings, and nothing is carried over from the environment (both keys are new).
- **The sync runs in `Odyssey.Worker`**, like every background job. Without a running worker nothing is
  fetched: *Run now* still records a `Running` run, which no process takes; the next run or purge
  started after two minutes marks it *Abandoned* and proceeds, logging a warning that asks whether the
  worker runs. The API reaches Norges Bank too — for the list of published currencies behind the
  Sync column on Currencies — so both containers need egress to `data.norges-bank.no`.
- **Both runtime images need `tzdata`** for `Europe/Oslo`, and both Dockerfiles already install it.
  Without it the sync runs fail with *Time zone unavailable*; the hosts still start.

## 0.37.1

### Currencies in use can no longer be deleted (issue #241)

**Take a database backup before upgrading.** `AddCurrencyForeignKeys` writes data, and its `Down`
restores the schema only.

**What the migration does.** It adds a `RESTRICT` foreign key to `Currencies` from every
currency-code column: accounts, transactions, budgets, tax statements, properties, account and property
estimates, and contract terms. Before this release a currency could be deleted while those rows still
named it, and rows like that would make adding the keys fail. So first, every code that rows use but
`Currencies` no longer holds is **restored** as an active currency named
`Restored currency XYZ (review)` with two minor units. No referencing row is changed or removed.
Every orphaned code is restored, including a blank or malformed one a hand edit may have left, because
skipping it would stop the upgrade at the key it violates.

**What to review afterwards.** On the Currencies page, search for `Restored currency`. For each row,
either correct its name, symbol and minor units, or change the records that use it to another
currency and then delete it. From now on, deleting a currency that is still in use is refused with a
`409` naming how many of each kind of record use it.

**Plan for write downtime.** MariaDB can add a foreign key only by copying the whole table
(`ALGORITHM=COPY`), which blocks writes to it while the copy runs. All eight tables are rebuilt,
`Transactions` among them. On a large database, upgrade during a quiet window.

## 0.35.0

### Account terms moved onto contracts (issue #190)

**Take a database backup before upgrading.** The `MoveAccountTermsToContracts` migration moves data
and then drops a column, it has **no immutable record** of what it did (a migration step cannot write
to the application log, and its working table is dropped at the end), and its `Down` restores the
schema only. The backup is the recovery path.

**What the migration does.** Every term recorded directly on an account is moved, row for row and with
the same `TermId`, onto a contract the account takes part in. The expected contract type is `Deposit`
for an asset account, `Loan` for a liability and `Other` for an unclassified one. If the account is a
party (in any role) on exactly one contract of that type, and none of the moving terms shares a
`(label, effective date)` with a term already on it, that contract is reused untouched. Otherwise a
new contract is created from the account — name, description, account number as reference, opened and
closed dates, archived state — **unsigned (Draft)**, with the account as `Object` and its custodian, if
any, as `Custodian` / `Lender` / `Other`. Accounts without terms are not touched.

**What to review afterwards.** Each moved account gets one system event on its contract, titled
*Terms migrated from account "…"*, listing the codes below that apply. It is an ordinary, editable
event — a to-do list, not an audit record.

| Code | Meaning | What to do |
|---|---|---|
| A1 | The contract was created by the migration | Review it; mark it Ready and Signed with the real dates. Until signed it is Draft and **excluded from the run rate**. |
| A2 | Two or more matching contracts existed | Move the terms to the right one and delete the created contract, or keep it. |
| A3 | The account is a party on a contract of another type | Check whether the terms belong there. |
| A4 | An unclassified account became an `Other` contract | Choose the real contract type (party roles may need changing first). |
| A5 | The account had no custodian | Add the institution as `Custodian` / `Lender`. |
| A6 | The reference number was copied from the account number | Replace it with the agreement's reference, if different. |
| A7 | The account's closed date precedes its opened date | Set the contract's end date. |
| A8 | A percentage term on a Deposit was flipped to `Incoming` | Verify — a negative rate or a percentage **fee** should stay `Outgoing`. |
| A9 | The one matching contract already had a series with the same label and date, so a new contract was created | Decide which contract the series belongs on; move or delete the duplicate. |
| A10 | The reused contract now exceeds the per-contract term cap | New terms are refused (`422`) until it is below the cap; edits still work. |
| A11 | The reused contract is archived | Its terms are hidden with it; unarchive or move them. |
| A12 | An amount's currency was filled from the account, and that currency is inactive | The term cannot be re-saved until the currency is active or changed. |
| A13 | The account also has value estimates | Informational — estimates stay on the account. |
| A14 | Periodic amount terms now sit on a Deposit / Loan | Once signed they **count in the contracts run rate**; confirm the amounts. |
| A15 | An existing contract was reused | The account keeps its existing role and no custodian party is added; add parties if wanted. |

**What is lost.**

- **L1** — An amount term with no currency used to follow the account's currency, including later
  changes; it is now frozen to the account currency at migration time.
- **L2** — The original direction of a flipped percentage term (A8) survives only in the event text.
- **L3 — Deleting an account no longer deletes its terms.** It removes only the account's party row;
  the contract and its terms remain and follow the contract's lifecycle. Orphaned Draft contracts left
  by an account deletion are expected and are not pruned automatically.
- **L4** — Guest loses read access to these terms: they are now read under `contracts.read`, which
  Guest does not hold. No role gains access.
- **L5** — Per-term history events: the move writes one summary event per account, not one per term.
- **L6** — `ExistingAccount.termCount` and `.currentTerms` are removed from the API, and with them the
  account row's term badge, Current-band term tiles, Terms section and *New term* menu item.
- **L7** — `TermExport.accountId` is removed from the admin data export.

**API and permissions.** The five `/api/accounts/{accountId}/terms…` routes are gone and answer `404`;
`/api/contracts/{id}/terms…` is the only term API. `ExistingTerm` loses `accountId` and its
`contractId` is never null. The account-term read/write claim pair is deleted; `RoleClaimSeeder` revokes
its `AspNetRoleClaims` rows at the next start with no migration. Sessions drop the dead values at their next
revalidation, within a minute ([no sign-out needed](deployment.md#new-or-revoked-permission-claims-need-no-sign-out)); until then they are harmless — no endpoint checks them.

**If the migration is interrupted.** An interruption before the schema change is safe to re-run: every
decision it takes is staged once and reused. An interruption *during* the final column drop is not
covered by the start-up drift check; repair it by hand per
[`migration-history-drift.md`](migration-history-drift.md).

## 0.34.0

### Rate term kinds folded into labelled terms, and `TermKind` removed

Two migrations run in order. `FoldRateTermKindsIntoFee` rewrites every `InterestRate`,
`ExpectedReturn` and unrecognised term as an ordinary labelled term — labelled `Interest rate`,
`Expected return` or `Unspecified` where it had no label — keeping its value, unit and dates.
`RemoveTermKind` then drops the `Terms.TermKind` column; a term's series is now its label alone.
If an owner already had a term labelled `Interest rate` or `Expected return`, the converted rate joins
that history rather than forking a second one. No data is lost, and the down migrations are
best-effort (they restore a kind only from the exact label the fold wrote).

On the wire, `termKind` is gone from every term request and response, from the data export, and as
the `?kind=` filter on both history endpoints; `label` is now required on every term.
`ExistingAccount.currentInterestRate` / `currentInterestRateKind` are removed. The account row's
headline rate, the rate step chart and the "Interest charged" tint are withdrawn for now — a former
rate shows as a term like any other.

## 0.33.0

### Contract terms carry a money direction (issue #159)

Each priced contract term now says **which way the money moves**, from the household's perspective —
`Outgoing` or `Incoming` — so a landlord's rent is no longer summed into "what the file costs to run"
and an employment contract's salary no longer reads as an expense. Three things an operator should
know.

**The `AddTermDirection` migration is additive and behaviour-preserving, with no backfill statement.**
It adds one `NOT NULL DEFAULT 0` `int` column to `Terms`; the default writes `Outgoing` into every
existing row, which is what every existing term already meant. On a file whose contracts only record
costs, `GET /api/contracts/summary` returns a **byte-identical** body before and after, except for the
new fields being null or empty. No index, no seeded row, no data migration. `Down` drops the column,
and the directions recorded after the upgrade are not recoverable from it.

**`runRate.monthly` / `.yearly` / `.byType` keep their names *and* their meaning — they are now
explicitly the OUTGOING side.** The incoming side arrives under new names (`incomingMonthly`,
`incomingYearly`, `incomingByType`), the net under `netMonthly` / `netYearly`, and the incoming
movements in a new `upcomingReceipts` list beside `upcomingCharges`. A consumer reading the existing
fields today is correct tomorrow. Two rules worth knowing when reading the payload: no gross ever
mixes directions, and the net is the only signed figure — it is `(incoming ?? 0) − (outgoing ?? 0)`,
computed from the unrounded sums, and null only when **both** sides are.

**The contracts header now shows four run-rate tiles instead of two.** "Monthly run rate" and
"Yearly run rate" are renamed **"Monthly out" / "Yearly out"** and joined by **"Monthly in" /
"Yearly in"**; the two by-type tiles become **"Monthly net by type" / "Yearly net by type"**, each
closing with a ruled **Net** row. The figures behind the two "out" tiles are the same numbers the old
two showed — the rename is because the figure is now one side of two, and a name that stayed would
read as a total. The signal panel gains a **Next receipts** group beside Next charges. Nothing here
needs an operator action; it is noted so the change is not mistaken for a defect.

**`ContractMaxSummaryCharges` changes meaning without changing its value or bound.** It now caps the
outgoing and incoming movement lists **separately**, so a file with many charges cannot starve the
receipts — and the worst-case row count for that part of the payload doubles. The setting's own
description on **System settings → Contracts** says so; nothing about it needs changing on upgrade.

**No new permission claim and no new configuration.** Direction rides the existing `contracts.read` /
`.update` and `accounts.terms.write` claims, so **no sign-out/sign-in is required on deploy**. A new
`400` exists on the write path: `Incoming` on an *account*-owned term — no account surface reads a
direction yet, so accepting one there would
record a fact the product then contradicts. And note that `PUT …/terms/{termId}` is a **full replace**:
omitting `direction` resets the term to `Outgoing`, exactly as omitting `label` or `anchorDate` already
clears those. Every read path returns the field, so a read-modify-write round trip carries it back
unchanged.

## 0.32.0

### Contract signature dates are backfilled, and the values are not evidence (issue #145)

A contract now carries two signature stamps, **Ready** (marked ready for signature) and **Signed**
(signed by all parties), and they fold into the derived contract status as two new states, **Draft**
and **Ready**. An unsigned contract is on file but not in force: it is excluded from the contracts run
rate, its by-type cost split and the upcoming charges. Three things an operator should know before
upgrading.

**The `AddContractSignatureDates` migration writes a FABRICATED signature timestamp onto every existing
contract row, and no such value is a statement of fact.** A field that looks like evidence and is not
is a provenance defect, so it is recorded here rather than left to be discovered: if you need to know
when an agreement was really signed, the backfilled value does not tell you. The value is derived from
each row's *own* dates — `LEAST(COALESCE(StartDate, CompletionDate, CreatedAtUtc), CreatedAtUtc)`, i.e.
*signed no later than the day its term began, and no later than the day its row was created* — rather
than one shared clock reading stamped across the table, so the residual is a per-row approximation
instead of a table-wide assertion. The clamp is what keeps a contract whose term starts in the future
from being stamped as signed in the future, which the write path refuses outright.

**Leaving the columns null would not have been the safer alternative.** The derivation reads a null
`Signed` as unsigned, so an un-backfilled upgrade would flip *every* contract in the database to
`Draft` and empty the run rate and the upcoming charges overnight. Backfilling is what makes the change
invisible until someone deliberately records a draft: no existing contract changes status across the
upgrade, and the summary body is identical except that `countsByStatus` gains `draft: 0` and
`ready: 0`.

**No new permission claim and no new configuration.** The two fields ride the existing
`contracts.read` / `.create` / `.update` claims, so **no sign-out/sign-in is required on deploy** —
nothing baked into an existing auth cookie is affected. Neither column is indexed (the status is
derived in memory after projection, so neither is ever a SQL predicate), and neither carries a
user-attribution column: a `Ready`/`Signed` transition is recorded as one structured `Information` log
line naming the contract, which stamp changed, whether it was set or cleared, and the acting user.
`Down` drops both columns; the backfilled values are not recoverable, which is correct — they were
fabricated on the way up.

One behaviour change for anyone driving the API directly: **`GET /api/contracts?sortBy=Status` now
orders by a lifecycle rank** (`Draft → Ready → Upcoming → Active → Paused → Expired → Archived`)
rather than the enum ordinal, so `Expired` and `Archived` now sort *after* `Active`. The persisted and
wire ordinals are unchanged — `Draft` and `Ready` are appended at 5 and 6 — and this affects display
order only.

## 0.29.0

### User profile pictures (issue #94)

A signed-in user can attach, replace and remove **one picture of themselves** from `/account`. It then
stands in for their initials wherever the application already identifies them: the account page header,
and the administrator's user list and detail panel. Four things an operator should know before
upgrading.

**A new permission claim, `profile-images.read`, and revoking it from Guest is the supported way to
narrow who sees colleagues' pictures.** It is granted to **every** role out of the box, so its effective
reach is "any authenticated caller" — what it buys is a *revocation lever that exists before release*
rather than narrower access today. It needs **no migration**: `RoleClaimSeeder` reconciles
`AspNetRoleClaims` at runtime and applies removals as well as additions. A revocation lever nobody knows
about is not a lever, which is why it is written down here.

**Pictures stay blank for already-signed-in users until their session picks up the new claim** — within
a minute and a page load, see [New or revoked permission claims need no sign-out](deployment.md#new-or-revoked-permission-claims-need-no-sign-out). Until then
the session sees the initials monogram. On `/account` the picture *is* the feature rather than
decoration, so for that window the controls are disabled with "Reload the page in a minute to manage
your profile picture" — note that the same disable covers **Remove**, so the self-service erasure control is
briefly unavailable too.

**Disabling an account also hides its picture — from administrators as well.** The read endpoint `404`s
an administratively disabled subject, which is what makes "disable the account" a proportionate remedy
for an abusive upload (there is deliberately no administrator write path, so an admin can neither
replace nor remove someone else's picture). The row survives, but only direct database access reaches
it. **So capture the evidence before disabling**, not after. Note this applies to an *administrative*
disable only — a user locked out by five mistyped passwords keeps their picture, deliberately: hiding
it would turn the endpoint into a `200 → 404 → 200` confirmation channel for failed-login lockouts.

**`Down` on the `AddUserProfileImage` migration destroys the stored images.** The schema is reversible;
the data is not. The migration is otherwise purely additive — two new tables, one unique index, two
cascade foreign keys, no existing table altered and no rows seeded.

Three new `RateLimiting:*` sections are shipped with defaults and stay in deploy-time configuration
(`ProfileImageWrite`, `ProfileImageDelete`, `ProfileImageRead`). The write and delete budgets are
**separate on purpose** — sharing one would mean a user who had exhausted their upload budget could not
*remove* their picture, which throttles a GDPR Art. 17 control. The read budget is deliberately generous:
a rejected read is invisible to the user (the avatar silently falls back to the monogram), so a tight
limit produces a silent defect rather than an error anyone reports.

## 0.17.0

### The SMTP transport moved into System settings (issue #8)

`Email:SmtpHost`, `Email:SmtpPort`, `Email:UseStartTls` and `Email:ClientBaseUrl` moved from configuration
into the settings store. Their environment variables and the whole `Email` configuration section were
**deleted**, not deprecated, and nothing carries a configured value across: enter the relay at
**`/settings` → Email** after upgrading. The Production `ValidateOnStart` gate on `Email:SmtpHost` went with
them, so the API no longer refuses to start without a relay — see
[Mail is configured after first sign-in](deployment.md#mail-is-configured-after-first-sign-in).

## Before 0.16.0

### Five credentials moved into the encrypted secret store (issue #445)

`FileAnalysis:ApiKey`, `Email:Username`, `Email:Password`, `Email:RecipientHashKey` and
`Legal:PseudonymizationSecret` are no longer read from configuration. They live in the encrypted
`SystemSettingSecrets` table and are entered at **`/settings` → Credentials** by an administrator
holding `system-settings.security.update`. Each write is audited; no value is ever shown again.

**Every one of them must be entered by hand.** A secret is deliberately
**not adopted from configuration** — no mechanism reads one out of the environment and writes it to
the store, and none should be added: that would require the plaintext to still be present in the environment, which is most
of what moving them was for, and would leave the row owned by configuration with no visible owner. So
there is a defined gap between deploying and an administrator entering each value.

| Credential | While it is unset, after the upgrade | Kind |
|---|---|---|
| `FileAnalysis:ApiKey` | Every analysis job fails and is recorded as a credential failure. Nothing is transferred and nothing is lost. | rotatable |
| `Email:Username` + `Email:Password` | **Transactional mail is not sent.** With *both* unset the relay is used unauthenticated (which is what an empty `EMAIL_USERNAME` did before); with only one stored, or either unreadable, the send is logged and skipped rather than attempted with half a credential. | rotatable |
| `Email:RecipientHashKey` | Unchanged and healthy: the send throttle generates a per-process key, so its recipient digests correlate within one process and not across a restart. | derivation |
| `Legal:PseudonymizationSecret` | Account deletion cannot pseudonymise consent records. Outside Production the fixed development value is substituted; in Production the deletion fails, inside its own transaction, leaving the acceptance rows intact. | derivation |

Read this before upgrading:

- **Copy `LEGAL_PSEUDONYMIZATION_SECRET` out of your environment first, and enter that same value.** It
  is a *derivation* key: if consent rows were already pseudonymised under it, a different value leaves
  those rows permanently un-re-derivable — the property GDPR Art. 7(1) consent attribution depends on.
  There is no provider to re-issue it from and no way to recover it afterwards.
- **Production no longer fails to start without it.** The `ValidateOnStart` gate is gone, deliberately:
  a credential an administrator enters through the UI cannot be a precondition for the UI coming up.
  The failure moved to the first account deletion, with the remedy in the message.
- **If you set any of the five by editing `Odyssey.Api/appsettings.json` directly, that value is
  simply gone** — the keys are no longer bound at all. Note them down before upgrading.
- **An unreadable row never falls back to a configured value, for any of the five.** There is nothing
  to fall back to: the configuration properties were deleted in the same change. An unreadable
  credential means the Data Protection key ring changed or was lost; restore it, or clear the row and
  enter the credential again. `/settings` shows such a row in coral, and the page header raises a
  **Credentials** signal naming what is broken.
- **The keys volume is now load-bearing for these credentials as well as for sessions.** The database
  backup alone is no longer sufficient — see [The Data Protection keys volume](deployment.md#the-data-protection-keys-volume)
  and back both up together.

### The file-analysis switch, model and base URL are settings (issue #439)

`FileAnalysis:Enabled`, `FileAnalysis:Model` and `FileAnalysis:BaseUrl` are database-backed as of issue #439,
editable at **`/settings` → File analysis** by an administrator holding
`system-settings.security.update`. Every change to any of the three is written to the audit log.

As with the three keys below, their environment variables (`FILE_ANALYSIS_ENABLED`, `FILE_ANALYSIS_MODEL`,
`FILE_ANALYSIS_BASE_URL`, and the `Aspire:` keys) are **gone** and nothing carries a configured value
across. A deployment starts at the shipped defaults — **analysis off**, `claude-sonnet-5`,
`https://api.anthropic.com` — and an administrator turns it on at `/settings`.

That default ordering is deliberate and worth keeping in mind if a carry-over step is ever
reintroduced: a deployment that silently *starts* transferring documents to a third party would be far
worse than one that silently stops, so the switch must fail to **off**.

Four operational notes:

- **`FileAnalysis:ApiKey` moved too, in issue #445** — into the *encrypted secret store*, not into
  these plaintext settings. See the credentials note above. The consequence stated on the settings row is
  unchanged: the stored key is attached to the outbound client, so **repointing the base URL sends that
  key to the new host**. Repoint it only to a host you control or trust. The base URL must be an
  absolute `https://` address with **no path** — the provider appends `/v1/messages` itself — and no
  credentials, query or fragment; all of those are rejected on save, and a value planted by a restore
  is rejected on read as well, which makes analysis refuse rather than fall back to
  `api.anthropic.com`.
- **The switch is honoured on the next request**, not up to 30 seconds later: it is read live on every
  file-analysis call rather than from the 30-second settings cache. An operator instructed to stop
  transfers can stop them.
- **Redirects are not followed.** The outbound client runs with `AllowAutoRedirect = false`; a `3xx`
  from the configured host is recorded as a provider error. .NET strips only `Authorization` across
  origins, so a custom `x-api-key` header would otherwise survive a redirect — along with the document
  on a `307`/`308`. Configure a gateway at its final address.
- **The circuit breaker is per typed client, not per host.** After repointing the base URL, a breaker
  opened by failures against the *previous* host stays open for the rest of its sampling window
  (`2 x FileAnalysis:TimeoutSeconds`, so 240s at the shipped 120s). Pre-existing behaviour, but now
  reachable by an admin action: wait it out, or restart the worker (which sends the document since issue
  #444; the API keeps a breaker of its own for the match step).

### Three `FileAnalysis` keys retired from `appsettings.json` (issue #434)

`FileAnalysis:MaxTokens`, `FileAnalysis:Match:MaxVocabulary` and `FileAnalysis:Match:TimeoutSeconds` are
database-backed as of issue #434 and no longer read from configuration. (`MaxVocabulary` was later retired altogether by
issue #433; see 0.45.0.)

Their environment variables (`FILE_ANALYSIS_MAX_TOKENS`, `FILE_ANALYSIS_MATCH_MAX_VOCABULARY`,
`FILE_ANALYSIS_MATCH_TIMEOUT_SECONDS`, and the `Aspire:` keys) are **gone**, and nothing carries a
configured value into the store: the settings start at their shipped defaults (8096 / 500 / 60) and an
administrator sets them at `/settings`. A carry-over step existed briefly and was removed once it was
established that no deployment had ever run a release it could upgrade from — see CLAUDE.md, which
records what a future one would have to get right if that changes.

`FileAnalysis:TimeoutSeconds` is **not** affected — it stays in configuration, because it is consumed
once at startup by the HTTP resilience handler and a runtime value could never reach a live pipeline.
