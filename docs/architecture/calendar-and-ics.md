# Calendar, recurring series and .ics import/export

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**A task or journal `.ics` export carries its attached files inline — a journal's library photos too — and the
import stores them without duplicating** (`TaskIcsService`, `JournalEntryIcsService`,
`Journal/Interop/IcsBinaryAttachments.cs`, `Journal/Interop/InlineAttachmentImporters.cs`). Rules that are easy to
get backwards:

- **`Ical.Net` never sees a binary value.** The export serializes each VTODO/VJOURNAL without its files and
  `IcsBinaryAttachmentWriter` splices in `ATTACH;ENCODING=BASE64;VALUE=BINARY` lines streamed from the store; the
  import's `IcsAttachmentSpooler` decodes them to temp files and hands `Ical.Net` the text with placeholders. A value
  held as one string caps out near 1 G characters, which is why a 10 GB export is possible at all.
- **`EncodedLength` must stay exact** — the export refuses an over-cap file from it before sending a byte.
- **Dedupe is id-then-hash, over `VisibleInFilesStore` only**: the exported file's own id with the same SHA-256 and
  size, else the oldest visible file with that SHA-256 and size, else a new Journal-area file. A new file passes the
  same `FileValidationService` checks an upload does, the admin upload cap included.
- **A photo is its own kind (`X-ODYSSEY-KIND=PHOTO`) and dedupes the same way over `PhotoOriginalSource.Describe`**;
  a match is linked as-is, and a new one goes through `PhotoService.StageUploadAsync` — the
  library upload's checks — carrying only the exported file name, title and caption. Tags, people and albums stay
  library-only.
- **Files make the claims stricter, never optional**: an export with attached files needs `files.read` (photos:
  `photos.read`), an import carrying inline files needs `files.read` and `files.create` (photos: `photos.read` and
  `photos.create`), and either is refused whole (`403`) without them — the import at its first such attachment,
  inside the spooler, before a byte of it is decoded.
- **The browser transfers these files itself.** The client asks `GET api/tasks/ics/summary` or `…/vjournal/summary` (same checks) and then
  navigates to the export URL; the import posts the picked `File` from disk (`INativeTransfer`). The service worker
  must not answer an `/api/` navigation with `index.html`.
- **The 10 GB transport ceiling is not every endpoint's limit.** `ImportSizeLimitMiddleware` holds any endpoint
  without an import/upload cap to `FileStorage:DefaultMaxRequestBodyBytes` (65 MB), and `[UploadTransportLimit]` (the
  photo upload) to the admin upload cap at the transport only. A new file endpoint takes one of the three
  attributes; `DefaultRequestBodyLimitTests` fails otherwise. The proxies give the 10 GB size and the hour-long
  timeouts to the task, journal and calendar `.ics`, photo and file routes alone, and nginx streams the `.ics`
  responses unbuffered (`proxy_buffering off`) so the export progress guard below sees the real client.

