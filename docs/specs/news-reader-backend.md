# News reader (RSS, Atom, JSON Feed) — backend specification

**Status:** approved.
**Source:** GitHub issue [#470](https://github.com/centralcmd/odyssey/issues/470), merged from the issue
body and its specification comments into one document.
**Frontend counterpart:** [`docs/specs/news-reader-frontend.md`](news-reader-frontend.md), issue
[#473](https://github.com/centralcmd/odyssey/issues/473).

Odyssey gets a new **Internet** module, and its first feature is a news reader for plain syndication
feeds (RSS 0.9x/1.0/2.0, Atom 1.0 and JSON Feed 1.x). The worker fetches the subscribed feeds on a
schedule under per-feed database leases, so any number of worker replicas can run. It strips each item
to plain text, downloads the item's images, and stores each article as files in its own folder on the
existing file-storage object store (a new storage feature, `NewsArticles`), with the metadata in
`OdysseyContext` on MariaDB. The household shares one feed list, one read/unread state and one set of
news tags. Search covers title, author and excerpt. This document covers the backend half: storage,
ingestion, fetching, egress policy and the HTTP contract.

---

## 1. Overview

The worker fetches the subscribed feeds on a schedule, strips each item to plain text, downloads the
item's images, and stores each article as files in its own folder on the existing file-storage volume,
with its metadata in the main database. The household shares one feed list, one read/unread state and
one set of news tags.

### Primary user value

- Read every subscribed site in one place inside Odyssey, self-hosted, with the data under the
  household's control.
- Articles are kept indefinitely, and up to 20 earlier versions of an edited article are retained, the
  original always among them. A publisher's later edit or removal does not erase what was fetched.
- No third-party content runs in the browser. Text is plain and images are served from Odyssey, so
  reading generates no tracking requests to publishers.
- Tag feeds and articles with a dedicated news tag set. Tags also do the job of favourites.

## 2. Goals and non-goals

### Goals

1. News entities in `OdysseyContext`, added by **one additive migration** (`AddNewsModule`).
2. A storage feature **`NewsArticles`** (root `news`, opaque names, graced release). Each article's
   source, plain text, images and earlier versions are files in its own folder.
3. Feed management: subscribe, edit, pause, archive, delete (batched), refresh now, release from
   quarantine, and feed tags.
4. A worker job that fetches feeds politely and parses RSS 0.9x/1.0/2.0, Atom 1.0 and JSON Feed 1.0/1.1
   safely. It runs under per-feed leases, so any number of worker replicas can run, and no feed or item
   can crash or stall it.
5. Ingestion: item HTML to normalised plain text, identity resolution, revisions, image download and
   validation, all within the soft quotas and the per-feed daily caps.
6. Read state (single, bulk, and mark-read for the list's exact filters up to a server watermark), and
   article tags.
7. Full-text search over title, author and excerpt.
8. Eight new permission claims (115 → 123), nine `SystemSettings` rows, one catalogued job
   (`JobType` 9), a news status endpoint, typed clients in `Odyssey.ApiClient`, and E2E.Api permission
   coverage.
9. Documentation: `docs/deployment.md` (the `NewsArticles` binding, the allow-list, the network
   topology, inode sizing, the accepted residuals), `CLAUDE.md` (the claim count and the news feature),
   `THIRD-PARTY-NOTICES.md` (AngleSharp).

### Non-goals (v1)

1. Newsletters, scraping, and full-page extraction for summary-only feeds (v2).
2. Other formats (RSS 3.0, OPML-as-feed, h-feed, ActivityPub), which get `Unsupported`.
3. Authenticated feeds. A URL with user info is refused.
4. AI features of any kind. No tables, columns or jobs are reserved for them.
5. Per-user read state or per-user feed lists.
6. A favourite or star flag, and any article state other than read and unread.
7. Merging the same story across feeds.
8. Purging articles or images by age. Deleting a whole feed is the only removal.
9. OPML import and export.
10. Rendering HTML.
11. Clickable auto-linking.
12. **Full-text search inside article bodies** (decided, §17.1 A). The body is stored as a file. v1
    search covers title, author and excerpt; full-body search is future work (§18).
13. Encrypting feed URLs at rest (user decision, §7.13).
14. Deduplicating images across articles (user decision).

## 3. Architecture

### 3.1 Components

1. **Entities.** `Odyssey.Context` (flat namespace, like every other entity) in `OdysseyContext`.
2. **Feed, article and tag services.** `Odyssey.Core/Internet/News/`.
3. **Article file store.** `Odyssey.Core/Internet/News/Files/`, holding the `NewsArticles` storage
   feature, the bucket allocator and the reader and writer that sit over `IObjectStore`.
4. **Egress client.** `Odyssey.Core/Internet/News/Fetching/`, a `SocketsHttpHandler` with:
   - `UseProxy = false`;
   - `AllowAutoRedirect = false`;
   - no cookie container;
   - a `ConnectCallback` that applies the address policy (§7.9);
   - manual redirects, and size and time caps.
5. **Feed parser.** It detects the format from the body (§8.6) and reads it through a depth-capped
   wrapping `XmlReader` (RSS, RDF, Atom) or a depth-capped `JsonDocument` (JSON Feed), into one item
   model.
6. **Content sanitiser and normaliser.** A linear nesting pre-scan (§7.10) runs before any HTML is
   parsed. AngleSharp then parses with no loader and no scripting. An iterative DOM walk with node and
   depth caps and a deadline produces the text, and one text sanitiser (§8.7) cleans every feed-derived
   string.
7. **Ingestor.** Identity resolution (§8.8), clamping, a transaction per item batch, insert or revision,
   file writes through the store, and the image queue.
8. **Image downloader.** The same egress client, plus the shared `StillImageValidator` with a news
   `StillImagePolicy` (which gains `MinDimension`).
9. **`NewsFeedJob`.** An `IPeriodicJob` in Core, registered in `Odyssey.Worker/WorkerHost.cs`,
   catalogued as `JobType.NewsFeedFetch = 9`. Its phases are purge, fetch, images and maintenance.
10. **API and clients.** Controllers in `Odyssey.Api/Controllers/Internet/`, DTOs in
    `Odyssey.Dtos/Internet/`, typed clients in `Odyssey.ApiClient`.

Shared Core helpers named in this spec, each the single definition of its rule: `NewsUrlKey` (§8.2),
`NewsText.Clean` (§8.7), `NewsTextQuota.IsReached` (§6.3), `NewsDailyCap.IsReached` (§6.3),
`NewsImageSizeCeiling.Kb` (§11.1), `GlobalizationGuard` (§8.2), `INewsCounterWriter` (§3.5),
`INewsArticleSearch` (§8.4), `NewsSearchLimiter` (§8.4).

### 3.2 Files on the object store

```
<volume>/news/                                ← StorageFeature "NewsArticles" root folder
  <feedId:N>/                                 ← one folder per source (feed)
    <bucket>/                                 ← 1, 2, 3 … at most 10 000 article folders each
      <articleId:N>/                          ← one folder per article
        <storedObjectId:N>.xml | .json        ← the source item, as fetched (Kind = Source)
        <storedObjectId:N>.txt                ← the plain-text article (Kind = PlainText)
        <storedObjectId:N>.jpg | .png | .webp ← each downloaded image (Kind = Image)
```

- **The feature.** `StorageFeature.Create<NewsArticleLayout>("NewsArticles", "news", args =>
  [args.FeedId.ToString("N"), args.Bucket.ToString(), args.ArticleId.ToString("N")],
  StorageReleaseMode.Graced, StorageFeatureReference.To<NewsArticleFile>(f => f.StoredObjectId),
  StorageNamingMode.Opaque)`.
  - **Opaque naming (user decision).** Files are named `<storedObjectId:N><ext>`, so no
    publisher-supplied name and no user text reaches the disk or the database path. The extension
    always comes from the validated content type.
  - **Graced release.** Deletion waits for `FileStorageDeletionGraceHours`, like every production
    feature. That keeps backups consistent.
- **Revision files keep the names they were written with.** The store has no rename and its suffix is
  applied only at write time. A revision is recorded only by setting
  `NewsArticleFiles.NewsArticleRevisionId`; an earlier version's source and text stay in the article's
  folder under their original names.
- **Buckets (user decision).** These follow the photo-originals precedent
  (`PhotoOriginalBucketAllocator`).
  - A `NewsFeedBuckets` row per feed holds `CurrentBucket` and `ArticlesInBucket`.
  - When the article folder is first created, the row is locked `FOR UPDATE` with a 5 s lock wait, and
    the bucket is taken. The bucket is allocated in **its own short transaction, before the article
    transaction**, as `PhotoOriginalBucketAllocator` does. A gap left by an aborted article is harmless.
  - The bucket advances at 10,000 articles, the `BucketCapacity` constant (from `NewsLimits`). That
    keeps every folder at ≤ 10,000 entries, the store's §3.3 rule.
  - `NewsArticles.Bucket` records the chosen bucket, so all of an article's later files (revisions, late
    images) land in the **same** folder.
- **Store rules inherited unchanged (`CLAUDE.md` → *Files on disk*):**
  - A `Committed` row always has its file.
  - Writes go through `PutAsync`, saved with the referencing row in one `SaveChangesAsync`.
  - Releases go through `StageRelease` in the same save that removes the reference.
  - No caller supplies a path.
  - No endpoint takes a stored-object id.
  - Every filesystem operation re-runs the structural, containment and symlink checks.
  - `OpenReadAsync` 404s another feature's object.
- **Deployment.** `FileStorage:Bindings:NewsArticles` names the volume, identical for `api` and
  `worker`. It goes in `docker-compose.yml` and the AppHost, the only places that carry bindings today;
  `docker-compose.prod.yml` does not. The shipped shape binds it to `primary`. Startup check B1 (every
  feature bound) and `FileStorageDeploymentParityTests` cover it. A host with no volumes is the existing
  no-op case, so news writes fail closed with `503 news-storage-unavailable`.
- **What the store's sweeps can and cannot detect.**
  - The primary detector of a lost file is a **failed read**. A missing body returns
    `bodyUnavailable: true` and logs an Error. A missing image returns `404` and logs an Error; the
    worker's maintenance repairs it (§3.4 step 9).
  - The store's integrity scrub and disk reconciliation are slow backstops at this volume. These are
    estimates and are speculative: about 200 objects an hour for the scrub, and 256 folders an hour for
    reconciliation, so a full cycle over the news folders takes months to years. The spec relies on
    neither for timely detection.
  - **Inode use.** About 2–7 files per article is the price of the one-folder-per-article layout.
    `docs/deployment.md` advises sizing a volume's inodes for it.

### 3.3 Concurrency: two tokens and column ownership

`NewsFeeds` has **two** concurrency tokens, following the weather precedent
(`WeatherPoint.SettingsVersion` and `WeatherPointSchedule.Version`).

**`SettingsVersion`** (`Guid`, concurrency token):
- Rotated **only** by API writes to user-owned columns: `Url`, `CustomTitle`, `FetchIntervalMinutes`,
  `DownloadImages`, `IsActive`, archive and delete requests, tags, and applying a `SuggestedUrl`.
- `ExistingNewsFeed` carries it as `version`, and `UpdateNewsFeed` requires it (`[Required] Guid?`). A
  mismatch is `409 news-feed-changed`.
- The worker never writes this token.

**`FetchFence`** (`Guid`, concurrency token):
- Rotated **only** by the worker's claim, lease renewal, item-batch saves and completion.
- The API never writes this token.

**Who writes which column.** Each side updates only the columns it owns, as tracked entities, so EF
writes only modified properties.
- The worker owns:
  - the validators (`ETag`, `LastModified`, `LastBodySha256`);
  - the last-fetch, last-success and error columns;
  - `ConsecutiveFailures`;
  - `NextFetchAtUtc`;
  - the lease;
  - the feed metadata parsed from the feed (`Title`, `SiteUrl`, `Description`, `Language`, `Format`,
    `FeedTtlMinutes`);
  - `SuggestedUrl` (below) and the quota columns `QuotaReachedAtUtc`/`QuotaReason` (§6.3).
- The API owns everything else. `Url` and `UrlKeyHash` stay API-owned. (Columns the ownership list does
  not name, such as `QuarantinedAtUtc`, are discussed in §19.)
- **Two requests cross the boundary, and each is a worker-read flag:**
  - **Refresh** sets `RefreshRequestedAtUtc`. The next claim treats the feed as due once
    `max(RefreshRequestedAtUtc, LastFetchAttemptAtUtc + 60 s) ≤ now`. Completion clears the flag
    **only if it was requested before the fetch started** (`RefreshRequestedAtUtc ≤` the claim time). A
    refresh requested during the fetch survives, so the feed is due again after the 60 s floor.
  - **A URL change** sets `ValidatorsResetRequested = true`. Completion clears the validators and the
    flag.

  So a refresh or URL edit made during a fetch is never overwritten.

**A same-host `301`/`308` never rewrites the URL directly.**
- The worker records `SuggestedUrl` (worker-owned) and logs it.
- `SuggestedUrl` is applied **only** by the hourly maintenance step and by a feed `PUT`, using an
  API-side save. That save checks `UrlKeyHash` uniqueness and rotates `SettingsVersion`. On a duplicate,
  `SuggestedUrl` is dropped and logged.
- **Feed `GET` and list endpoints never write.**
- On a `PUT`: if the request leaves `url` unchanged and a suggestion is pending, the `PUT` applies the
  suggestion. If the request changes `url`, the user's value wins and the suggestion is cleared. Either
  way, the response's `url` is the stored value.

**Fence mechanics.**
- The fetch holds the feed's current `FetchFence` in memory.
- Lease renewal and every item-batch save go through **one serialised `FencedSaveAsync`** for that
  fetch. It is a single async lock per fetch, so a renewal and a batch never run at the same time.
- Every fenced save **marks `FetchFence` modified and rotates it**. EF puts a concurrency token in the
  `WHERE` clause only for a modified entity, so this is what makes the check real. The fetch then keeps
  the new value.
- **Every item-batch save checks the fence.** The batch includes the feed row, re-read with its fence as
  a concurrency check. If the lease was lost, or `DeletionRequestedAtUtc` is set, the fetch aborts
  cleanly and nothing more is written. A lease overrun therefore cannot double-ingest, and the
  `IdentityKeyHash` unique index stays an ordinary-path guarantee.
- Completion succeeds only if `LeaseOwner = me` and the fence matches.
- An **aborted fetch** (lease lost, or deletion requested) **clears its lease** in a final fenced save
  when it still can, so the purge can start straight away.

**The hard deadline.**
- The whole fetch, from claim through ingest, has a **90 s deadline**: 30 s for the network and 60 s for
  ingest.
- The lease is **3 min** and is renewed every **60 s**, **only while the deadline has not passed**. At
  90 s the fetch is cancelled.
- A parser hung in synchronous code cannot renew its lease (renewal is cooperative), so the lease
  expires at 3 min and the hang counts as a **crash event**.

### 3.4 Processing pipeline (one tick, every 30 s, on every worker replica)

1. **Purge.** For a feed with `DeletionRequestedAtUtc` set, delete articles in time-boxed chunks
   (≤ 250 ms per transaction, ≤ 5 s per tick).
   - **The purge claims the feed** with the same lease and fence as a fetch. It runs only on feeds with
     no live lease, and fetch claims skip feeds pending deletion, so purge and ingest never run on the
     same feed at once.
   - Each chunk runs `StageRelease` for its articles' files in the same save.
   - When none remain, the feed row is deleted.
   - The same loop rewrites `FeedVisible` after an archive or unarchive. `FeedVisible` is **eventually
     consistent** and an index aid only; the authoritative filter is always the join to the feed row.
2. **Claim due feeds (multi-replica).** Take due feeds (active, not archived, not pending deletion, not
   quarantined, `NextFetchAtUtc <= now` or a refresh due under §3.3, no live lease), oldest first.
   - Each claim is a **tracked per-feed update**, the `WeatherScheduleJob` pattern. It never uses
     `ExecuteUpdate`, so it runs on the in-memory tiers.
   - A process claims only as many feeds as it has **free slots** (4 minus the fetches it already has in
     flight).
   - A claim takes a feed only when its host's `NewsHostThrottle.NextAllowedAtUtc ≤ now` (§3.6).
   - For each claimed feed, set:
     - `LeaseOwner` = this process's random GUID;
     - `LeaseExpiresAtUtc = now + 3 min`;
     - `NextFetchAtUtc = now + max(effective interval, 1 h)`, provisionally;
     - `CrashSuspectCount += 1`;
     - a new `FetchFence`.

   A replica that loses the token race skips that feed.
3. **Fetch.** Up to 4 concurrently per process, at most 1 per host per process, and ≥ 2 s between
   requests to one host across all replicas (§3.6).
   - The request is a GET with `If-None-Match` and `If-Modified-Since`.
   - Redirects are manual, at most 5, and every hop is re-checked. Only the hops actually requested
     count toward the cap.
   - The body is decompressed up to **10 MiB**.
   - If the body's SHA-256 equals `LastBodySha256`, it is treated as a `304`.
   - The network part has a 30 s limit and the whole ingest of one fetch a **60 s budget**, inside the
     90 s deadline (§3.3).
4. **Parse.** Format detection (§8.6). At most 500 items are read; the 200 newest by date are kept.
5. **Per item.** Each step runs under a 2 s per-item deadline. A failure skips only that item, never the
   batch.
   1. Sanitise and clamp every field (§8.7), and normalise the body (§8.3), after the HTML pre-scan
      (§7.10).
   2. Resolve identity (§8.8).
   3. Check headroom before any file write: the news disk threshold (§6.3) and the text quota (§6.3).
      Either refusal is a transient skip.
   4. Then one of three cases:
      - **New.** Allocate the bucket (its own transaction, §3.2), `PutAsync` the source file and the
        plain-text file, and insert the article, its identities and its file rows in **one**
        `SaveChangesAsync`.
      - **Unchanged** (`ContentHash` equal). Touch `LastSeenAtUtc`, at most once per hour.
      - **Changed.** The edit sequence in §3.5.
   5. Queue the image references (`NewsArticleImages`, `Pending`), under the per-feed daily article cap.
6. **Complete.** In the fetch's single final fenced save:
   - **Success.** Update the metadata and validators, set `ConsecutiveFailures = 0` and
     `NextFetchAtUtc = now + effective interval`.
   - **Handled failure.** The back-off in §9.3.
   - **Either way.** Clear the lease and reset `CrashSuspectCount`. Clear `RefreshRequestedAtUtc` under
     the §3.3 rule. Honour `ValidatorsResetRequested`. Recompute `QuotaReachedAtUtc` and `QuotaReason`
     (§6.3).
   - **Transient skips** (the daily cap, the text quota, the disk threshold or a `507`/reserve refusal, a
     lock timeout) also clear `ETag`, `LastModified` and `LastBodySha256`, so the next poll re-parses.
     Deterministic item failures are retried on at most the next 3 full parses.
   - **A redirect hop deferred by `NewsHostThrottle`** (its host is busy) ends that fetch attempt as a
     transient skip: not a failure, so `ConsecutiveFailures` is not incremented, and
     `NextFetchAtUtc = now + 1 min`.
   - **Graceful stop.** It cancels cleanly: the lease is cleared, `CrashSuspectCount` is decremented
     back, and `NextFetchAtUtc = now + 1 min`.
7. **Crash accounting.**
   - A lease that expires without completing is one crash **event**, recorded in `NewsFeedCrashEvents`
     (no feed id, so a delete cannot reset it).
   - A feed's `CrashSuspectCount` counts only events where it was the sole in-flight feed of that owner,
     or two consecutive events. At ≥ 3 the feed is **quarantined**.
   - Three crash events within 1 h trip the job breaker (`BreakerTripped`, the thumbnail precedent) for
     1 h.
   - Compose sets `stop_grace_period: 30s` for the worker.
8. **Images.** Up to 50 per tick per process, in a 20 s budget, claimed by row lease. Each request first
   takes its host's `NewsHostThrottle` slot; a busy host defers the image to the next tick. Each one is
   downloaded, validated and stripped, then written with `PutAsync` into **its article's folder**. The
   quota and daily caps apply (§6).
9. **Maintenance (hourly, per replica, idempotent).**
   - Recompute `NewsFeedCounters` and `NewsStorageTotals`, and log any drift.
   - Apply pending `SuggestedUrl` values (§3.3).
   - Recompute `QuotaReachedAtUtc`/`QuotaReason` for feeds not fetched since, under the conditional-write
     rule in §6.3.
   - **Image repair.** Candidates come from two sources only:
     1. **Store integrity findings.** `StoredObjects` rows of feature `NewsArticles` that the store's
        scrub or reconciliation marked as faulted or missing, read through the store's existing status
        query (`IntegrityFaultAtUtc` or its equivalent), never by re-scanning the disk.
     2. **A bounded worker probe.** Each pass calls `OpenReadAsync` on at most 200 `Stored` news images,
        the oldest `VerifiedAtUtc` first, and stamps `VerifiedAtUtc` on each one.

     For an image whose stored file is not found:
     1. Stage a release of the stale `StoredObject` and **delete its `NewsArticleFile` row** in the same
        save.
     2. Set the image row to `Pending` (`NewsArticleFileId = NULL`, `Attempts = 0`).
     3. At most **100 re-queues per hour per process**, and each image is re-queued **at most once per
        24 h** (`LastRequeuedAtUtc`).

     So no reader can make the worker fetch from a publisher again and again. The image `GET` never
     writes.
   - Prune `NewsHostThrottle` rows not touched for 24 h, and `NewsFeedCrashEvents` older than 7 days.

### 3.5 Writes that span files and rows

**Edits.**
1. **Write the new files first** (`PutAsync`, which saves nothing). Each one returns a `Committed`
   `StoredObject` attached, unsaved, to the caller's context. No row locks are held during file I/O.
2. Open **one explicit transaction with two saves**, inside `CreateExecutionStrategy().ExecuteAsync`:
   1. Save 1: insert the revision row and re-label the current file rows with its id.
   2. Save 2: insert the new `NewsArticleFile` rows, which reference the attached `StoredObject`s, and
      update the article.

   The attached `StoredObject`s persist together with their referencing rows, so a `Committed` row never
   exists without its referencing row.
3. If the transaction fails, the newly written files are reservations and the store's abandoned-write
   sweep removes them.
4. **The overwrite-within-1-hour path** follows the same order. In save 1 it releases the current files
   with `StageRelease` and deletes the old current `NewsArticleFile` rows in that same save; save 2
   writes the new ones.
5. "One current Source and one current PlainText per article" is enforced by the ingestor and covered by
   a test; there is no database index for it.

**Counters.** `NewsFeedCounters` and `NewsStorageTotals` are written by relative increments through
`INewsCounterWriter`:
- Relational: `UPDATE … SET UnreadCount = UnreadCount + @d`. EF InMemory: a tracked increment. This is
  the existing lookup-seam pattern.
- The increments run **inside the same transaction** as the article save they belong to
  (`Database.UseTransaction` on the same connection).
- **Lock order:** feed counters by ascending `NewsFeedId` first, then `NewsStorageTotals` **last**.
  Bulk read-state and mark-read touch feed ids in ascending order, so concurrent writers cannot
  deadlock.
- `NewsStorageTotals` is a hot row. Its update is the last statement of each transaction, so the lock is
  held for a single statement.
- An hourly recount corrects any drift, and logs it.

### 3.6 Per-host spacing across replicas

- `NewsHostThrottle` (`HostKeyHash` PK, `NextAllowedAtUtc`). A request takes a host only when its
  `NextAllowedAtUtc ≤ now`, and sets it to `now + 2 s` in the same save, using a tracked update with the
  row as its own concurrency token. Spacing therefore holds across replicas.
- The throttle covers **feed requests, image hosts and every redirect hop's host**. Each request takes
  the slot for its own host first. A busy host defers an image to the next tick, and a busy redirect hop
  ends the fetch as a transient skip (§3.4 step 6).
- Rows not touched for 24 h are pruned hourly.

## 4. Data model

All of these are in **`OdysseyContext`**, namespace `Odyssey.Context`, with **GUID** primary keys
(Odyssey's convention) and `DateTime` UTC columns.

- Every table has its own primary key, link tables included. There are no base tables. The exceptions
  are `NewsFeedCounters` (keyed on its feed) and `NewsHostThrottle` (keyed on the host hash).
- Bounded strings carry `[StringLength]`, and every one has a clamp rule in §8.7, so a length overflow
  is never the first line of defence.
- **No user ids** anywhere; acting-user attribution goes to logs only.
- **Index length on MariaDB.** InnoDB keys are limited to 3,072 bytes, which is 768 `utf8mb4`
  characters. So the long natural keys get a **SHA-256 key column** (`BINARY(32)`), and that column
  carries the unique index.

### 4.1 `NewsFeeds`

| Column | Type / constraint | Note |
|---|---|---|
| `NewsFeedId` | `Guid` PK | |
| `Url` | `string(2048)` | original text; API-owned |
| `UrlKeyHash` | `binary(32)`, **unique** | SHA-256 of `UrlKey` (§8.2); API-owned |
| `Title`, `CustomTitle` | `string(256)?` | |
| `SiteUrl` | `string(2048)?` | http(s) only |
| `Description` | `string(1024)?` | |
| `Language` | `string(35)?` | validated BCP 47, else `null` |
| `Format` | `byte` enum | `0` Unknown, `1` Rss, `2` Atom, `3` RssRdf, `4` JsonFeed, `5` Unsupported |
| `FetchIntervalMinutes` | `int`, `[Range]` from `NewsLimits` | |
| `FeedTtlMinutes` | `int?` | RSS `ttl`, or `sy:updatePeriod` ÷ `sy:updateFrequency` |
| `DownloadImages`, `IsActive` | `bool` | |
| `CreatedAtUtc` | `DateTime` | serves `createdAtUtc` and `SortBy=CreatedAt` (see §19) |
| `ArchivedAtUtc`, `DeletionRequestedAtUtc`, `QuarantinedAtUtc`, `QuotaReachedAtUtc` | `DateTime?` | |
| `QuotaReason` | `tinyint?`, enum `NewsFeedQuotaReason` | `1` DailyCap, `2` TextQuota; set and cleared with `QuotaReachedAtUtc` (§6.3) |
| `NextFetchAtUtc` | `DateTime` | worker-owned |
| `RefreshRequestedAtUtc` | `DateTime?` | API sets, worker clears (§3.3) |
| `ValidatorsResetRequested` | `bool` | API sets, worker clears (§3.3) |
| `SuggestedUrl` | `string?` (a URL, see §19) | worker-owned; applied by maintenance or `PUT` (§3.3) |
| `LeaseOwner` | `Guid?` | per process (§3.4 step 2) |
| `LeaseExpiresAtUtc` | `DateTime?` | |
| `CrashSuspectCount` | `byte` | |
| `SettingsVersion` | `Guid`, **concurrency token** | rotated only by API writes (§3.3) |
| `FetchFence` | `Guid`, **concurrency token** | rotated only by the worker (§3.3) |
| `LastFetchAttemptAtUtc`, `LastSuccessAtUtc`, `LastErrorAtUtc` | `DateTime?` | |
| `LastHttpStatus` | `int?` | |
| `LastErrorKind` | `byte` enum | see below |
| `LastErrorMessage` | `string(512)?` | server-composed, host only |
| `ConsecutiveFailures` | `int` | |
| `RedirectedFromHttps` | `bool` | |
| `ETag` | `string(256)?` | printable ASCII only |
| `LastModified` | `string(64)?` | printable ASCII only |
| `LastBodySha256` | `binary(32)?` | |
| `DailyQuotaDate` | `DateOnly?` | |
| `ArticlesToday` | `int` | |
| `ImageBytesToday` | `long` | |

**`LastErrorKind`**: `0` None · `1` Network · `2` Timeout · `3` HttpStatus · `4` TooLarge · `5`
ParseError · `6` BlockedAddress · `7` TooManyRedirects · `8` Gone · `9` Tls · `10` Unsupported · `11`
NotAFeed · `12` Quarantined. Reaching a daily cap is not an error: it is shown through
`QuotaReachedAtUtc` and the `QuotaReached` health.

**`NewsFeedQuotaReason`** is its own enum (`DailyCap = 1`, `TextQuota = 2`), separate from the image
`DeferredReason`, whose ordinal 2 means something else.

- `CK_NewsFeeds_QuotaReason`: `(QuotaReachedAtUtc IS NULL) = (QuotaReason IS NULL) AND (QuotaReason IS
  NULL OR QuotaReason IN (1, 2))`.

### 4.2 `NewsFeedCounters`

`NewsFeedId` PK and FK → `NewsFeeds`, **CASCADE** · `ArticleCount` int · `UnreadCount` int. No
concurrency token. Written by relative increments (§3.5); recounted hourly.

### 4.3 `NewsStorageTotals`

One row. `ImageBytes` long · `TextAndSourceBytes` long. No concurrency token. Written by relative
increments as the last statement of each transaction (§3.5). The image quota and the text quota read it.

### 4.4 `NewsFeedBuckets`

`NewsFeedBucketId` PK · `NewsFeedId` (FK, **CASCADE**, unique) · `CurrentBucket` int ·
`ArticlesInBucket` int · `UpdatedAtUtc`.

### 4.5 `NewsHostThrottle`

`HostKeyHash` `binary(32)` PK · `NextAllowedAtUtc` `DateTime`. The row is its own concurrency token
(§3.6).

### 4.6 `NewsArticles`

| Column | Type / constraint | Note |
|---|---|---|
| `NewsArticleId` | `Guid` PK | also the article's folder name |
| `NewsFeedId` | FK → `NewsFeeds`, **CASCADE** | |
| `Bucket` | `int` | fixed at insert; every file of the article uses it |
| `Title` | `string(1024)` | |
| `Author` | `string(256)?` | |
| `Excerpt` | `string(300)` | the first 300 characters of the plain text, cut at a word boundary. **The only body text in the database** |
| `BodyOmittedReason` | `tinyint?`, enum `NewsBodyOmittedReason` | `1` TooLarge, `2` TooComplex; `null` when the body was stored (§7.10, see §19) |
| `Link` | `string(2048)?` | http(s) only |
| `LinkKeyHash` | `binary(32)?` | |
| `ContentHash` | `binary(32)` | the change detector: the full title, author, link and body (§8.8) |
| `IdentityHash` | `binary(32)` | title plus the first 4,096 body characters |
| `BodyLength` | `int` | characters in the plain-text file |
| `PublishedAtUtc`, `FeedUpdatedAtUtc` | `DateTime?` | |
| `FirstSeenAtUtc`, `LastSeenAtUtc` | `DateTime` | |
| `SortAtUtc` | `DateTime` | `min(coalesce(Published, FirstSeen), FirstSeen + 1 h)`, fixed |
| `ContentChangedAtUtc` | `DateTime?` | |
| `RevisionCount` | `int` | |
| `IsRead` | `bool` | |
| `ReadAtUtc` | `DateTime?` | |
| `FeedVisible` | `bool` | index aid only, eventually consistent, **never** an access check |
| `Language` | `string(35)?` | copied from the feed |

- `CK_NewsArticles_ReadState`: `(IsRead = 0 AND ReadAtUtc IS NULL) OR (IsRead = 1 AND ReadAtUtc IS NOT
  NULL)`.
- A unique index on `(NewsArticleId, NewsFeedId)` backs the identity composite key (§4.7).
- A **`FULLTEXT`** index on `(Title, Author, Excerpt)` (InnoDB), declared in the model with Pomelo's
  `HasIndex(...).IsFullText()` (§11.1).

### 4.7 `NewsArticleIdentities`

`NewsArticleIdentityId` PK · `NewsArticleId` · `NewsFeedId` FK → `NewsFeeds`, **RESTRICT** · `Kind` byte
(`1` Guid, `2` Link, `3` ContentHash) · `IdentityKey` `string(2100)` · `IdentityKeyHash` `binary(32)` ·
`FirstSeenAtUtc`, `LastSeenAtUtc`.

- Feed consistency is a **composite FK** `(NewsArticleId, NewsFeedId)` → `NewsArticles (NewsArticleId,
  NewsFeedId)`, **CASCADE** with the article, backed by the unique index on the article side.
- **Unique** on `(NewsFeedId, IdentityKeyHash)`.
- At most 32 per article (`MaxIdentitiesPerArticle`).

### 4.8 `NewsArticleRevisions`

`NewsArticleRevisionId` PK · `NewsArticleId` FK **CASCADE** · `RevisionNumber` int (1 = the original),
unique with the article · `Title`, `Author`, `Link` · `ContentHash`, `IdentityHash` · `BodyLength` ·
`ValidFromUtc`, `ReplacedAtUtc`.

- Its text and source are **files** (`NewsArticleFiles` with this revision's id).
- The cap is 20 per article, and the original is always kept. A second change within 1 h overwrites
  without a new revision.

### 4.9 `NewsArticleFiles` — the feature's one referencing table

| Column | Type / constraint |
|---|---|
| `NewsArticleFileId` | `Guid` PK |
| `NewsArticleId` | FK → `NewsArticles`, **CASCADE** |
| `NewsArticleRevisionId` | FK → `NewsArticleRevisions`, **CASCADE**, `null` for the current version |
| `Kind` | `byte`: `1` Source, `2` PlainText, `3` Image |
| `StoredObjectId` | FK → `StoredObjects`, **RESTRICT**, unique |
| `ContentType` | `string(64)`: `application/xml`, `application/json`, `text/plain`, `image/jpeg`, `image/png`, `image/webp` |
| `SizeBytes` | `long` |
| `Sha256` | `binary(32)` |
| `CreatedAtUtc` | `DateTime` |

- `CK_NewsArticleFiles_RevisionKind`: a row with a revision id is a Source or a PlainText file.
- The PlainText content type is bare **`text/plain`**. Its encoding is fixed as UTF-8 by this spec, not
  by a parameter, because `PutAsync` maps only bare types to extensions.
- This is the **`StorageFeatureReference`** of `NewsArticles`. `FileStorageMaintenanceTests` already
  fails on any key to `StoredObjects` that is not RESTRICT or does not belong to exactly one feature;
  its pinned foreign-key counts are updated for this table.
- A cascade from a feed or article delete is caught by the store's hourly unreferenced sweep. The feed
  purge also stages releases explicitly (§3.4 step 1).
- A **source lint** allows only the ingestor's namespace to open `Kind = Source` files.

### 4.10 `NewsArticleImages` — image references and state

| Column | Type / constraint |
|---|---|
| `NewsArticleImageId` | `Guid` PK |
| `NewsArticleId` | FK → `NewsArticles`, **CASCADE** |
| `NewsArticleFileId` | FK → `NewsArticleFiles`, **SET NULL**, unique, set when stored |
| `Position` | `int`, append-only, unique with the article; overflow past 999 is skipped |
| `DisplayOrder` | `int` |
| `Origin` | `byte`, 1–6 |
| `SourceUrl` | `string(2048)`, http(s) only, never served |
| `AltText` | `string(512)?` |
| `Width`, `Height` | `int?` |
| `State` | `byte`: `0` Pending, `1` Stored, `2` Failed, `3` Rejected, `4` Skipped, `5` Superseded, `6` Deferred |
| `DeferredReason` | `byte?`: `1` DailyCap, `2` StoreQuota |
| `Attempts` | `byte` |
| `NextAttemptAtUtc` | `DateTime?` |
| `LeaseOwner` | `Guid?` |
| `LeaseExpiresAtUtc` | `DateTime?` |
| `VerifiedAtUtc` | `DateTime?` — stamped by the maintenance probe (§3.4 step 9) |
| `LastRequeuedAtUtc` | `DateTime?` — limits a repair to once per 24 h |

- There is **no** `CHECK` tying `State` to `NewsArticleFileId`, because it would conflict with
  `SET NULL`. The `NewsArticleFileId`/`State` consistency is enforced by the image service and covered
  by a test.

### 4.11 Tags

- **`NewsTags`**: `NewsTagId` PK · `Name(64)` · `NameKey(64)` unique · `Description(256)?` · `Icon(64)?`
  · `Color(7)?` · `ArchivedAtUtc` · `CreatedAtUtc` · `UpdatedAtUtc`. Same shape and guards as
  `JournalTag`.
- **`NewsFeedTags`**: PK · `NewsFeedId` **CASCADE** · `NewsTagId` **RESTRICT** · unique pair.
- **`NewsArticleTags`**: PK · `NewsArticleId` **CASCADE** · `NewsTagId` **RESTRICT** · `SortAtUtc` ·
  unique pair.
  - `SortAtUtc` is a copy of the article's `SortAtUtc`, copied when the link row is inserted
    (`PUT …/tags`), never computed. The article's value never changes after ingest, so it needs no
    maintenance.
  - It serves `IX_NewsArticleTags_Tag_Sort (NewsTagId, SortAtUtc DESC, NewsArticleId)` (§5.3).

### 4.12 `NewsFeedCrashEvents`

`NewsFeedCrashEventId` PK · `LeaseOwner` · `DetectedAtUtc` · `FeedsInFlight` int. There is deliberately
no feed id. Rows are pruned after 7 days.

### 4.13 `CHECK` constraints

| Constraint | Rule |
|---|---|
| `CK_NewsArticles_ReadState` | `(IsRead = 0 AND ReadAtUtc IS NULL) OR (IsRead = 1 AND ReadAtUtc IS NOT NULL)` |
| `CK_NewsArticleFiles_RevisionKind` | a row with a revision id is a Source or a PlainText file |
| `CK_NewsFeeds_QuotaReason` | `(QuotaReachedAtUtc IS NULL) = (QuotaReason IS NULL) AND (QuotaReason IS NULL OR QuotaReason IN (1, 2))` |
| `CK_JobRuns_JobType`, `CK_JobHeartbeats_JobType` | widened to admit `9` |
| `CK_JobRuns_CountersNonNegative` | gains the `NewsFeedJobRun` counter columns |

`CK_JobRuns_HeldReason` is **unchanged**: no new held reason exists.

## 5. API endpoints

General rules:

- Every id is a **`Guid`** (`{id:guid}`). A non-GUID `{id}` is a routing `404`.
- Routes use plural nouns. List models derive from `QueryParams<TSortBy>` and redeclare `Limit` as
  `[Range(1, NewsLimits.MaxPageSize)]`.
- Timestamps must carry a zone.
- When file storage is unavailable to the API (no binding, a degraded volume), only the endpoints that
  **read or write files** return `503 news-storage-unavailable` (`Retry-After: 30`). They are: article
  detail, revisions, image content, and the worker writes. Everything else (lists, counts, read state,
  tags, feeds) keeps working, because it is database-only.
- `503 news-busy` (`Retry-After: 2`) can be returned by **every** endpoint whose query carries the
  `news-query` tag (§8.4): lists, counts, the catalogue, search, mark-read and writes. The key is
  `news-busy` everywhere.
- **A feed pending deletion is `404` everywhere**: feed detail, article detail, revisions and images.

### 5.1 Feeds

1. `GET /api/news-feeds` — `news.read`. `NewsFeedsQueryParams`:
   - `search` (title, custom title, site host; `LIKE` wildcards escaped);
   - `tagId`;
   - **`state`** ∈ `Active` (the default), `Paused`, `Archived`, `All`;
   - **`health`**, a repeatable list;
   - `SortBy` ∈ `Title`, `UnreadCount`, `LastSuccessAt`, `CreatedAt`. Every sort ends with `Id` as a
     tie-break.

   Returns `PagedResult<ExistingNewsFeed>` (`limit` ≤ 200):

   ```json
   { "id": "…guid…", "version": "…guid…", "displayTitle": "Example News", "title": "Example News",
     "customTitle": null, "siteUrl": "https://example.com/", "description": "…", "language": "en",
     "format": "Rss", "fetchIntervalMinutes": 60, "effectiveIntervalMinutes": 60, "downloadImages": true,
     "isActive": true, "archivedAtUtc": null, "createdAtUtc": "…",
     "lastSuccessAtUtc": "…", "lastCheckedAtUtc": "…", "nextFetchAtUtc": "…",
     "health": "Healthy", "quotaReason": null, "unreadCount": 4, "articleCount": 311, "tagIds": ["…"],
     "management": { "url": "…", "lastErrorKind": "None", "lastHttpStatus": 200, "lastErrorAtUtc": null,
       "lastErrorMessage": null, "consecutiveFailures": 0, "quarantinedAtUtc": null,
       "redirectedFromHttps": false, "articlesToday": 12, "imageBytesToday": 3145728 } }
   ```

   - `displayTitle` = `CustomTitle ?? Title ?? host(SiteUrl) ?? "Untitled feed"`. It **never** uses the
     fetch URL's host.
   - **`management` is `null` without `news-feeds.update`.**
   - `quotaReason` is `DailyCap` | `TextQuota` | `null`, non-null exactly when `health` is
     `QuotaReached`.
   - `unreadCount` and `articleCount` come from `NewsFeedCounters`.
   - "Needs attention" is `NewsFeedHealthSets.NeedsAttention` = `Failing`, `Gone`, `Quarantined`,
     `QuotaReached`.
   - **Fetching the full set** (the Feeds page). The client requests consecutive offsets until it holds
     `totalCount` rows: at most 5 requests at the `NewsMaxFeeds` ceiling of 1,000. The loop also stops
     on a short or empty page, and after at most `ceil(NewsMaxFeeds / limit)` requests, so a feed
     deleted mid-loop cannot keep it running. The client removes duplicates by id.
2. `GET /api/news-feeds/catalogue` — `news.read`. Returns every feed not pending deletion, unpaged, as
   `{ id, displayTitle, health, quotaReason, language, unreadCount, articleCount, state }`. It carries no
   URL.
   - `articleCount` is the same maintained counter `ExistingNewsFeed.articleCount` exposes, from
     `NewsFeedCounters`. No new query is needed.
   - The read count is `max(0, articleCount − unreadCount)` per feed, since the hourly recount can
     briefly make the two disagree.
3. `GET /api/news-feeds/{id:guid}` — `news.read`. A feed pending deletion is a `404`. Never writes.
4. `GET /api/news-feeds/unread-counts` — `news.read`. Returns `{ total, byFeed[], byTag[] }`. `byTag`
   counts effective tags. Archived feeds and feeds pending deletion are excluded.
5. `POST /api/news-feeds` — `news-feeds.create`. Takes `{ url, customTitle, fetchIntervalMinutes,
   downloadImages, tagIds }`. The API makes **no** outbound request.
   - `201`.
   - `409 news-feed-duplicate`, with `existingFeedId`.
   - `422` keyed `url`: the scheme, user info, length, a refused IP literal, or a port not granted.
   - `422` keyed `tagIds`.
   - `422 news-feed-limit`. Archived feeds count toward the limit.
6. `PUT /api/news-feeds/{id:guid}` — `news-feeds.update`. Not an upsert.
   - Requires `version` (the feed's `SettingsVersion`). A mismatch is `409 news-feed-changed`.
   - A changed URL sets `ValidatorsResetRequested` (§3.3).
   - Applies or clears a pending `SuggestedUrl` under the §3.3 rule. The response's `url` is the stored
     value.
7. `PUT /api/news-feeds/{id:guid}/tags` — `news-feeds.update`. Replaces the feed's tags.
8. `POST /api/news-feeds/{id:guid}/refresh` — `news-feeds.update`. Returns `202` and sets
   `RefreshRequestedAtUtc`; the feed is due once `max(RefreshRequestedAtUtc, LastFetchAttemptAtUtc +
   60 s) ≤ now` (§3.3). Answers `422 news-feed-inactive` for a feed that cannot be fetched.
9. `POST /api/news-feeds/{id:guid}/archive` and `POST /api/news-feeds/{id:guid}/unarchive` —
   `news-feeds.update`.
10. `DELETE /api/news-feeds/{id:guid}` — `news-feeds.delete`. Returns `202`. The feed must be archived
    first, otherwise `422 news-feed-not-archived`. Sets `DeletionRequestedAtUtc`; the purge is batched
    (§3.4 step 1).
11. `POST /api/news-feeds/{id:guid}/release-quarantine` — `news-feeds.update`. A feed that is not
    quarantined is `422` (`NewsFeedNotQuarantined`).

### 5.2 Articles

12. `GET /api/news-articles` — `news.read`. Parameters:
    - `feedIds` and `tagIds`: repeatable, each at most **50 ids** (`NewsLimits.MaxFilterIds`,
      `[MaxLength]`). An empty list means no filter. **Within a list the ids are OR** (any of the feeds;
      any of the tags, own or inherited from the feed). **Across lists the filters are AND.**
    - `isRead`: a single optional bool. The client sends it only when exactly one of Unread/Read is
      selected.
    - `search`: MariaDB full-text over `(Title, Author, Excerpt)` in boolean mode, with a sanitised,
      bound expression (§8.4);
    - `from`, `to`;
    - `includeArchivedFeeds`;
    - `SortBy` ∈ `Newest`, `Oldest`, `Relevance` (only with `search`);
    - `cursor` (opaque, `[StringLength(512)]`), `offset`, `limit` (`[Range(1, 200)]`; the client uses
      50).

    **Id rules.**
    - Ids are **de-duplicated first**, then the 50-id cap and the unknown-id lookup run.
    - An unknown id is a `422`, keyed `feedIds` or `tagIds`. A feed **pending deletion** counts as
      unknown.
    - Naming an archived feed in `feedIds` includes it whatever `includeArchivedFeeds` says, because the
      caller chose it explicitly. It never gets round the pending-deletion exclusion.
    - An **archived tag** can be used as a filter: its links still exist.

    **Returns `NewsArticlePage`**, its own sealed record, not a `PagedResult<T>` (the shared
    `PagedResult` contract is unchanged; `Odyssey.ApiClient` gets its own method for it):

    ```text
    { items, totalCount: int?, totalCountCapped: bool?, nextCursor: string?, asOfUtc }
    ```

    - Each item is a `NewsArticleSummary`: `{ id, feedId, feedTitle, title, author, excerpt, link,
      publishedAtUtc, sortAtUtc, firstSeenAtUtc, contentChangedAtUtc, revisionCount, isRead, readAtUtc,
      tagIds, feedTagIds, imageCount, language }`.
    - `asOfUtc` is the server clock at the first page.
    - `totalCount` is exact up to 10,000. The query counts with `COUNT(*)` over `LIMIT 10001`, using the
      same index. Above that it returns `10000` with `totalCountCapped: true`. `totalCount` and
      `totalCountCapped` are returned on the first page only (no cursor), and are `null` on later pages,
      so each append does not re-count.
    - `nextCursor` is `null` on the last page.

    **Keyset continuation.**
    - **`Newest` / `Oldest`:** the cursor encodes `(sortAtUtc, id)` of the last row returned. The next
      page is the rows strictly after it in the sort order `sortAtUtc` then `id`. The predicate is
      `SortAtUtc < @s OR (SortAtUtc = @s AND Id < @id)` for `Newest`, mirrored for `Oldest`, written
      expanded (not as a row constructor). It applies to **every** branch of the list query (§5.3).
      Marking rows read, with `isRead=false` as the filter, can then neither skip nor repeat a row.
    - **`Relevance`** (search only) keeps offset paging: the cursor encodes the next offset, and paging
      stops at 10,000 rows. It sorts by score, then `SortAtUtc` descending, then `Id` descending. The
      decoded offset is re-checked against the 10,000 cap. Offset paging there can still skip a row while
      articles are marked read mid-scroll; this is an accepted limitation of search results. The client
      still removes duplicates by id.
    - The cursor also carries `asOfUtc` from the first page. Later pages exclude rows with
      `firstSeenAtUtc > asOfUtc`, so articles ingested mid-scroll do not appear in the middle of the
      list; they appear on the next refresh. **`asOfUtc` is best-effort at the edge**: an article
      ingested around the first page's moment can appear on a later page. It is shown late but never
      skipped.
    - **The cursor is protected** with Data Protection (authenticated encryption,
      `IDataProtectionProvider.CreateProtector("Odyssey.News.ListCursor.v1")`) and bound to the filter
      set: feed ids, tag ids, `isRead`, search, `from`/`to`, `includeArchivedFeeds` and sort.
      - The filter binding hashes the **normalised** values (trimmed search, UTC dates, sorted and
        de-duplicated ids) and leaves out `limit`, so a client may change the page size mid-scroll.
      - It carries no row content, only the sort key, the id, `asOfUtc` and a filter hash. A
        `Newest`/`Oldest` cursor is about 220 base64url characters, hence the 512 bound.
      - A tampered or foreign cursor, one sent with different filters, or one that no longer decrypts
        (key revoked or rotated out) is a `400` keyed `cursor`. The client restarts from a fresh first
        page (#473).
      - **The cursor value is never echoed** in an error body or a log line.
    - `offset` is still accepted without `cursor`, for API clients. Sending both is a `400`.
13. `GET /api/news-articles/{id:guid}` — `news.read`. Returns the summary plus `body`, `images[]`,
    `bodyUnavailable` and `bodyOmittedReason`.
    - `body` is **read from the article's plain-text file** through `OpenReadAsync`, as a bounded stream:
      at most `NewsLimits.MaxBodyLength` (262,144) characters, through a `StreamReader` with a fixed
      buffer, under the request token.
    - `images` lists only the `Stored` images, in display order.
    - If the file cannot be read, the response is `503 news-storage-unavailable`. If the store reports
      the object missing, the response is `200` with `body: null` and `bodyUnavailable: true`, and an
      error is logged.
    - `bodyOmittedReason` is `TooLarge` | `TooComplex` | `null`. When it is set, `body` is `null`: the
      body was refused before parsing (§7.10).
    - A feed pending deletion is a `404`.
14. `GET /api/news-articles/{id:guid}/images/{imageId:guid}/content` — `news.read`. In order:
    1. authorise;
    2. load the image row joined to its article and its feed;
    3. `404` unless the row is **`Stored`**, belongs to this article, the article exists, and the feed
       has `DeletionRequestedAtUtc IS NULL`;
    4. only then open the file;
    5. answer `503` only after all of that has passed.

    - Bytes come from `OpenReadAsync` on this feature only, with `nosniff`, `CORP: same-site`, a strong
      `ETag` (the SHA-256 from the database), `Cache-Control: private, no-cache` and range processing.
    - Revalidation is answered from the database. No path and no source URL are ever sent.
    - **The image `GET` never writes.** If the file is missing, it answers `404` and logs an error, and
      that is all; repair is the worker's (§3.4 step 9).
    - It can return `503 news-storage-unavailable` or `news-busy`. A browser `<img>` cannot read either;
      the client treats any non-`200` image as unavailable (#473).
15. `GET /api/news-articles/{id:guid}/revisions` — `news.read`. Revisions newest first, **metadata
    only**: `revisionNumber`, `title`, `author`, `link`, `validFromUtc`, `replacedAtUtc` and
    `bodyLength`. Raw sources are never returned.
15a. `GET /api/news-articles/{id:guid}/revisions/{number:int}` — `news.read`. Returns **one** revision,
    with its `body`, read from its revision text file as a bounded stream (as #13). Raw sources are never
    returned.
16. `PUT /api/news-articles/{id:guid}/read-state` — `news.update`. Takes `{ isRead }`. Idempotent.
17. `POST /api/news-articles/read-state` — `news.update`. Takes 1–500 ids.
18. `POST /api/news-articles/mark-read` — `news.update`. Accepts the **list's filters** (`feedIds` and
    `tagIds` as body arrays under the #12 id rules, `search`, `from`/`to`, `includeArchivedFeeds`) plus
    the first page's echoed `asOfUtc`, which must be ≤ now + 5 min. The cursor is not involved.
    - Marks **at most 5,000** articles per request, in **tracked chunks of 1,000**, never
      `ExecuteUpdate`.
    - Returns `{ "markedCount": n, "hasMore": bool }`. The client repeats the same request (same filters,
      same `asOfUtc`) while `hasMore` is true; #473 shows this as a single action.
    - An unknown id is a `422` keyed `feedIds` or `tagIds`, as is an `asOfUtc` in the future
      (`NewsMarkReadScopeInvalid`).
    - The confirm's count exists only for **one feed or one tag** and nothing else set; any other
      combination shows no number (#473).
19. `PUT /api/news-articles/{id:guid}/tags` — `news.update`. Replaces the article's own tags.
20. `PUT` / `DELETE /api/news-articles/{id:guid}/tags/{tagId:guid}` — `news.update`. Idempotent; an
    archived tag is a `422`.

### 5.3 The list query plan

A single-id filter reads the sort index in order and stops after `limit` rows. A plain `IN (…)` of 50
ids would read and sort every matching row instead. So:

- **Feeds.** One `UNION ALL` branch per feed id. Each branch is ordered by `(SortAtUtc, Id)` on the
  per-feed sort index, carries the cursor predicate, and has `LIMIT limit`. An outer
  `ORDER BY … LIMIT limit` merges them. The work is capped at 50 × `limit` rows.
- **Tags.**
  - *Inherited* tags expand to the feeds carrying them, which then take the per-feed branches above.
  - *Own* tags use `IX_NewsArticleTags_Tag_Sort (NewsTagId, SortAtUtc DESC, NewsArticleId)`, with one
    branch per tag.
  - The branches merge with the feed branches in the same outer sort. When both a feed list and a tag
    list are set, each branch also checks the other list.
- **Inherited-tag expansion is capped.** If the expanded feed set exceeds 50 feeds, the query instead
  scans the global sort index `(SortAtUtc, Id)` with the filters applied, and stops at `LIMIT`.
- **Merged branches are de-duplicated** by article id before the outer `LIMIT`, because an article can
  match both a feed branch and an own-tag branch.
- **Every branch carries every filter:** `isRead`, `from`/`to`, the pending-deletion exclusion and the
  cursor predicate.
- `Relevance` (search) keeps its `FULLTEXT` plan, with the id lists as filters, under the 10,000-row
  cap.
- Every command is tagged `news-query` and runs under the 5 s `max_statement_time` (§8.4).

### 5.4 Tags

21. `GET /api/news-tags` — `news.read`. **Unpaged**; returns every tag (≤ 500, the tag cap), each with
    `{ id, name, color, archived, feedCount, articleCount }`.
    - `feedCount` and `articleCount` count **direct links only** (`NewsFeedTags`, `NewsArticleTags`),
      the same counts the in-use `409` reports. Articles that inherit the tag from their feed are not
      counted. Each count is one `GROUP BY NewsTagId` over its link table.
22. `POST /api/news-tags` — `news-tags.create`. `409 news-tag-duplicate` for a duplicate name,
    `422 news-tag-limit` at 500 tags.
23. `PUT /api/news-tags/{id:guid}` — `news-tags.update`.
24. `POST /api/news-tags/{id:guid}/archive` and `POST /api/news-tags/{id:guid}/unarchive` —
    `news-tags.update`.
25. `DELETE /api/news-tags/{id:guid}[?detachLinks=true]` — `news-tags.delete`. A tag in use is
    `409 news-tag-in-use` with counts. Detaching needs a claim per link class present
    (`news-feeds.update` for feed links, `news.update` for article links), decided inside the delete's
    transaction (`DomainForbiddenException`).

### 5.5 Status and job

26. `GET /api/news-status` — `file-storage.read` (Admin). Always answers `200`:

    ```json
    { "storage": "Ready", "volume": "primary",
      "articleCount": 0, "feedCount": 0, "quarantinedFeedCount": 0,
      "imageBytes": 0, "imageQuotaBytes": 53687091200, "imageQuotaReached": false,
      "textAndSourceBytes": 0, "textAndSourceQuotaBytes": 21474836480, "textAndSourceQuotaReached": false,
      "volumeFreeBytes": 0,
      "topFeedsByImageBytes": [{ "feedId": "…", "displayTitle": "…", "imageBytes": 0 }] }
    ```

    - `storage` comes from the store's volume state: `Ready`, `Degraded` or `Unconfigured`.
    - `imageBytes` and `textAndSourceBytes` come from `NewsStorageTotals`.
    - `textAndSourceQuotaBytes` is the **resolved** `NewsTextStoreMaxGiB` × 2³⁰, read through the same
      lookup the worker uses (degraded read → min). The client never computes it from the settings page
      or a default.
    - `textAndSourceQuotaReached` is `NewsTextQuota.IsReached(textAndSourceBytes,
      textAndSourceQuotaBytes)` (§6.3).
    - `displayTitle` is the feed's display title (§8.5). It includes the user-written `CustomTitle`;
      `AuthorizationPolicyTests` asserts that every role holding `file-storage.read` also holds
      `news.read`, so the endpoint never shows a title to a caller who could not read the feed.
27. `JobType.NewsFeedFetch = 9` appears on `/background-jobs` (§11.1 item 4).
    - **The manual run** needs `news-feeds.update`. The live `ExclusiveGuard` treats `Trigger = 1` as
      exclusive, so only one manual news run exists at a time. The API inserts the `Running` row,
      unclaimed. The first worker to claim it marks every eligible feed (last attempt ≥ 15 min ago) due
      by setting `RefreshRequestedAtUtc`, in tracked chunks. It then finishes the run as `Succeeded`,
      with `FeedsMarked` as its counter. The ordinary ticks then fetch those feeds under their per-feed
      leases. Under `Testing`, the run executes inline.
    - The news card is gated on `news-feeds.update`. It shows a **warning line** while
      `imageQuotaReached` is true and while `textAndSourceQuotaReached` is true. A full text quota is a
      per-feed transient skip, **not a held reason**.

## 6. Supported file types, images and quotas

### 6.1 File types

| Kind | Content type stored | Extension | Written by |
|---|---|---|---|
| Source (XML formats) | `application/xml` | `.xml` | ingest; the re-serialised item, namespaces kept via `XElement.ReadFrom`, no DTD |
| Source (JSON Feed) | `application/json` | `.json` | ingest; the re-serialised item object |
| PlainText | `text/plain` (UTF-8 by this spec) | `.txt` | ingest; the §8.3 output, UTF-8, LF line ends |
| Image | `image/jpeg`, `image/png`, `image/webp` (still) | `.jpg`, `.png`, `.webp` | image phase; validated and stripped |

**Size limits.** A source file is at most 1 MiB; an item above that keeps no source file, and this is
logged. A plain-text file is at most 262,144 characters. An HTML fragment is at most 2 MiB before
parsing (§7.10). An image is at most `NewsImageMaxSizeKb`.

> SVG is excluded because it is a script-capable document.

### 6.2 Images

1. Only absolute http(s) URLs without user info are fetched. The egress client is used, with a 15 s
   timeout and the size cap while streaming. An allow-list grant applies only to images on the feed's
   own host. Each request takes its host's throttle slot first (§3.6).
2. **The declared type is ignored.** The bytes go through the shared `StillImageValidator` with a news
   policy: magic bytes; jpeg, png or webp; `MaxDimension = 4096`; **`MinDimension = 32`** (a new
   optional member of `StillImagePolicy`, default `null`); non-animated; every `APPn` stripped;
   re-validated. What is stored is the validator's stripped output, so bytes appended after an image's
   end marker are not stored.
3. The image is written with `PutAsync` into **its own article's folder**, as a per-article copy (user
   decision). It is never shared.
4. At most `NewsImageMaxPerArticle` per article; the rest are `Skipped`, and they do not revive later.
   The order is: enclosures, `media:content`, `media:thumbnail`, JSON `image`/`banner_image`, JSON image
   attachments, then inline `<img>`.
5. **Retries.** Back-off at 1 h, 6 h, 24 h and 72 h, then `Failed`. A validation failure is `Rejected`
   at once.
6. Images that an edit drops become `Superseded`. Their files stay in the folder.

### 6.3 Quotas, caps and disk headroom (user decision: soft, no purge)

- **Disk headroom.** The object store enforces only its 1 GiB floor. User uploads pre-check
  `FileStorageReserves.UploadThresholdBytes` (the floor plus 4 GiB). **News writes pre-check
  `UploadThresholdBytes + NewsLimits.ReserveMarginBytes` (1 GiB)** before every `PutAsync`, so news
  stops writing **1 GiB before** user uploads stop, never after. A refused write, or a store `507`, is a
  transient skip, and the job holds with the existing **`StorageUnavailable`** reason. Finance and
  journal uploads keep their headroom.
- **Text-and-source quota.** At `NewsTextStoreMaxGiB`, measured by
  `NewsStorageTotals.TextAndSourceBytes`, new articles are not stored. This is a transient skip, the
  feed reports `QuotaReached` with `QuotaReason = TextQuota`, and nothing is deleted. Revisions of
  existing articles count toward the cap too.
- **Image store quota.** At `NewsImageStoreMaxGiB`, measured by `NewsStorageTotals.ImageBytes`, new
  downloads become `Deferred` (`StoreQuota`, retried hourly). Raising the setting makes them due.
- **Daily image cap.** `NewsFeedMaxImageMbPerDay` defers images until the next UTC midnight
  (`DailyCap`).
- **Daily article cap.** At `NewsFeedMaxArticlesPerDay`, new items are skipped; this is a transient
  skip, so the validators are cleared.
- **Database row growth** is not quota-bound, which is an accepted residual. The backstops are the daily
  article cap, the text quota and the store reserve.

**One predicate per quota.**
- `NewsTextQuota.IsReached(textAndSourceBytes, quotaBytes)` lives in `Odyssey.Core`. The worker's skip,
  the quota-reason recompute and `GET /api/news-status` all call it.
- `NewsDailyCap.IsReached(feed, todayUtc, cap)` lives in `Odyssey.Core` and is
  `DailyQuotaDate == todayUtc && ArticlesToday >= cap`, so yesterday's `ArticlesToday` cannot keep the
  cap set. It serves both the ingest skip and the quota-reason recompute.
- Each host resolves `NewsTextStoreMaxGiB` through its own settings cache, so the worker and the status
  endpoint may disagree for up to the 30 s TTL after a settings save. This is expected (`CLAUDE.md`,
  "Settings reach the worker within the cache TTL").

**When `QuotaReason` is set and cleared.**
- Each fetch completion recomputes `QuotaReachedAtUtc` and `QuotaReason` in its single fenced save
  (§3.3), in this order:
  1. **`TextQuota`** while the text-quota predicate is true;
  2. else **`DailyCap`** while `NewsDailyCap.IsReached` is true for the feed;
  3. else both columns are `NULL`.
- When both apply, `TextQuota` wins: it does not lift at the next UTC day.
- `QuotaReachedAtUtc` keeps its first value while the reason stays the same, and is reset when the
  reason changes.
- The hourly maintenance applies the same rule to feeds not fetched since, so a paused or slow feed does
  not keep a stale reason past UTC midnight or after the quota is raised. **That write is conditional**:
  it updates the columns only `WHERE` the feed holds no live lease and `LastFetchAttemptAtUtc` equals the
  value it read. A pass that read the feed just before a fetch completed then changes nothing, so it
  cannot overwrite that completion's `TextQuota` with `NULL`.
- **`DailyCap` means the article cap only.** The image caps (daily image bytes and the image-store
  quota) never set feed health; they defer images (`DeferredReason`).

**Operator visibility.** At the reference load the 20 GiB text default fills in about 2–4 years
(speculative estimate). When it is reached, every affected feed shows `QuotaReached`, the status panel's
text quota bar reads "full", and the job card shows its warning line. `docs/deployment.md` explains how
to raise it.

## 7. Security, privacy and compliance

1. **Tenancy.** Shared and single-tenant, with access decided by claims. There are no user ids, and
   `users.delete` does not touch news data.
2. **Claims (8 new; 115 → 123).**

   | Claim | Admin | Owner | User | Guest |
   |---|:-:|:-:|:-:|:-:|
   | `news.read` | ✓ | ✓ | ✓ | ✓ |
   | `news.update` | ✓ | ✓ | ✓ | |
   | `news-feeds.create`, `news-feeds.update`, `news-feeds.delete` | ✓ | ✓ | | |
   | `news-tags.create`, `news-tags.update`, `news-tags.delete` | ✓ | ✓ | | |

   `RoleClaimSeeder` reconciles them, so no migration is needed. The status endpoint uses
   `file-storage.read`, and the allow-list uses `system-settings.security.update`.
   `AuthorizationPolicyTests` pins the matrix and asserts that every role holding `file-storage.read`
   also holds `news.read`.
3. **Read exposure.**
   - `management` (the feed URL and error detail) requires `news-feeds.update`.
   - No feed field shows the fetch host to anyone else (`displayTitle` rule).
   - Raw sources, image source URLs, stored-object ids and paths are exposed nowhere.
   - Every returned URL is http(s) without user info, or `null`.
   - Feed `GET` and list endpoints, and the image `GET`, never write.
4. **Write exposure.**
   - Every relationship is set by a scalar id.
   - Nested objects are ignored.
   - No endpoint accepts a path, a file name, a stored-object id or an image URL.
5. **Third-party flow.**
   - Outbound GETs go only to feed and image hosts.
   - The User-Agent is fixed and anonymous.
   - There are no cookies.
   - The browser never contacts publishers.
6. **Secrets.** None.
7. **Audit.** Feed create, update, URL change, automatic rewrite (a `SuggestedUrl` applied), archive,
   delete request, purge completion and quarantine release, and tag delete are logged with ids, the
   acting user id and hosts only. Every allow-list change goes through the security-update registry's
   derived audit line.
8. **Error disclosure.** Problem bodies echo only route ids and `existingFeedId`. The list cursor is
   never echoed. The accepted residual: an IP-literal `422` confirms an allow-list entry, and only Admin
   and Owner hold `news-feeds.create`.
9. **Egress (SSRF).**
   - **Who fetches.** The worker only. The API makes no outbound request.
   - **Client.** `UseProxy = false`. `ConnectCallback` resolves DNS once per connection, applies the
     policy to every resolved address and pins the vetted IP.
   - **Hard deny (never allowed, even when allow-listed).** `0/8`, `::/128`, all of `169.254/16`, all of
     `fe80::/10`, all of `192.0.0/24`, `fd00:ec2::/64`, `100.100.100.200` and `240/4` (reserved; no
     deployment has a reason to reach it), each also checked in its unwrapped form.
   - **Unwrapping.** Mapped, NAT64, 6to4 and Teredo addresses are unwrapped and checked.
   - **Default allow.** Global unicast only. The standard IPv4 and IPv6 exclusions apply (private,
     loopback, unique-local, documentation and reserved ranges), including `2001::/23`.
   - **Allow-list.** `NewsPrivateHostAllowList` grants a host, and a port when the entry names one, only
     for that **feed's own host**. It opens a default-blocked address, never a hard-denied one. It is
     edited under `system-settings.security.update` and read uncached. A degraded read resolves to empty.
   - **Ports.** 80 and 443 unless granted.
   - **Redirects.** Manual, at most 5 hops actually requested, every hop re-checked. A same-host
     `301`/`308` records a `SuggestedUrl` (§3.3); a cross-host one, including a subdomain, does not. An
     https → http downgrade is allowed for feeds only.
   - **Spacing.** At least 2 s between requests to one host across all replicas, for feeds, images and
     redirect hops (§3.6).
   - **Limits.** 30 s per fetch, 10 MiB decompressed, 500 items per document.
10. **Parsing.**
    - **XML.** `DtdProcessing = Ignore`, `XmlResolver = null`, and character caps. A **depth cap of 64**
      is enforced by a wrapping reader that every consumer reads through, the raw capture included.
    - **JSON.** `MaxDepth = 32`.
    - **HTML.** AngleSharp with no loader, no JS and no CSS. Input is at most 2 MiB. An iterative walk is
      capped at 100,000 nodes, depth `NewsLimits.MaxHtmlDepth` (256) and a 2 s deadline.
    - **HTML nesting pre-scan (hard cap, decided §17.1 B).** Before AngleSharp parses any HTML fragment,
      a linear pre-scan (no tree) tracks nesting depth:
      - non-void start tags push, and matching end tags pop;
      - void elements never push;
      - the HTML implied-end-tag set (`p`, `li`, `dt`, `dd`, `option`, `optgroup`, `tr`, `td`, `th`,
        `rp`, `rt`) closes an open element of the same kind rather than nesting.

      If the depth would exceed `NewsLimits.MaxHtmlDepth` = 256 (the same number as the DOM-walk depth
      cap), the fragment is **not parsed**. A fragment over the 2 MiB HTML input cap gets the same
      outcome. There is no large-stack thread fallback.
    - **Outcome of a refused fragment.** The article is still stored (title, author, link, dates, feed).
      Its body text is omitted. The excerpt falls back to the item's other text field (summary vs
      content) if that one passes, else it is empty. The article records `BodyOmittedReason`
      (`TooLarge` for the size cap, `TooComplex` for the depth cap), and the article detail DTO carries
      it as `bodyOmittedReason`. The refusal is logged with the article id only.
    - XML and JSON nested 100,000 deep still fail at their existing depth-capped readers.
11. **Packages.**
    - **AngleSharp** (new, a third-party parser on untrusted input). It runs with the mitigations above,
      and checking it against the advisory database is a pre-implementation gate.
    - In-box: `System.Xml`, `System.Text.Json`, `System.IO.Compression` and `CodePages`.
    - `System.ServiceModel.Syndication` is not used. *From memory, unverified:* its strict date parsing
      fails a whole feed.
    - No SQLite package and no native binary are added.
12. **File system.** All of this is inherited from the object store: no caller-supplied path, re-run
    structural, containment and symlink checks, opaque names, `OpenReadAsync` scoped to its feature, and
    the store's integrity scrub and quarantine. File names are GUIDs, so no publisher or user text is
    ever in a path.
13. **Feed URLs at rest.** An accepted residual (user decision). Feed URLs, including any query tokens,
    are plaintext in MariaDB and its backups. They are hidden from readers and logged by host only. A
    private feed's item links and enclosure URLs can carry tokens too: item links are visible to
    `news.read` holders, and source URLs are never served. Source files keep private-feed tokens as
    well. All of this is stated in `docs/deployment.md`.
14. **Worker blast radius.** The worker runs untrusted parsers and holds the Data Protection key ring
    read-only. Mitigations:
    - compose already runs it non-root, with a read-only root filesystem, `/tmp` on `tmpfs`,
      `cap_drop: [ALL]` and `no-new-privileges`;
    - it uses no proxy;
    - NuGet audit is a build gate;
    - **network isolation** (§11.3): the worker is on exactly `db_worker` and `egress`, and shares no
      network with `api` or `client`. This stops the worker from reaching `api` and `client` directly.
      **The SSRF control against every other destination is still the address policy** (item 9); the
      network split is defence in depth, and the spec and `docs/deployment.md` say so. If an operator
      adds their own networks, the worker must never share one with `api`;
    - **memory**: the worker's 1 GiB limit is shared with ImageMagick. News bounds its own peak (4
      fetches × 10 MiB bodies, 2 MiB HTML, one image at a time per slot) at **≤ 200 MiB**.

    The accepted residual: a parser compromise could read the key ring. A key-less fetcher container is
    the v2 option.
15. **Personal data.** Bylines are publisher-published personal data. The household exemption probably
    applies (*speculative, not legal advice*). A feed delete erases the feed's data and files, the files
    after the deletion grace.
16. **Raw source on disk.** The raw item is untrusted XML or JSON stored as a file. It is never served
    by any endpoint, never opened by the API, and only ever read back by a future, separately specified
    re-derivation job. A source lint allows only the ingestor's namespace to open `Kind = Source` files.
17. **The list cursor.** Low risk: it carries only a sort key, an id, a timestamp and a hash, and every
    filter it binds is already within `news.read`. It is authenticated-encrypted so it cannot be forged
    or replayed against other filters.

## 8. Validation and mapping rules

### 8.1 DTO annotations (`Odyssey.Dtos/Internet/`, `sealed record`)

Every bound names a constant in **`NewsLimits`** (`Odyssey.Dtos` root). The client reads the same
constants.

| Constant | Value |
|---|---|
| `MaxUrlLength` | 2048 |
| `MaxCustomTitleLength` | 256 |
| `MinFetchIntervalMinutes` / `MaxFetchIntervalMinutes` | 15 / 1440 |
| `MaxPageSize` | 200 |
| `MaxRevisions` | 20 |
| `MaxTags` | 500 |
| `MaxTagIdsPerRequest` | 32 |
| `MaxFilterIds` | 50 |
| `MaxTagNameLength` | 64 |
| `MaxTagDescriptionLength` | 256 |
| `MaxBodyLength` | 262144 |
| `MaxHtmlDepth` | 256 |
| `BucketCapacity` | 10000 |
| `MaxIdentitiesPerArticle` | 32 |
| `MinSearchTermLength` | 3 |
| `ReserveMarginBytes` | 1 GiB |

| DTO.Property | Annotation(s) |
|---|---|
| `NewNewsFeed.Url` | `[Required]`, `[StringLength(MaxUrlLength)]`, plus `NewsFeedUrlRules` in the service |
| `NewNewsFeed.CustomTitle` | `[StringLength(MaxCustomTitleLength)]` |
| `NewNewsFeed.FetchIntervalMinutes` | `int?`, `[Range(Min…, Max…)]` |
| `…TagIds` (feed and article tag writes) | `[MaxLength(MaxTagIdsPerRequest)]`, `Guid` |
| `UpdateNewsFeed.IsActive` | `[Required] bool?` |
| `UpdateNewsFeed.Version` | `[Required] Guid?` |
| `MarkNewsRead.AsOfUtc` | `[Required] DateTimeOffset?` |
| `MarkNewsRead.FeedIds` / `TagIds` | `[MaxLength(MaxFilterIds)]`, `Guid` |
| `NewsArticleReadState.IsRead` | `[Required] bool?` |
| `NewsArticlesReadStateBulk.ArticleIds` | `[Required]`, `[MinLength(1)]`, `[MaxLength(500)]` |
| `NewNewsTag.Name` / `Description` | `[StringLength(MaxTagNameLength, MinimumLength = 1)]` / `[StringLength(MaxTagDescriptionLength)]` |
| `NewNewsTag.Icon` / `Color` | `[TagIcon]` + `TagIconGuard` / `[HexColor]` + `TagColorGuard` |
| Query `Search` | `[StringLength(ListDefaults.MaxSearchLength)]` |
| Query `Limit` | redeclared `[Range(1, MaxPageSize)]` |
| Query `From` / `To` | `DateTimeOffset?` |
| Query `FeedIds` / `TagIds` | `[MaxLength(MaxFilterIds)]`, `Guid` |
| Query `Cursor` | `[StringLength(512)]` |

### 8.2 URL keys (`NewsUrlKey`, in `Odyssey.Core`, server-only)

- **`UrlKey`.** Trim. Require an absolute `http`/`https` URL with no user info. Lower-case the scheme and
  host, map IDN hosts to punycode (ICU), and drop the default port and the fragment. Remove the
  parameters `utm_*`, `fbclid`, `gclid`, `mc_cid`, `mc_eid` and `_hsenc`, keeping the order of the rest.
  It is stored as `UrlKeyHash` (SHA-256).
- **`LinkKey`.** The same steps, then the scheme and a leading `www.` are removed. It is stored as
  `LinkKeyHash`.
- The worker image adds `icu-libs` and `icu-data-full` and drops `InvariantGlobalization`.
  `GlobalizationGuard` moves to `Odyssey.Core`, so both hosts call it, and it stops the host when ICU is
  missing. A test asserts that the API and the worker produce identical keys.

### 8.3 HTML → plain text

1. Run the nesting pre-scan and size check (§7.10); a refused fragment stops here. Run the entity
   pre-pass (§8.6). Parse with AngleSharp, dropping `script`, `style`, `noscript`, `template`, `iframe`,
   `object`, `embed`, `svg`, `math` and `form` along with their content.
2. Walk the tree iteratively.
   - A block element produces a paragraph break.
   - `br` produces a line break.
   - `li` is prefixed with `• `.
   - Table cells are separated by ` | `.
3. A link becomes `text (url)`, but only when the URL is absolute http(s) and differs from the text. Any
   other scheme keeps the text only.
4. `<img>` produces no text. Its `src` and `alt` go to the image list.
5. Decode entities, then apply the text sanitiser (§8.7).
6. Collapse whitespace. Paragraphs are separated by exactly one blank line, and runs of blank lines
   collapse.
7. Truncate on a paragraph boundary to at most `MaxBodyLength`, including the trailing `\n\n[…]`.
8. Plain-text sources skip step 1.
9. Body sources, in order of preference: Atom `content`, then `content:encoded`, then JSON
   `content_html`, then JSON `content_text`, then `description`/`summary`.

The result is written to the article's **PlainText file**. Only the 300-character excerpt is stored in
the database.

### 8.4 Search (MariaDB `FULLTEXT`)

v1 search covers **title, author and excerpt** only (decided, §17.1 A).

**Terms.**
- The input is split into words of Unicode letters and digits only. All other characters are
  separators.
- A term is dropped when it is shorter than `NewsLimits.MinSearchTermLength` (3, matching
  `innodb_ft_min_token_size`), or when it is in the InnoDB default stopword list, held as a constant,
  `NewsSearchStopwords`.
- At most 8 terms are kept.
- **Each term is emitted as `+word*`** in boolean mode, so all terms are required and match as prefixes.
  No quotes and no other operators ever appear.
- If no term is left, the request is a `400` keyed `search`: "Use words of at least 3 letters."
- The expression is a **bound parameter** and is never concatenated into SQL.

**The query seam.**
- `INewsArticleSearch.Apply(IQueryable<NewsArticle>, terms)` returns an **`IQueryable`**.
  - Relational: `EF.Functions.Match(...)` (Pomelo).
  - EF InMemory: a `LIKE` predicate, used only under InMemory, the same shape as the existing lookups.
- `Relevance` orders by the match score on relational (then `SortAtUtc` descending, then `Id`
  descending). On InMemory it falls back to newest first, and that is documented.
- The real behaviour is covered in `Odyssey.IntegrationTests` (AC 28).

**Time limit on every news query.**
- A `DbCommandInterceptor` applies `SET STATEMENT max_statement_time=5 FOR` to every command tagged
  `TagWith("news-query")`. That covers article lists (including the tag and feed branches), feed lists,
  counts, the catalogue, mark-read chunks and writes, as well as search.
- `SetCommandTimeout(6)` is the client-side backstop.
- `ER_STATEMENT_TIMEOUT`, or a lock wait timeout, maps to `503 news-busy`.

**Concurrency limit.**
- `NewsSearchLimiter` follows the `LocationSearchGlobalLimiter` pattern: at most 4 concurrent searches
  globally, at most 2 of those slots per user, and 30 per minute per user.
- Over the limit, the answer is `429`, with `Retry-After`.

### 8.5 Mapping

- `displayTitle` is `CustomTitle ?? Title ?? host(SiteUrl) ?? "Untitled feed"`.
- Enums serialise as strings through `TolerantEnumConverter`, each with an `Unknown` member at 255.
- Dates are UTC with a `Z` suffix.

### 8.6 Format detection, entity pre-pass and field mapping

- **Detection** reads the body and never trusts `Content-Type`.
  - Decoding is by BOM, then the XML declaration or charset, then UTF-8. The code-page provider is
    registered.
  - A body starting with `{` is JSON Feed when its `version` matches `https://jsonfeed.org/version/1…`;
    otherwise it is `NotAFeed`.
  - A body starting with `<` is typed by its root element: `rss` is RSS, `feed` in the Atom namespace is
    Atom, `rdf:RDF` with the RSS 1.0 or 0.90 namespace is RDF. Anything else is `NotAFeed`.
  - No RDF resource is ever fetched.
- **The entity pre-pass** maps HTML5 named entities to numeric references. It is table-driven, aware of
  CDATA and comments, and has no DTD.
- **Field mapping:**

  | Internal field | RSS 2.0 | RSS 1.0 (RDF) | Atom | JSON Feed |
  |---|---|---|---|---|
  | Feed title | `channel/title` | `channel/title` | `feed/title` | `title` |
  | Site link | `channel/link` | `channel/link` | `link[@rel=alternate]` | `home_page_url` |
  | Feed TTL | `ttl` | `sy:updatePeriod` ÷ `sy:updateFrequency` | — | — |
  | Item GUID | `guid` | `item/@rdf:about` | `id` | `id` (a number becomes a string) |
  | Item link | `link` | `link` | `link[@rel=alternate]`, else the first `link` | `url`, else `external_url` |
  | Title | `title` | `title` | `title` | `title`, else `''` |
  | Author | `author` / `dc:creator` | `dc:creator` | `author/name` | `authors[0].name`, else `author.name` |
  | Published | `pubDate` (RFC 822, tolerant) | `dc:date` | `published`, else `updated` | `date_published` |
  | Updated | — | — | `updated` | `date_modified` |
  | Base URL | `xml:base`, else item link | `xml:base`, else item link | `xml:base`, else item link | item `url`, else `home_page_url` |

### 8.7 Sanitising and clamping

**Text sanitiser.** `NewsText.Clean` runs on every string derived from a feed:
- NFC normalisation;
- C0 and C1 controls are removed, except `\n`; `\t` becomes a space;
- these are removed: U+200B–200F, U+202A–202E, U+2060–2064, U+2066–2069, U+061C, U+FEFF, U+FFF9–FFFB,
  tag characters U+E0000–E007F, and unpaired surrogates;
- U+2028 and U+2029 become `\n`.

**URL sanitiser.** Only an absolute http(s) URL with no user info and at most 2048 characters is kept.
Anything else becomes `NULL`, or an image `Rejected`.

**Clamping.** Every bounded column is clamped before insert, on a code-point boundary:
- titles and other short text are cut with `…`;
- the body follows §8.3;
- the excerpt is cut at a word boundary;
- a GUID longer than 2000 characters becomes `g#<sha256>`;
- an invalid `ETag` or `Last-Modified` is dropped.

### 8.8 Identity and content hash

**Hashes.**
- `ContentHash` = SHA-256 of `Clean(title)`, author, link and the **whole** body. It is the change
  detector.
- `IdentityHash` = SHA-256 of title plus the first 4,096 body characters. It is the identity used in
  `h:`.
- Both are computed in memory before the file is written.

**Candidate keys.** **G** (`g:` or `g#`), **L** (`l:<LinkKey>`), **H** (`h:<IdentityHash>`). Before
matching, each fetch marks GUIDs that repeat within it as *unreliable*, and LinkKeys that repeat within
it as *shared*.

**Resolution rules:**
1. A reliable known G is that article.
2. A reliable unknown G is a **new** article, unless its L is known, unshared and maps to exactly one
   article *A*, and none of *A*'s GUIDs appear in this fetch. Then the item is *A* (the feed changed its
   GUID).
3. With no reliable G but a usable L, a known L wins. Otherwise a known H, when title plus body is at
   least 280 characters. Otherwise the item is new.
4. With no reliable G and no usable L, H is authoritative at any length.

**Attaching keys.** A key is attached only when no other article owns it and it is not shared or
unreliable in this fetch. At most 32 keys per article. Key lookups use `IdentityKeyHash`.

**Accepted:** two genuinely distinct short items with no GUID, no link and identical text are merged
into one.

## 9. Error handling and failure modes

### 9.1 API failure classes

| Class | Status | Code / key |
|---|---|---|
| `NewsStorageUnavailable` | 503 + `Retry-After: 30` | `news-storage-unavailable`. Only on detail, revisions and image content: no binding, a degraded volume, or a file read I/O error |
| `NewsBusy` | 503 + `Retry-After: 2` | `news-busy`: `max_statement_time` or a lock wait timeout, on any `news-query` endpoint |
| Search rate limit | 429 + `Retry-After` | `NewsSearchLimiter` over its limits |
| Validation | 400 | field-keyed, including `search` (no usable term), `cursor` (tampered, foreign, filter mismatch, undecryptable) and `cursor` + `offset` together; 51+ ids in `feedIds`/`tagIds` |
| Not found (feed, article, tag, image of another article, image not `Stored`, feed pending deletion) | 404 | — |
| `NewsFeedDuplicate` | 409 | `news-feed-duplicate` + `existingFeedId` |
| `NewsFeedConcurrency` | 409 | `news-feed-changed`, on a `SettingsVersion` conflict |
| `NewsTagDuplicate` / `NewsTagInUse` | 409 | `news-tag-duplicate` / `news-tag-in-use` + counts |
| `NewsTagLimitReached` | 422 | `news-tag-limit` |
| `NewsFeedUrlRejected` | 422 | key `url` |
| `NewsFeedLimitReached` | 422 | `news-feed-limit` |
| `NewsFeedNotArchived` / `Inactive` / `NotQuarantined` | 422 | `news-feed-not-archived` / `news-feed-inactive` / as named |
| `NewsTagNotUsable` | 422 | key `tagIds` |
| `NewsMarkReadScopeInvalid` | 422 | unknown or pending-deletion id in `feedIds`/`tagIds` (keyed by the list), or `asOfUtc` in the future |
| Unknown filter id on the list | 422 | key `feedIds` or `tagIds` |
| Detach without the per-class claim | 403 | generic |

### 9.2 Worker failure handling

- **A feed fails:** recorded on the feed; the run moves on to the next feed.
- **An item fails:** that item is skipped. Deterministic failures are retried on the next 3 full parses,
  then abandoned with a log line.
- **The file store refuses a write** (the news headroom threshold, the reserve or a degraded volume): a
  transient skip. The validators are cleared and the job holds with `StorageUnavailable`, the existing
  held reason. The worker also honours `StorageCheckFailed` from its storage verdict.
- **The text quota is reached:** a per-feed transient skip, reported as `QuotaReason = TextQuota`, not a
  job hold.
- **The lease is lost or deletion is requested mid-fetch:** the fetch aborts, writes nothing more, and
  clears its lease when it still can.
- **The worker crashes or a parser hangs:** the lease expires, and the crash-event accounting in §3.4
  step 7 applies.
- **Graceful stop:** never counted as a crash.
- **A purge fails:** the batch rolls back and resumes on the next tick.
- **An orphan file** (written, but the save failed): the store's abandoned-write sweep removes it. A
  `Committed` row always has its file.
- **A refused HTML fragment:** the article is stored without a body (§7.10).

### 9.3 Retry and back-off

| Event | Next fetch |
|---|---|
| Failure | `now + min(interval × 2^min(failures, 6), 24 h)` ±10 % |
| `Retry-After` | honoured, clamped to [interval, 24 h] |
| `410` | the feed is deactivated |
| `301`/`308` | same host: `SuggestedUrl` recorded (§3.3); the redirect is followed |
| `302`/`307` | followed, not rewritten |
| Redirect hop to a throttled host | transient skip, `now + 1 min`, not a failure |

At startup, overdue feeds are spread over the next 10 minutes.

## 10. Performance targets

> Note: these are design estimates, not measurements (speculative). Reference load: 200 feeds, ~1,000
> items/day, ~1 M articles after ~3 years.

| Scenario | Target (P95) | Mechanism |
|---|---|---|
| Newest unread, all feeds, 50 rows | < 100 ms | index `(IsRead, FeedVisible, SortAtUtc DESC, NewsArticleId)` |
| By feed | < 100 ms | index `(NewsFeedId, IsRead, SortAtUtc DESC, NewsArticleId)` |
| By tag (effective), or feed + tag | < 150 ms | per-feed branches for inherited tags, `IX_NewsArticleTags_Tag_Sort` branches for own tags, merged by `ORDER BY … LIMIT` (§5.3) |
| Cursor page, 50 feeds and 50 tags set, 1 M articles | ≤ 300 ms | the §5.3 per-id top-N merge; a recorded benchmark with the hardware stated, not a CI timing assertion |
| Unread counts / feed list | < 50 ms | maintained counters (`NewsFeedCounters`) |
| Search, 3 terms | < 300 ms | `FULLTEXT (Title, Author, Excerpt)` |
| Article detail including body | < 150 ms | one row plus one file read of ≤ 1 MiB through the store |
| Mark read: single / 500 / 5,000 (one request) | < 50 / 250 / 500 ms | — |

- Worker transactions hold row locks for ≤ 250 ms.
- Each fetch is capped at 30 s for the network plus a 60 s ingest budget (the 90 s deadline). Each item
  has a 2 s deadline, each image 15 s, and the image phase 20 s per tick.
- Every list query is checked with **`EXPLAIN` on MariaDB** against a generated store of 1 M articles,
  90 % unread, with archived feeds holding 80 % of the unread (integration tier, category
  `Performance`). The CI gate for a cursor page is the plan check (no filesort); the latency figure is a
  recorded benchmark.
- **Storage (speculative).** About 15–30 KiB per item of text plus source, so ≈ 5–10 GiB a year. Text
  and source are bounded by `NewsTextStoreMaxGiB` (20 GiB by default). Images are bounded by the quota
  (50 GiB by default) and the daily caps, and they are per-article copies with no deduplication (user
  decision).

## 11. Database migration and deployment

### 11.1 One additive migration on `OdysseyContext`: `AddNewsModule`

```bash
dotnet ef migrations add AddNewsModule \
  --project "./Odyssey.Context" \
  --startup-project "./Odyssey.Api/Odyssey.Api.csproj" \
  --context OdysseyContext
```

The migration contains:

1. **Tables:**
   - `NewsFeeds`, `NewsFeedCounters`, `NewsStorageTotals`, `NewsFeedBuckets`, `NewsHostThrottle`,
     `NewsArticles`, `NewsArticleIdentities`, `NewsArticleRevisions`, `NewsArticleFiles` and
     `NewsArticleImages`;
   - `NewsTags`, `NewsFeedTags` and `NewsArticleTags`;
   - `NewsFeedCrashEvents`.

   Each has the keys, `CHECK` constraints and indexes listed in §4 and §16.
2. **The `FULLTEXT` index** on `NewsArticles (Title, Author, Excerpt)`, declared with Pomelo's
   `HasIndex(...).IsFullText()`, so it is part of the model and visible to `CreatedBy`. **No change to
   `MigrationRunner`** is needed.
3. **Nine `SystemSettings` rows** with `HasData`. Each follows the full `CLAUDE.md` recipe, with its
   pair in `SystemSettingsBounds`:

   | Setting | Default | Bounds | Claim | Degraded read |
   |---|---|---|---|---|
   | `NewsDefaultFetchIntervalMinutes` | 60 | 15–1440 | `system-settings.update` (see §19) | **max** (slower polling) |
   | `NewsMaxFeeds` | 200 | 1–1000 | `system-settings.update` (see §19) | min |
   | `NewsImageMaxPerArticle` | 5 | 0–20 | `system-settings.update` (see §19) | min |
   | `NewsImageMaxSizeKb` | 5120 | 64–10240, and ≤ the derived ceiling | `system-settings.update` (see §19) | min |
   | `NewsImageStoreMaxGiB` | 50 | 1–10000 | `system-settings.update` (see §19) | min |
   | `NewsTextStoreMaxGiB` | 20 | 1–1000 | `system-settings.update` | min |
   | `NewsFeedMaxArticlesPerDay` | 500 | 10–5000 | `system-settings.update` (see §19) | min |
   | `NewsFeedMaxImageMbPerDay` | 200 | 1–10240 | `system-settings.update` (see §19) | min |
   | `NewsPrivateHostAllowList` | `""` | ≤ 32 entries, ≤ 2048 characters | `system-settings.security.update` | empty (blocks every private address) |

   **`NewsImageMaxSizeKb` has a derived ceiling.**
   - `NewsImageSizeCeiling.Kb(maxFileSizeBytes)` = `Math.Max(SystemSettingsBounds.NewsImageMaxSizeKbMin,
     maxFileSizeBytes / 1024)`, cast to `int`, lives in `Odyssey.Core`. The floor is the bound constant,
     not a literal `64`, so a deployment with a very small file-size limit can still save a value.
   - The `PUT` validates the setting against `RequestCapCeilings` (in `Odyssey.Api`), which calls the
     helper. This is a derived bound, so it is validated in the service, not by `[Range]`.
   - The ceiling is published as `SystemSettingsDto.NewsImageMaxSizeKbCeiling` (int), so `/settings`
     bounds the field.
   - The worker's read-side clamp calls the same helper, as a backstop.
4. **Background-job schema:**
   - `CK_JobRuns_JobType` and `CK_JobHeartbeats_JobType` admit **9**;
   - `CK_JobRuns_CountersNonNegative` gains the `NewsFeedJobRun` counter columns;
   - a `NewsFeedJobRun` TPH subtype carries `FeedsFetched`, `ItemsStored`, `ItemsRevised`,
     `ImagesStored`, `FeedsQuarantined` and `FeedsMarked` (the manual run's counter), plus its detail
     DTO and its `JobRunDetailJsonConverter` case;
   - `ExclusiveGuard` is **unchanged**: 9 is not added to its `IN` list, because the per-feed leases are
     the guard;
   - **`CK_JobRuns_HeldReason` is unchanged**, since no new held reason exists.

   Outside the schema: `BackgroundJobsService.NewManualRun` and `HasWorkAsync` gain cases for 9, and the
   `JobCatalogue` entry is:

   | Field | Value |
   |---|---|
   | `Tick` | 30 s |
   | `StaleAfter` | 15 min |
   | `ManualRunSupported` | `true` |
   | Card | gated on `news-feeds.update` |
   | Manual run | needs `news-feeds.update` |

   `HasWorkAsync` for type 9 is true when any feed is due, any image is `Pending` with
   `NextAttemptAtUtc ≤ now`, or any feed is pending deletion. The client presentation is in #473.

**Not in this migration:**
- **Storage feature registration.** `NewsArticles` is registered in DI. `StoredObjects` needs no schema
  change, and the feature key is data.
- **Role claims.** They need no migration; `RoleClaimSeeder` reconciles them.

### 11.2 Rules that apply because production data exists

- **This migration is additive only.** It changes no existing table except the job `CHECK` widening.
- **`Down()`** first deletes the `JobRuns` and `JobHeartbeats` rows for type 9, then narrows the
  `CHECK`s, then drops the tables. Any news data is then lost. The news `StoredObjects` rows **remain**,
  and then trigger the B4 Critical log ("a live row of an unknown feature"); they are not quarantined.
- **Release notes** say: delete every news feed and wait out the deletion grace before rolling back.
  Files outlive a feed delete by the deletion grace (168 h by default). An operator with their own
  bindings must add `FileStorage__Bindings__NewsArticles`; otherwise startup check B1 fails with
  `UnboundFeature`.

### 11.3 Deployment changes

- Bind `FileStorage__Bindings__NewsArticles` (default `primary`) for `api` and `worker` in
  `docker-compose.yml` and the AppHost. `FileStorageDeploymentParityTests` checks it.
- The worker image gains `icu-libs` and `icu-data-full` and drops `InvariantGlobalization`.
- The worker's compose hardening already exists. The compose additions are
  **`stop_grace_period: 30s`** for the worker and the network topology below.
- **Network topology.** `docker-compose.yml` has no `networks:` key today. It gains per-consumer
  internal networks:

  | Network | Internal | Members |
  |---|---|---|
  | `db_api` | yes | `mariadb`, `api` |
  | `db_worker` | yes | `mariadb`, `worker` |
  | `db_migrations` | yes | `mariadb`, `migrations` |
  | `egress` | no | `worker` only |

  - `api` and `client` stay on the default network for user traffic.
  - The worker is on **exactly** `db_worker` and `egress`, and shares no network with `api` or
    `client`.
  - A compose test asserts that the worker's networks are exactly `{db_worker, egress}` and that no
    network contains both `api` and `worker`.
  - `docs/deployment.md` describes the change, what it protects and what it does not (§7.14).
- **No feature toggle.** Claims gate the feature.

## 12. Acceptance criteria

1. An RSS 2.0 fixture and an Atom fixture from a local allow-listed server are both `Healthy` after one
   tick. Each article has a Source file and a PlainText file at
   `news/<feedId>/1/<articleId>/<storedObjectId>.{xml,txt}`.
2. Every endpoint answers `403` without its claim. The role matrix is pinned in
   `AuthorizationPolicyTests` and covered in E2E.Api.
3. `management` is `null` for a caller with only `news.read`. With only `news.read`, no field carries the
   fetch host; the fixture's `siteUrl` host differs from it.
4. Each failure class in §9.1 is produced by a test.
5. Each §8.1 annotation rejects out-of-range input with a `400`. A non-GUID `{id}` is a routing `404`.
6. With two feeds and two tags set, the list returns articles from either feed that carry either tag,
   own or inherited. Mark read with the same filters marks exactly those.
7. **Files and storage:**
   - every file has a `Committed` `StoredObject` with feature `NewsArticles`, opaque naming and a GUID
     file name;
   - no publisher-supplied name appears in any `RelativePath`;
   - `OpenReadAsync` for a news object through another feature fails.
8. Nested objects in request bodies create or change nothing.
9. A tag delete returns `409` with counts. A detach without `news.update` while article links exist
   returns `403` and changes nothing.
10. **Feed delete:**
    - `DELETE` on an archived feed returns `202` and hides the feed at once;
    - the purge removes every row in time-boxed chunks and stages a release for every
      `NewsArticleFile`;
    - after the deletion grace, the store has removed the files;
    - an API write made during the purge succeeds.
11. Re-fetching an unchanged feed stores no row and writes no file. A `304` is not parsed.
12. **Identity:**
    - a changed GUID with the same link, while the old GUID is absent from the fetch, is matched;
    - a new GUID that shares a link with an item whose GUID is still present is a new article;
    - in a feed where every item shares one link, each GUID is its own article, across 3 fetches;
    - an http → https move of a GUID-less feed creates no duplicates;
    - an item with no GUID, no link and fewer than 280 characters is stored once across 3 fetches;
    - two items in one fetch with the same GUID are both stored;
    - GUID-less items sharing a link and under 280 characters are each stored once across 3 fetches.

    The `IdentityKeyHash` unique index never fires on the ordinary path.
13. Republishing with ≥ 280 characters matches by hash. Below that, it is a new article.
14. **An edit:**
    - creates revision 1, re-labels the old Source and PlainText files to it, and writes new current
      files, all in one transaction (§3.5);
    - leaves read state, tags and `SortAtUtc` unchanged;
    - on the 21st edit keeps revision 1;
    - when only the body's tail or the link changes, is detected and keeps the identity.
15. A future `pubDate` gives `SortAtUtc = FirstSeen + 1 h`.
16. Item isolation: a 10,000-character GUID, a 5 MB title and an invalid date are all stored.
17. XXE and billion-laughs fixtures read no file and make no request, and memory stays under 64 MB.
18. **Deep nesting.** HTML nested 100,000 deep is refused by the pre-scan without parsing, and the
    process survives. HTML nested 256 deep parses. 257 is refused with `TooComplex`. XML and JSON nested
    100,000 deep still fail at their existing depth-capped readers (the raw-capture path included).
19. **Crash handling:**
    - a crash fixture leaves the feed's next fetch ≥ 1 h out;
    - three solo crash events quarantine the feed, and release clears it;
    - three crash events within 1 h trip the breaker;
    - three SIGTERMs with 4 feeds in flight quarantine nothing.
20. **Two workers:** a feed is never fetched by both. API writes during a fetch (a `PUT`, a refresh, a
    mark-read) never fail the worker's completion and never count as a crash. A refresh requested
    mid-fetch survives completion. A user's URL change made during a fetch takes effect. An 80 s fetch
    renews its lease and is never treated as expired.
21. **Egress:**
    - **Blocked by default**, opened only by an allow-list grant for the feed's own host: `127.0.0.1`,
      `10.0.0.1`, `fd00::1`, `192.0.2.1`, `2001:2::1`, `::ffff:10.0.0.1`, `64:ff9b::a00:1`,
      `2002:0a00:0001::1`, and a Teredo address that embeds `10.0.0.1`. Without a grant, each one is
      `BlockedAddress`. With a grant, it succeeds **only for that feed**. AC 1's loopback test server is
      this case.
    - **Never allowed (hard deny)**, `BlockedAddress` even when allow-listed: `0.0.0.0`, `::`,
      `169.254.169.254`, `169.254.1.1`, `fe80::1`, `192.0.0.192`, `fd00:ec2::254`, `100.100.100.200`
      and `240.0.0.1`.
    - An allow-listed private host is refused for an image referenced by another feed, and for a
      redirect coming from another host.
    - **Proxy:** with `HTTPS_PROXY` set, the target is still checked.
    - **DNS rebinding:** the connection goes only to the vetted IP.
    - **Redirects:** the 6th redirect gives `TooManyRedirects`; an http → https `301` on the same host
      leads to the stored URL being rewritten (through `SuggestedUrl`); a `301` to another host,
      including a subdomain, does not; an https → http redirect for an image is refused.
    - **Gzip bomb:** aborted at 10 MiB.
22. With two workers, two feeds on the same host are never requested less than 2 s apart.
23. An `ETag` or `Last-Modified` containing CR/LF or non-ASCII is never stored or sent.
24. HTML-to-text fixtures produce exactly the expected text, with sanitiser coverage. `javascript:` and
    `data:` links are stored as `NULL`.
25. **Images:**
    - JPEG, PNG and WebP are stored stripped, verified by MetadataExtractor;
    - GIF, SVG, animated WebP, images of 16×16 or smaller and images over the cap are `Rejected`;
    - the **same image in two articles is stored twice**, once in each article folder (user decision);
    - repositioning after an edit never collides.
26. The image endpoint is scoped to its article, sends `nosniff`, `CORP: same-site`, a strong `ETag` and
    `no-cache`, and answers `304` without opening the file.
27. Article detail reads the body from the PlainText file. With a degraded volume it returns
    `503 news-storage-unavailable`, and the list, counts, read state and tags keep working.
28. **Search (integration tier, real MariaDB):**
    - FTS operators in the input never cause a `500`;
    - the expression is bound;
    - each term rule in §8.4 holds, including a stopword in the input;
    - `%_` in a feed search matches literally;
    - `max_statement_time` interrupts with `503 news-busy`;
    - a 2-letter-only query is a `400` naming the minimum;
    - a word that appears only in the body (past the excerpt) is **not** found, as documented.
29. `mark-read` with the list's filters, including feed plus tag, marks exactly what the list showed, up
    to the echoed `asOfUtc`.
30. **Quotas:**
    - with free space between the news threshold and the upload threshold, a finance file upload
      succeeds while news writes are refused;
    - at `NewsTextStoreMaxGiB`, no new article is stored;
    - at `NewsImageStoreMaxGiB`, images are `Deferred`;
    - nothing is deleted in any of these cases.
31. A raw XML item using namespaces declared at the feed root re-parses on its own.
32. The API and the worker produce identical keys for IDN and NFC fixtures. A windows-1252 feed decodes.
33. **The fixture corpus** (at least 14 fixtures) parses:
    - RSS 2.0 with `content:encoded`, RSS 2.0 with every item sharing one link, and RSS 0.91;
    - Atom with `xml:base`;
    - a malformed `pubDate`, `media:content`, and entities including `&nbsp;`;
    - a feed with no GUIDs;
    - RSS 1.0 with `dc:*` and `sy:*`, and RSS 0.90;
    - JSON Feed 1.0, JSON Feed 1.1, a numeric JSON `id`, and `content_text` only.
34. Format detection ignores `Content-Type`, and no RDF resource is ever fetched.
35. A read article that is edited stays read.
36. `AddNewsModule` applies cleanly to an empty database and to a production-shaped one.
37. Startup check B1 fails without a `NewsArticles` binding. `FileStorageDeploymentParityTests` covers
    the binding.
38. The nine settings pass the settings guard tests. A degraded allow-list blocks every private address.
39. Both compose files run the worker hardened, and the health checks pass.
40. CI: a cursor page with 50 feeds and 50 tags plans with no filesort, and returns each article once.
    Benchmark (recorded, not a CI gate): 1 M articles, p95 ≤ 300 ms.
41. **Buckets:**
    - a feed's 10,001st article lands in bucket 2;
    - two workers ingesting the same feed concurrently never overfill a bucket;
    - all of an article's later files share its folder.
42. The status endpoint answers `200` with the store's volume state in every condition.
43. **Polyglot.** An image with bytes appended after its end marker is stored as the validator's
    stripped output, with the appended bytes absent, and a second validation pass accepts it.
44. Feed and image claims, the purge, mark-read with filters and the counter writes all run under EF
    InMemory, with no `ExecuteUpdate` or `ExecuteDelete` anywhere on these paths.
45. An edit and a within-1-hour overwrite never raise a duplicate key, and never leave two current
    Source or PlainText files.
46. Detail, revisions and images for a feed pending deletion all return `404`.
47. The search limiter returns `429` above its limits, and a timed-out search returns `503 news-busy`.
48. `totalCount` is capped at 10,000 and sets `totalCountCapped`.
49. `revisions` returns no bodies. `revisions/{n}` returns one.
50. Worker memory stays at or below its news budget (200 MiB) under a 4-feed load with 10 MiB bodies.
51. A lease renewal during ingest: no conflict, and every item batch is saved.
52. A parser that hangs past 90 s counts as a crash event once its lease expires.
53. A same-host `301` sets `SuggestedUrl`. It is applied by the hourly maintenance step or a feed `PUT`,
    never by a `GET`, and a duplicate is dropped.
54. The image `GET` never writes. Maintenance re-queues a missing image at most once per 24 h, from
    either source (store integrity findings or the bounded probe), and a reader's `GET` never triggers a
    re-queue.
55. `mark-read` caps each request at 5,000 and sets `hasMore`. Repeating it marks exactly the matching
    set.
56. `NewsImageMaxSizeKb` above the ceiling (`MaxFileSizeBytes / 1024`) is rejected on `PUT`, and the
    ceiling is published.
57. A failed edit transaction leaves no row pointing at the new files. The store sweeps them.
58. `GET /api/news-status` returns `textAndSourceQuotaBytes` equal to the resolved setting,
    `textAndSourceQuotaReached` true at the quota, and `displayTitle` on every top-feed entry.
59. A fetch completion at the text quota sets `TextQuota`, also when the article cap holds too. One at
    the article cap only sets `DailyCap`. Neither sets the reason when only an image cap holds. The
    hourly maintenance clears `DailyCap` after UTC midnight and `TextQuota` after the quota is raised. On
    real MariaDB the CHECK rejects a mismatched pair and `QuotaReason = 3`.
60. A full text quota writes no held reason; the news card shows the warning line.
61. `SystemSettingsDto.NewsImageMaxSizeKbCeiling` equals `MaxFileSizeBytes / 1024`.
62. The worker, the status endpoint and the quota-reason recompute call `NewsTextQuota.IsReached`
    (source-lint: no second comparison).
63. With `MaxFileSizeBytes` below 64 KiB, the ceiling is 64. The `PUT` check and the worker clamp both
    use `NewsImageSizeCeiling`.
64. `AuthorizationPolicyTests`: every role holding `file-storage.read` also holds `news.read`.
65. A `PUT` with an unchanged `url` and a pending suggestion returns the suggested URL. A `PUT` with a
    changed `url` returns the user's value and clears the suggestion.
66. With `isRead=false`, marking the first page's rows read and then requesting `nextCursor` returns the
    next unseen rows. Nothing is skipped or repeated, for both `Newest` and `Oldest`.
67. An article ingested well after the first page (beyond the commit edge) is not returned by later
    cursor pages, and appears on a fresh first page. No article is ever skipped.
68. A modified cursor, or one sent with a changed filter, is a `400` keyed `cursor`. Sending `cursor`
    together with `offset` is a `400`.
69. `totalCount` is present only on the first page. `Relevance` paging stops at 10,000 rows with
    `nextCursor: null`.
70. Every issued `nextCursor` round-trips through `[StringLength(512)]`. A cursor protected under a
    revoked key is a `400` keyed `cursor`.
71. `GET /api/news-tags` returns all 500 tags at the cap in one response.
72. 51 ids in `feedIds` or `tagIds` is a `400`. An archived feed named in `feedIds` is included with
    `includeArchivedFeeds=false`.
73. Duplicate ids are collapsed before the cap. A pending-deletion feed id is a `422`. An archived tag
    filters normally.
74. Every catalogue entry's `articleCount` equals that feed's `ExistingNewsFeed.articleCount`.

**Test tiers.**
- `Odyssey.Core.Tests` and `Odyssey.Api.Tests` (EF InMemory) cover the services, the `LIKE` search
  fallback and the API contract. `OdysseyApiFactory` already binds every file feature to its
  per-factory temp volume, and `NewsArticles` is added to that set.
- `Odyssey.IntegrationTests` (real MariaDB) covers `FULLTEXT`, the leases and both tokens, the bucket
  locking, the FK behaviour, the `CHECK`s, the query plans and the migration.

## 13. LLM prompt/output contract

Not applicable — AI is out of scope for v1.

## 14. Rollout plan

### Phase 1 (MVP)

- `AddNewsModule`, the entities and the `NewsArticles` storage feature with its bucket allocator.
- The services and endpoints #1–#27 (with #15a), plus the typed clients, including the
  `NewsArticlePage` method.
- `NewsFeedJob` (purge, fetch, ingest, images, maintenance), multi-replica.
- The 8 claims, the 9 settings, `NewsLimits`, `StillImagePolicy.MinDimension`, and the shared Core
  helpers (§3.1).
- The HTML nesting pre-scan (§7.10).
- The compose binding, `stop_grace_period`, the network topology, and worker ICU.
- Documentation updates.

Later work is listed in §18.

## 15. Answers to the design brief

**Q1 — Deduplication.** §8.8. A stable GUID is authoritative. The link and hash are fallbacks under
stated conditions. Every key an article has ever had is kept, looked up by hash.

**Q2 — Edits.**
- An unchanged `ContentHash` is a no-op.
- A changed one writes the new current files first, then, in one transaction, moves the current Source
  and PlainText **files** to a new revision row by re-labelling their `NewsArticleFiles` rows with the
  revision id, and inserts the new current file rows. No existing file is rewritten (§3.5).
- Read state, tags and `SortAtUtc` are kept. The client shows "updated".
- At most one revision per hour, and at most 20, with the original always kept.
- Images that an edit drops become `Superseded`. Their files stay in the folder.

**Q3 — Storage specifics.**
- Metadata is in MariaDB. Each article's files are on the existing object store, under
  `news/<feedId>/<bucket>/<articleId>/`, with opaque GUID names.
- One additive migration, and no second chain.
- Concurrency uses per-feed leases with a `FetchFence` token for the worker and a `SettingsVersion`
  token for the API, so worker replicas are safe.
- Backups follow the existing database-plus-volume order in `docs/deployment.md`. There is no extra
  procedure.

**Q4 — Full-text search.** MariaDB `FULLTEXT` over title, author and excerpt, in boolean mode, with
sanitised and bound terms. The full body is not searchable in v1, because it lives in a file (decided,
§17.1 A); full-body search is future work.

**Q5 — Feed tags vs article tags.** Tags are stored independently and inherited at query time; `tagIds`
and `feedTagIds` are returned separately.

**Q6 — Indexes.** §16.

**Raw content.** Kept as the article's Source file, plus one per revision. It is never exposed.

## 16. Index list (MariaDB)

| Table | Index | Serves |
|---|---|---|
| `NewsFeeds` | `UNIQUE (UrlKeyHash)` | duplicates |
| `NewsFeeds` | `(IsActive, ArchivedAtUtc, DeletionRequestedAtUtc, QuarantinedAtUtc, NextFetchAtUtc)` | claiming due feeds |
| `NewsFeeds` | `(LeaseExpiresAtUtc)` | crash detection |
| `NewsFeedCounters` | PK `(NewsFeedId)` | counters |
| `NewsFeedBuckets` | `UNIQUE (NewsFeedId)` | the allocator |
| `NewsHostThrottle` | PK `(HostKeyHash)` | per-host spacing |
| `NewsArticles` | `(NewsFeedId, IsRead, SortAtUtc DESC, NewsArticleId)` | **unread per feed**; the per-feed branches of the list query; purge chunks |
| `NewsArticles` | `(IsRead, FeedVisible, SortAtUtc DESC, NewsArticleId)` | **global unread / newest** |
| `NewsArticles` | `(SortAtUtc DESC, NewsArticleId)` | all, newest first; the capped-expansion scan |
| `NewsArticles` | `(NewsFeedId, IsRead, FirstSeenAtUtc)` | the mark-read watermark |
| `NewsArticles` | `UNIQUE (NewsArticleId, NewsFeedId)` | the identity composite FK |
| `NewsArticles` | `FULLTEXT (Title, Author, Excerpt)` | **search** |
| `NewsArticleIdentities` | `UNIQUE (NewsFeedId, IdentityKeyHash)`; `(NewsArticleId)` | dedup |
| `NewsArticleRevisions` | `UNIQUE (NewsArticleId, RevisionNumber)` | history |
| `NewsArticleFiles` | `UNIQUE (StoredObjectId)`; `(NewsArticleId, NewsArticleRevisionId, Kind)` | files |
| `NewsArticleImages` | `UNIQUE (NewsArticleId, Position)`; `(NewsArticleId, DisplayOrder)`; `(State, NextAttemptAtUtc)`; `UNIQUE (NewsArticleFileId)` | images, queue |
| `NewsTags` | `UNIQUE (NameKey)`; `(ArchivedAtUtc)` | — |
| `NewsFeedTags` / `NewsArticleTags` | `UNIQUE (owner, tag)`; `(NewsTagId, owner)` | **feeds/articles of a tag**; the tag counts |
| `NewsArticleTags` | `IX_NewsArticleTags_Tag_Sort (NewsTagId, SortAtUtc DESC, NewsArticleId)` | own-tag branches of the list query |
| `NewsFeedCrashEvents` | `(DetectedAtUtc)` | the breaker window |

MariaDB has no partial indexes, so the `IsRead`-leading composites play the role of partial unread
indexes. AC 40 asserts the plans.

## 17. Decisions, assumptions and open questions

### 17.1 Decided

- **A. Full-article-text search is out of v1.** v1 search covers title, author and excerpt (summary)
  only. Full-body search is a later version (§18).
- **B. Pathological HTML nesting gets a hard cap.** A linear pre-scan refuses any HTML fragment whose
  nesting would exceed `NewsLimits.MaxHtmlDepth` = 256, or that exceeds the 2 MiB input cap, before
  AngleSharp parses it. The article is stored without its body and reports `bodyOmittedReason`
  (`TooLarge` | `TooComplex`). There is no large-stack thread. Full rule in §7.10, test in AC 18.

### 17.2 Resolved with the user

- MariaDB plus the file store; no separate SQLite store.
- Folders `news/<feedId>/<bucket>/<articleId>/`, with ≤ 10,000 articles per bucket and opaque GUID
  names.
- Plain text on disk only; the database keeps title, author, a ≤ 300-character excerpt, hashes and
  metadata. Per-article image copies, not deduplicated. A multi-replica worker.
- Edits never change read state. RSS 1.0, RSS 0.90 and JSON Feed are supported. Images are kept forever
  in v1. No milestone.
- Plaintext feed URLs are accepted. A soft quota plus daily caps, with no purge.
- Unavailable menu actions are omitted (#473). Feed and tag filters combine.
- The News list **scrolls** like the other card pages (`OdsInfiniteList`), with no pager, so the list is
  fetched in keyset pages (§5.2).
- Every News filter is a multi-select (`OdsMultiSelect`), so the feed and tag filters take several
  values (`feedIds`, `tagIds`).

### 17.3 Assumptions

1. The `NewsArticles` feature is bound to the same volume as the other features (`primary`) by default.
2. The excerpt (≤ 300 characters) in the database is acceptable as "metadata". The body of record is the
   PlainText file (user decision).
3. Feed URLs are plaintext at rest (accepted), and a worker compromise could read the key ring
   (accepted).
4. Ids are GUIDs, as everywhere else in Odyssey.

### 17.4 Open questions

None. The two questions earlier drafts carried (AngleSharp recursion and full-body search) are decided
in §17.1.

## 18. Future work (not committed)

- **Full-body search**, for example a separate searchable text column (which would duplicate the body in
  the database) or an external index.
- Full-page extraction for summary-only feeds, which would read the stored Source file.
- A separately specified re-derivation job over the stored Source files.
- OPML import and export, and clickable links.
- A key-less fetcher container.

## 19. Conflicts found while merging

These were not settled by the source order, or the sources left a gap the merged text had to fill.
Each needs a reviewer's confirmation.

1. **A URL change's next fetch time.** The original `PUT` text says a changed URL "clears the validators
   and sets `NextFetchAtUtc = max(now, LastFetchAttemptAtUtc + 15 min)`". The later column-ownership
   rule makes `NextFetchAtUtc` and the validators worker-owned and has a URL change set only
   `ValidatorsResetRequested`. The merged doc follows the later rule, so a URL change **no longer
   reschedules** the feed and the 15-minute floor for URL edits is gone; the AC that tested it (URL
   `PUT` respecting 15 min / 60 s spacing) was replaced by the two-worker spacing AC without restating
   it. Decide whether a URL change should also set `RefreshRequestedAtUtc` (with a 15-minute floor) or
   just keep the existing schedule.
2. **Which process applies `SuggestedUrl` in "the hourly maintenance step".** The source says the API
   applies it "through the hourly maintenance step, using an API-side save", while the only hourly
   maintenance step defined is the worker's `NewsFeedJob` phase. The security review asked for the
   process to be named; the follow-up restricted *when* it is applied (maintenance or `PUT`, never a
   `GET`) but not *where* it runs. The merged doc lists it under the worker's maintenance (§3.4 step 9)
   and keeps "API-side save" for the uniqueness check and `SettingsVersion` rotation, which conflicts
   with "the worker never writes `SettingsVersion`" if the worker runs it.
3. **"AC 5 amended" (multi-select filters).** The multi-select addendum says it amends AC 5, but AC 5
   (old numbering) is about DTO annotations and routing `404`s, with nothing to amend. The merged doc
   keeps the original AC 5 and adds the multi-select criterion as AC 6, next to it. It may have meant
   old AC 27 (mark-read with filters), now AC 29.
4. **The egress AC "replaced by the two lists".** The source marks the egress AC replaced by the
   default-blocked and hard-deny address lists. The merged doc reads that as replacing only the old
   single address list, and keeps the other egress cases (cross-feed grant, proxy, rebinding, redirects,
   gzip bomb) in AC 21.
5. **An edit "in one save" vs two saves.** Old AC 13 says an edit does its re-label and new files "all
   in one save"; the later edit-ordering rule uses one transaction with two saves. AC 14 now says "one
   transaction".
6. **The `240/4` range.** The original default-allow rule listed `240/4` as an ordinary exclusion; the
   later egress list moved `240.0.0.1` into the hard-deny set, citing `240/4` as reserved. The merged
   §7.9 hard-denies all of `240/4`.
7. **Memory budget AC.** The source says AC 37 (old numbering, compose hardening) asserts the 200 MiB
   news memory budget, while old AC 48 is the criterion that states it. The merged doc points at AC 50
   (old 48) and leaves AC 39 (old 37) as written.

8. **Column ownership is incomplete.** The ownership rule lists the worker's columns and gives "the API
   everything else", but the worker also writes `QuarantinedAtUtc` (on quarantine), `RedirectedFromHttps`,
   `CrashSuspectCount`, `DailyQuotaDate`, `ArticlesToday` and `ImageBytesToday`, and
   `release-quarantine` is an API write to `QuarantinedAtUtc` and the crash count. The merged §3.3 adds
   only what later text assigns explicitly (`SuggestedUrl`, the quota columns). Which token
   `release-quarantine` rotates, and how it avoids racing the worker, is not stated.

Gaps filled or left open while merging:

9. **`NewsFeeds.CreatedAtUtc`** is not in any source table, but `ExistingNewsFeed.createdAtUtc` and
   `SortBy=CreatedAt` need it. Added.
10. **`SuggestedUrl` type and length** are not stated. The merged table says "a URL"; `string(2048)?`
   like `Url` is the obvious reading.
11. **The claim for seven of the nine settings** is stated only for `NewsTextStoreMaxGiB`
    (`system-settings.update`) and the allow-list (`system-settings.security.update`). The others are
    shown as `system-settings.update`, inferred.
12. **Persisting `bodyOmittedReason`.** Decision B puts it on the detail DTO, so the merged doc adds
    `NewsArticles.BodyOmittedReason` (`tinyint?`, `1` TooLarge, `2` TooComplex). Not stated: whether a
    PlainText file is written at all for an omitted body (AC 1 says every article has one), what
    `BodyLength` and the hashes use, whether a revision can carry the reason, and whether the cap also
    applies to HTML in non-body fields such as an Atom `type="html"` title.
13. **The identity composite FK's delete behaviour.** The source replaces "consistency enforced by the
    service" with the composite FK but does not restate the on-delete rule. The merged doc gives it the
    article's `CASCADE` and keeps the separate `NewsFeedId` → `NewsFeeds` `RESTRICT` key.
14. **The full `NewsFeedHealth` enum** is defined only in an earlier draft that is no longer available.
    The merged doc names the members the sources use (`Healthy`, `Failing`, `Gone`, `Quarantined`,
    `QuotaReached`) but not their ordinals or any others.
15. **`NewsArticleImages.Origin` 1–6** has no member list. The six image sources in §6.2 are the likely
    mapping.
16. **The `GET /api/news-tags` shape** (`{ id, name, color, archived, feedCount, articleCount }`) leaves
    out `description` and `icon`, which the entity carries and the writes take. Kept as stated.
17. **The `429` search limit** has no problem `code`.
18. **`NewsStorageTotals`** has no stated primary key.

Dropped while merging (superseded by later text):

- The `-r<n>` revision file names, the generated-column unique index for one current Source/PlainText
  file, and `CK_NewsArticleImages_Stored`.
- The image `GET` resetting a missing image to `Pending` (moved to worker maintenance), and a feed `GET`
  applying `SuggestedUrl`.
- The 10-minute lease renewed every 2 minutes (now 3 minutes, every 60 s), the single-statement claim of
  up to 20 feeds (now tracked per-feed claims up to free slots), and the single `Version` token.
- The `MigrationRunner` `FULLTEXT` drift-guard change and the `FULLTEXT` half of the migration AC.
- `SUM(NewsArticleFiles.SizeBytes)` as the image quota measure (now `NewsStorageTotals`).
- Mark-read in chunks of 5,000 with no per-request cap (now 5,000 per request in chunks of 1,000).
- `NewsArticlePage` as a `PagedResult<T>`, and the `limit` field a review proposed for it (the adopted
  shape has none).
- `feedId` / `tagId` single filters (now `feedIds` / `tagIds`).
- From the old quotas AC (replaced wholesale): "the daily caps defer, and the items return the next UTC
  day even with an unchanged ETag", "10,000 deferred rows do not starve `Pending` ones", and "a store
  reserve refusal holds the job with `StorageUnavailable` and leaves API reads working".
- From the old `EXPLAIN` AC (replaced wholesale): "the counters equal a recount".
- From the old spacing AC (replaced wholesale): "URL `PUT` and manual runs respect the 15 min, 60 s and
  per-host spacing" (see item 1).
- Keyset paging from the "later" list (now implemented).
- References to the earlier drafts' text, to the superseded SQLite store and its lifecycle endpoints
  (`/api/news-store/status`, `/api/news-store/reinitialise`) and error codes (`news-store-unconfigured`,
  `news-store-lost`, `news-store-unavailable`, `news-store-busy`).
- Review notes not adopted by the spec text: treating a reservation swept before the edit save as a
  transient skip, and the reviewer's alternative index for an inherited-tag expansion above 50 feeds (the
  spec scans `(SortAtUtc, Id)` instead).

## 20. Acceptance criteria mapping (old → new)

Old numbers are those used across the issue body and its comments.

| Old AC | New AC |
|---|---|
| 1–5 | 1–5 |
| 5 (the multi-select amendment) | 6 |
| 6–16 | 7–17 |
| 16b | 18 |
| 17–72 | 19–74 (old *n* → new *n* + 2) |

Notable entries: old 18 → 20 (two workers), 19 → 21 (egress), 26 → 28 (search), 28 → 30 (quotas),
38 → 40 (query plans), 41 → 43 (polyglot), 48 → 50 (memory), 52 → 54 (image repair), 57 → 59 (quota
reason), 72 → 74 (catalogue count).
