# Exchange rates

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Exchange rates sync from Norges Bank, stored as the published currency → NOK direction only, and every
other pair converts through NOK** (issues #349, #472). `ExchangeRate.Provider` is `NULL` for a manual row and
`NorgesBank` for a fetched one; there is no separate source column. The sync writes one row per observation
and never the reciprocal: the *StoreFetchedRatesInPublishedDirectionOnly* migration (folded into the v0.51.0 baseline) deleted every fetched `NOK → X` row that
had its `X → NOK` partner (manual and partnerless rows were kept), and readers invert instead. Rules that are easy to get backwards:

- **The newest-rate ordering is written once, in `ExchangeRateOrdering`** — `AsOf`, then
  `UpdatedAt ?? CreatedAt`, then a stored row before an inverted one, then the id — and source plays no
  part. `CurrencyConversionService` (direct vs. hop: the later UTC date wins, the same date goes to direct)
  and every status rule in `ExchangeRateService` read it; a source-lint fails on a second copy in
  `Odyssey.Core`.
- **Readers invert FETCHED rows, never manual ones** (issue #472). A fetched `B → A` row competes for
  `A → B` as `1 / Rate`, unrounded, through a second, fetched-only branch whose `Provider` filter runs
  *before* its grouped MAX, so a newer manual row of the same stored pair cannot hide it. A manual rate may
  be a buy or sell quote and is never turned around. On a full tie the stored row wins, which is what keeps
  a database still holding both directions giving exactly the results it gave before. A row's Current
  badge, the `status=` filter and the summary count only a winner that is a stored row **of its own pair**:
  an inverted winner names the other pair's row and must never mark it Current. `GET
  /api/exchange-rates/resolved` reports the rate a conversion uses and its legs; `/latest` returns an
  inverted row with `inverted: true` and the stored row's id. `CK_ExchangeRates_RatePositive` keeps the
  division safe.
- **"In force" means `AsOf ≤ now`.** A future-dated row is `Upcoming` and converts nothing until its
  date; the bounded lookups keep their exclusive `AsOf < bound`.
- **The run guard is the `JobRuns.ExclusiveGuard` generated column's unique index**, real MariaDB only; a purge
  takes it too, through a `Purge` run row. Runs execute in `Odyssey.Worker` (`ExchangeRateSyncJob`), which
  keeps no schedule in memory. The API only *starts* a manual run: it inserts the `Running` row unclaimed,
  and the first worker replica to set `ClaimedAtUtc` — a concurrency token — executes it. A scheduled run
  or a purge is claimed by whoever inserts it, and so is a manual run with nothing to do, which the API
  finishes `Skipped` at once. A request no worker claims within `UnclaimedRequestTimeout` (2 minutes) is
  abandoned, so a missing worker cannot hold the guard for the whole stale window. Under `Testing` no
  worker runs, so run-now claims and executes inline.
- **A fetched row is never edited in place nor deleted on its own** (both `422` keyed `source`, for
  every caller) — the sync fills only beyond its fetched extents, so a row removed from inside them is
  never refetched. Fetched rates leave only through the per-currency purge.