**A calendar `.ics` export and import stream, and both are planned or applied in batches** (issue #516). Rules that
are easy to get backwards:

- **No export opens a transaction.** The planner (`CalendarIcsService.PlanAsync`) projects keys and the collapse
  columns once, the writer fetches id batches through `ExportChunking.ReorderToSnapshot`; a retry under
  `EnableRetryOnFailure` would re-emit streamed chunks. The planner is also the summary — one definition of what an
  export contains. Past the byte cap the writer throws `IcsExportSizeExceededException` and the controller aborts;
  it never truncates.
- **An occurrence moved to another calendar is written in its new calendar's file** — since #519 as an `EXDATE` of its
  series in the series' own calendar (below). The aggregate export collapses a series only when all its rows share one calendar
  (`CalendarIcsService.Export.cs`, `Decide`), and keeps synthetic UIDs.
- **A synthetic UID (`{guid}@odyssey.local`) is never persisted as `ExternalUid`** (#345 F1). It matches a row by key
  only within the target calendar; a row created from one keeps it as `ImportSourceUid`, which is never exported and
  never read as a key. An import never clears a stored `ExternalUid`.
- **The import holds no whole-file state but the sort**: since #519 the byte scanner (`IcsByteScanner`, the splitter's
  rules: 1 Mi-character lines, 4 Mi components) feeds the UID grouper, groups of one UID apply together, batches of 500
  groups resolve by keyed queries and save in one transaction each, and within a group the last component wins. A
  failure after a commit is `calendar_ics_import_partially_applied` with counts; a cancellation is never wrapped.
- **A cancelled component removes only with `allowRemovals`**, which needs `calendar.delete` (checked once, at the
  controller); a cancelled `RECURRENCE-ID` is an exclusion under the same gate. A removed series follows
  `RecurrencePatternService.Delete`; each batch that removed anything logs ids, keys and counts only.
- **The export progress guard is `ExportConcurrencyFilter`**, for every guarded export: no first byte in 10 minutes,
  under 1 MiB in a 5-minute window with a write waiting on the client, or 24 hours. It replaced a fixed 10-minute
  deadline; don't put one back.

**A calendar event or series links files and library photos, and they travel inline in its `.ics`** (issue #521).
Four sibling tables (`CalendarEventAttachments`/`Photos`, `RecurrencePatternAttachments`/`Photos`), all `CASCADE`, no
attribution column. Rules that are easy to get backwards:

- **`CalendarLinkService` is the only writer** of the four (a source-lint in `Odyssey.Core.Tests` scans Core, Api and
  Worker), because "event links exist only on a standalone event" has no database check. A series' links show on every
  occurrence; a link array on a generated occurrence is a `422`. **Every detach copies the series' links** — series
  delete (chunked, 1,000 rows per save, inside its transaction), a cancelled series in an import, and every keying that
  detaches a row (inline or the backfill job). `CalendarEventMaxLinksPerKindMax` (100) is what bounds that copy at
  1,000 × 2 × 100 = 200,000 rows; raising it re-opens the bound.
- **Writes keep what they do not name**: a null array keeps, `[]` clears. The read claims are **change-only** — only an
  ADDED file needs `files.read`, an added photo `photos.read` — and the cap refuses only a rise. Reads return ids,
  positions and counts under `calendar.read`; names, types and sizes need the kind's read claim
  (`CalendarLinkVisibility.ApplyLinkVisibility`, on every action that returns an event or series).
- **Export: inline once, then a reference** (`ATTACH;VALUE=URI;X-ODYSSEY-SHA256=…;SIZE=…:odyssey-file:<id>`), decided at
  write time. The aggregate export carries none. `attachments=omit` needs neither read claim; `include` is a
  `403 calendar_ics_export_attachments_forbidden` when the plan links a kind the caller cannot read.
- **One head reader, `IcsAttachHead`**, for pass 1, the spooler and the task and journal imports: a substring test of
  `X-ODYSSEY-KIND` or `ENCODING` would gate one kind and store the other. The importer re-demands the claims where it
  stores or links, and only a VEVENT's own `ATTACH` is skimmed or decoded — never one nested in a `VALARM`.
- **Import: claims by KIND on pass 1, before any batch** (`403 calendar_ics_import_attachments_forbidden`): any
  `odyssey-file:`/`odyssey-photo:` or inline `ATTACH` needs the read claim, inline content also the create claim. Any
  other URI has no kind, needs nothing and replaces nothing. A kind's links are replaced only when one of its `ATTACH`
  resolved or was stored, so the aggregate export or a Google file re-imported never strips links.
- **Two phases per batch** (v5): phase A decodes one attachment at a time, matches it read-only and writes new content
  as a `Pending` reservation (`IObjectStore.ReserveAndWriteAsync`, `StoredFileWriter.PrepareNewFileAsync`,
  `PhotoService.PrepareUploadAsync`) outside any lock; phase B, inside each retried attempt, re-checks under the lock
  and attaches a FRESH row (`AttachReserved`). Never re-attach a row a failed attempt mutated, and never release a lost
  reservation through `StageRelease` — `AbandonReservationAsync`, else the abandoned-write sweep.

**A recurring series remembers its exceptions by occurrence KEY, and times are wall-clock plus zone** (issue #519).
Every generated row carries `OccurrenceKey` — its local wall-clock start under the rule, `Kind = Unspecified`, unique
per series — and an edited one `IsOverride`; a deleted one is a `RecurrenceExclusions` row. Rules that are easy to get
backwards:

- **Survival is key-set membership alone.** `RecurrenceSeries.Plan` regenerates the future keys and updates every
  non-override row in place; an override or exclusion survives exactly while its key does. There is no list of
  "timing fields", and never delete-then-insert of a key (the unique index would `1062`). Past keys are never touched.
- **`StartDateTime` has two stored meanings**: the UTC instant when `TimeZoneId` is set, the wall-clock when it is null
  (floating — every pre-#519 row and every all-day row, which `CK_*_AllDayHasNoZone` keeps zone-less). Writes send
  wall-clock plus zone and only the server derives the instant (`CalendarTimeZones`: a gap moves forward, an ambiguous
  time takes the earlier instant). A floating read still returns its value labelled UTC, so older clients keep working.
  Windowed reads take instants plus `viewTimeZoneId`, and the two-branch predicate and the offset-segment sort key
  (`CalendarWindow`) are SQL — never filter or sort a mixed set in memory.
- **Every series writer takes the series lock first** (`CalendarSeriesTransactions`: `FOR UPDATE`, ascending ids,
  `SET STATEMENT innodb_lock_wait_timeout = 5`), and keys a legacy series inline with the backfill's routine
  (`RecurrenceSeries.KeyInPlace`) before anything else. A `1205`/`1213` past the retries, or a `1062` on a key index, is
  `409 calendar-series-busy`, never a `500`.
- **`Version` rotates on series-level writes only** (a series `PUT`, keying, an import that rewrites the master), never
  on an occurrence write. The `PUT` checks `expectedVersion` first, then `confirmedDrops` — both under the lock; consent
  is bound by the drop counts, not by the version.
- **An import change that removes a stored row or discards a stored exception needs `allowRemovals` plus
  `calendar.delete`**; without it the series is left unchanged and counted in `gatedSeriesCount`. A series `PUT` may
  discard exceptions under `calendar.update` alone because the user previewed and confirmed them.
- **The import groups by UID before applying** (`IcsImportGrouper`: keyed-hash external sort, component cap
  `max(1, bytes/64)`, 1.5× disk reservation, 1.25× spill bound). A master, its `EXDATE`s and its overrides apply as one
  group in any file order; groups commit in hash order.
- **A series collapses on export only when `CanExportWithExceptions` holds** (the RFC-literal key set equals rows plus
  exclusions, every row keyed): one master with `EXDATE`s, one `RECURRENCE-ID` per row that differs, one `VTIMEZONE`
  per planned zone. A moved override is an `EXDATE` in its series' calendar and a standalone event in its own; the
  aggregate stays flat for any series with an exception.
