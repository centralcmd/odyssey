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
