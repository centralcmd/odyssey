# The news reader

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**The news reader fetches untrusted feeds from the worker, and its egress policy is the SSRF control**
(issues #470, #473; specs in `docs/specs/news-reader-*.md`). Six rules are easy to get backwards:

- **Every outbound request goes through `NewsEgress`** (`Odyssey.Core/Internet/News/Fetching/`): one DNS answer
  per connection, every address vetted at connect time (wrapped IPv4 forms unwrapped), manual redirects re-checked
  per hop, decompression capped in layers and bytes. A private host is reachable only through the
  `NewsPrivateHostAllowList` setting, gated on `system-settings.security.update`, and only for that feed's own host.
  Never send news traffic through another `HttpClient`.
- **The worker shares no network with the API or the client.** It sits on its own database network and the
  `egress` network only, in both compose files; a fetched host must never be able to reach the API's port.
- **The worker needs ICU** (`GlobalizationGuard`, now in `Odyssey.Core/Configuration`, runs in both hosts).
- **`NewsArticles` must be bound on every host with volumes**, the API and the worker alike.
- **The API never writes a worker-owned column.** `NewsFeedFetchStates` is the worker's, behind `FetchFence`; the
  API changes it only through `INewsFetchStateFlags`, and a suggested feed URL is applied only by a user's `PUT`.
- **Feed HTML is reduced to text and never re-serialised**, behind a 256-level and 2 MiB cap per fragment, so no
  feed markup ever reaches the browser.

**A podcast is a news feed with `Kind = Podcast`, and its audio is worker-fetched, quota-reserved and graced**
(issue #556; operator notes in `docs/deployment.md` → *Podcasts*). Rules that are easy to get backwards:

- **Episodes are articles.** `PodcastEpisodes`, `PodcastEpisodeAudio` and `PodcastChapters` are 1:1 or 1:n with
  `NewsArticles`. The article stream and the episode list filter on the `NewsArticles.FeedKind` copy (it is what lets
  them use `IX_NewsArticles_Kind_Sort`), but only through `NewsFeedKindRewrite`: a feed whose type changed is
  *pending* until the purge loop records in `NewsFeedFetchStates.FeedKindRewrittenAtUtc` that it rewrote every article,
  and a reader lets a pending feed's articles through by id. The join to the feed row stays authoritative. A podcast
  feed named in `feedIds` is in the article stream on purpose.
- **Every write to `PodcastEpisodeAudio` rotates `AudioFence`, and the API never touches a `Downloading` row.** The
  API's request, cancel and type-change writes go through `IPodcastAudioRequests` (conditional updates); its one tracked
  write, the user remove of `Stored` audio, is fenced like the worker's saves, so a download claiming the row meanwhile
  turns it into `409 podcast-audio-busy`. Stored audio is
  released only through `PodcastAudioRelease` (graced, never immediate), by the user remove, the type-change cleanup,
  the retention job and the purge.
- **One download per feed and per host across replicas is the generated guard columns' unique indexes**
  (`FeedDownloadGuard`, `HostDownloadGuard`), MariaDB only; the claim's read-side exclusion is the InMemory fallback.
  An indexed generated column over a `CHAR` key needs `RTRIM(…)` or MariaDB refuses it (error 1901).
- **Every reserved byte is returned on every exit path**, in the save that ends the download, and the hourly recount
  rebuilds `AudioReservedBytes` from live leases. The quota counts stored, graced and reserved bytes.
- **Audio goes through `NewsStreamFetch`, never `NewsHttpFetch`**: identity encoding enforced, a 10-hop redirect cap,
  time-bounded rather than buffer-bounded. The chapter-marker reader (`PodcastAudioChapters`) is managed, bounded and
  polls its deadline in every loop; it must never throw out of the job.
- **Last-played is stamped only after one response delivered the whole file to a `news.update` holder**, which is why
  the client nginx streams `…/audio/content` unbuffered. Auto-delete counts from it, so a stamp on a partial transfer
  would delete audio nobody has.
