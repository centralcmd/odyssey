# News Reader — Frontend Specification

| | |
|---|---|
| **Status** | Approved |
| **Source issue** | [#473](https://github.com/centralcmd/odyssey/issues/473), the issue body and every spec amendment posted on it, merged into one document |
| **Backend counterpart** | [`docs/specs/news-reader-backend.md`](news-reader-backend.md) (issue #470). It owns the API contract |
| **Visual reference** | The design system artifact, https://claude.ai/artifact/VkrcragM9HGn1cXKkANizr, **version 227** |

Odyssey gets a new **Internet** module, and its first feature is a news reader for plain syndication
feeds (RSS, Atom and JSON Feed). This document is the frontend half: the reader at `/news`, feed
management at `/news-feeds`, news tags at `/news-tags`, a News status panel on `/background-jobs` and a
News group on `/settings`. The reader is a scrolling list of expandable record cards. Expanding a card
shows the plain-text body and an image carousel in one reading panel, and marks the article read, with
an Undo in the snackbar and an untimed **Mark unread** in the card's `⋯` menu. Every endpoint, field,
status code and claim named here is defined in the backend spec, and this document never restates
them. Where this document and the design system disagree on visuals, the design system wins; on
behaviour, accessibility and security, this document wins.

---

## 1. Overview

The worker fetches the subscribed feeds on a schedule, strips each item to plain text and downloads the
item's images. News lives in MariaDB, and each article's source, plain text and images are files on the
existing file store. The household shares one feed list, one read/unread state and one set of news
tags.

### 1.1 Primary user value

- Read every subscribed site in one place inside Odyssey, self-hosted, with the data under the
  household's control.
- Articles are kept indefinitely, and up to `NewsLimits.MaxRevisions` (20) earlier versions of an
  edited article are retained, the original always among them. A publisher's later edit or removal
  does not erase what was fetched.
- No third-party content runs in the browser. Text is plain and images are served from Odyssey, so
  reading generates no tracking requests to publishers.
- Tag feeds and articles with a dedicated news tag set. Tags also do the job of favourites.

## 2. Goals and Non-Goals

### 2.1 Goals

1. A new **Internet** module with three pages: `/news` (the reader), `/news-feeds` (feed management)
   and `/news-tags` (news tags).
2. The reader is a scrolling list of **expandable record cards** (user decision). Expanding a card
   shows the plain-text body and an image carousel, and **marks the article read** (user decision).
   Undo is offered in a snackbar and is always available as **Mark unread** in the card's `⋯` menu.
3. Reader filters, in the standard filter bar: search (titles, authors and summaries), Feed, Tag and
   Status (Unread / Read), with sorting and a "Load N at a time" size. An overview of unread and read
   counts, and an **Attention** list of feeds that need attention. **Mark all read** for exactly the
   current filters. **Hide read** to clear just-read cards (user decision).
4. Article actions: mark read or unread, the article's own tags, earlier versions, and copy or open
   the original link.
5. Feed management: subscribe, edit, pause, resume, archive, unarchive, delete, refresh now and
   release quarantine; feed tags; feed health shown as text.
6. News tags: list, create, edit, archive, delete, including the in-use delete with counts and detach.
7. Admin surfaces: a News status panel and job card on `/background-jobs`, and a **News** group of nine
   rows on `/settings`.
8. New client pieces (§4.1) and four changed shared components (§4.2).
9. The design-system additions are published to the artifact (§9.1), and the client syncs to them.

### 2.2 Non-Goals (v1)

1. Keyboard shortcuts (user decision).
2. A split reading pane or a separate article route (user decision).
3. Clickable auto-linking inside the body. URLs stay as text.
4. Offline reading, and precaching news assets.
5. OPML import and export, AI features, and per-user read state.
6. A diff view between revisions.
7. Notifications for new articles.
8. Generalising `OdsTagAdmin`. That is a committed follow-up (§13).
9. **Full-article-text search** (user decision). Search covers the title, author and summary only; the
   full text is stored as a file and is not searched. See §13.
10. A published-date range in the filter bar. The API keeps `from`/`to`, but v1's UI does not offer
    them.

## 3. User Experience

### 3.1 Entry points and routing

| Where | What |
|---|---|
| Module switcher (`NavModuleSwitcher`) | A new module **Internet** (icon `public`), visible with `news.read` |
| Nav rail, group *News* | **News** `/news` (`feed`) · **Feeds** `/news-feeds` (`rss_feed`), both behind `news.read` |
| Nav rail, group *Reference* | **News Tags** `/news-tags` (`local_offer`), behind `news.read` |
| Command palette | The three pages, through `NavModel` |
| `/background-jobs` | The news job card and the *News status* panel |
| `/settings` | The **News** group |

- Every page carries `[Authorize(Policy = PermissionClaims.NewsRead)]`, so it is gated at the route
  and not only in the nav.
- Every page has a `PageTitle`: *News*, *Feeds* and *News tags*, each suffixed by the app's existing
  title pattern. On a route change, focus moves to the page `h1`, following the app's existing
  route-focus behaviour.
- The page-state keys are `news-page`, `news-feeds-page` and `news-tags-page`.
- Every news id (feeds, articles, tags, images) is a `Guid`. Client routes and query strings use
  `{id:guid}`, for example `/news?feedId=<guid>`, and the typed clients follow.
- **Deep links.** "Show articles from ‹feed›" and any `?feedId=` / `?tagId=` link **preselect that one
  item** in the Feed or Tag select. They clear the other filters except Status, and override the saved
  page state for this visit. The URL form stays singular.
- **Stale ids.** Before the first load, any feed or tag id restored from page state or a link that is
  not in the catalogue or tag list is dropped. A pending-deletion feed (a `422` from the API) therefore
  never fails the load.
- `/news-feeds?state=All&reveal=<id>` opens Feeds with the Status filter cleared to All and that feed
  revealed (§3.3).
- Feed URLs never appear in any route or query string.

### 3.2 `/news` — the reader

**Headings.** `h1` *News* (from `PageHeader`) → `h2` *Articles* → `h3` each card's name → `h4` each
in-card section.

- There is **no visible section separator** between `PageHeader` and the list: no "Articles · N
  articles" `OdsSectionDivider`. The list follows `PageHeader` directly.
- The `h2` *Articles* is an **sr-only** heading, for the outline only. It is **never a focus target**:
  a clipped element would show no focus ring (2.4.7). §6.6 says where focus goes instead.

#### 3.2.1 `PageHeader`

- Sub-line: *"42 unread · 18 feeds"*.
- Actions:
  - **Mark all read** (Outlined). Hidden while the Status filter is **Read** only: there is nothing
    unread to mark.
  - **Hide read** (§3.2.8), beside **Mark all read**.
  - **Manage feeds** (a Text link to `/news-feeds`).

#### 3.2.2 Overview and Attention

`PageHeader`'s **overview** panel, as on Accounts, holds two tiles:

- **Articles:** Unread · Read. These are the sums of the catalogue's `unreadCount` and
  `max(0, articleCount − unreadCount)` over **non-archived** feeds, matching what the list shows by
  default.
- **Feeds:** Healthy or pending · Need attention · Paused · Archived, counting **every** feed in the
  catalogue.

There is **no** standalone "N feeds need attention" alert above the list. Instead, `PageHeader`'s
**`Problems`** collection, labelled **"Attention"** as on Contracts, holds one `PageHeaderProblem` per
feed in `NeedsAttention` (`Failing`, `Gone`, `Quarantined` and `QuotaReached`, the shared
`NewsFeedHealthSets.NeedsAttention`):

| Health | Severity |
|---|---|
| `Gone`, `Quarantined` | Error |
| `Failing`, `QuotaReached` | Warning |

- Each row's text is "‹feed›. ‹health text›", the component's existing format, and its action is **Review →** to `/news-feeds?state=All&reveal=<id>`. The Feeds page uses `OdsInfiniteList`'s
  existing `RevealKey` to scroll to and expand that feed, as Accounts does for jump-to.
- **Quota rows** (text or image quota full, Warning) appear only for a caller with `file-storage.read`,
  which is what `GET /api/news-status` requires. Their **Settings →** action is rendered only with
  `system-settings.read`.
- **Data.** Everything comes from the feed catalogue and, for quota rows, `GET /api/news-status`. No
  other endpoint is needed.
- Text, not colour, carries every state.
- **Names.** The problems button reads "Attention, 2" (the existing "‹label›, ‹count›" name). Each
  action has a unique name through `PageHeaderProblem.ActionAriaLabel`, "Review ‹feed›" or, for
  example, "Open Settings for the text quota". The trailing " →" is `aria-hidden`.
- **Changes after the first load.** If a catalogue refetch changes the count, the page announcer says
  "1 feed needs attention" (or "No feeds need attention"). Nothing is announced at the first load.

#### 3.2.3 Filter bar

`PageHeader`'s search panel holds one wrapping row, in the same order and with the same components as
Contracts. At narrow widths it wraps into a stack.

| # | Control | Details |
|---|---|---|
| 1 | `OdsSearchField` | Placeholder "Search titles, authors and summaries". It takes the free width (min 280 px), as on the other pages. 300 ms debounce. Messages in §7.4 |
| 2 | `OdsMultiSelect` **Feed** | Options are every feed. A feed whose health is in `NeedsAttention`, or that is paused, carries it **as text in the option name**, for example "Example News (needs attention)" or "(paused)"; any glyph is decorative only. Archived feeds come last, with the `archive` icon and "(archived)" in the name. Each feed's `language` applies to its option text (§6.8) |
| 3 | `OdsMultiSelect` **Tag** | Options are every tag, with its icon and colour. Archived tags come last, with "(archived)" in the name |
| 4 | `OdsMultiSelect` **Status** | **Unread** and **Read**, each with an icon. The default is **Unread**. Selecting both, or neither, shows all, and the trigger then reads "Status: All" |
| 5 | `OdsSortSelect` | Fields **Published** (ascending or descending, mapping to `Oldest`/`Newest`) and **Relevance**. Relevance is listed only while a search is active; clearing the search falls back to Published, newest first, and that is announced (§6.7) |
| 6 | `OdsPageSizeSelect` "Load N at a time" | Presets 25 · 50 · 100, default 50. It sets the request `limit` for the scrolling list |

- **Combining.** Several feeds or tags are OR within the filter. Different filters are AND.
- Each multi-select shows its count badge and has **Clear** in its popover, as on Contracts.
- **The 50-id cap.** Feed and Tag set `OdsMultiSelect.MaxSelected` to `NewsLimits.MaxFilterIds`. When
  the cap is reached, the unselected options become `aria-disabled`, and a `role="status"` line in the
  popover reads "Up to 50 feeds" (or tags), interpolated from `NewsLimits.MaxFilterIds`. **Select all**
  is hidden while `MaxSelected` is lower than the option count.
- **Changing "Load N at a time" mid-scroll** applies from the next append. It is not a fresh first
  page, because the cursor does not bind the page size.
- No segmented control, switch or date range is rendered.

#### 3.2.4 List

- The list is an `OdsInfiniteList` in **remote mode** (§4.4) of `OdsRecordCard` rows, the same
  component and status pill as Accounts, Contacts, Contracts and Journal.
- It loads "Load N at a time" rows per request. When the sentinel scrolls into view, or the user
  presses the pill, it requests the next page with `nextCursor`. It stops when `nextCursor` is `null`.
- Duplicates are removed by id on append.
- **A fresh first page** happens on any filter, sort, search or tag change, on Hide read, on Refresh
  and after Mark all read. On a fresh first page:
  - the old rows stay visible under `OdsListStatus`'s **refetch bar**, not the loading skeleton;
  - when the new page arrives, the rows are replaced and the list scrolls to its top;
  - §6.6 decides focus.
- **The pill** is described in §4.4. The total comes from the first page only, and reads "10,000+"
  while `totalCountCapped` is true.
- **Failures.** A failed append keeps the loaded rows. The pill becomes "Couldn't load more. Retry",
  and its button retries the same cursor. A `news-busy` on an append follows the first-load rule in
  §7.1, with the pill as the region.
- One card is open at a time, as on every record list.

#### 3.2.5 Collapsed card (header)

| Slot | Content |
|---|---|
| Icon | `article`, neutral, `aria-hidden`. Never tinted by feed state |
| `Name` (h3) | The title, or *(untitled)*, with `lang` from the article `language` (§6.8) |
| `Chips` | **Unread** (visible text) when unread, or an sr-only **Read** chip when read. This is the single source of the state; there is no separate description. **Updated** when `contentChangedAtUtc` is set. Own tags plus inherited feed tags (`OdsTagChips`); inherited chips use the outlined variant with a leading `rss_feed` glyph and an sr-only "(from feed)" |
| `Meta` | Feed title (plain text, never a link) · author · `OdsRelativeTime` ("3 h ago"), rendered as `<time datetime="…">` with the absolute date as sr-only text · "2 images" when there are images |
| Actions | The card's `⋯` menu (§3.2.10), outside the trigger |

- Read cards keep the normal text tokens; `Dimmed` is **not** used.
- `Meta` and `Chips` render inside the card's expand button, so **nothing interactive renders there**:
  a link or button inside them would be a control inside a button.
- Tags are shown only as the header's `Chips`. The expanded body never repeats them.

#### 3.2.6 Expanded card

This follows `OdsRecordCard`'s fixed body order of alert → details → content → sections.

1. **Alert.** Shown only when the article is edited: an Info `OdsAlert` reading "Updated by the
   publisher on <time>12 Nov 2026</time>." with a **Show earlier versions** button. This is the only
   place that button appears.
2. **Details** (`OdsInfoTileGrid`), each as text:
   - *Published* and *First seen*: absolute dates in `<time>`;
   - *Read*: a time, or "Not read";
   - *Original link*: an `OdsExternalLink` (§6.2) with **Copy link** and **Open original**.

   There is no *Feed* tile: the feed title is already in the header's `Meta`.
3. **Content: the reading panel.** The body text and the image carousel sit in one **reading panel**.
   - The panel follows the design system's **outlined card** treatment: `--mud-palette-surface`, a 1 px
     `--mud-palette-divider` border, `--radius-md`, and no shadow. In the kit it is `.nw-reading`
     (`ui_kits/web/news.css`). The client implements it as a scoped class on `NewsArticleBody`'s
     container, not as a new shared component.
   - It is presentational only: no role, no heading, no focus stop.
   - Two columns from `md` (960 px) up, with the text in 8 of 12 columns and the carousel in 4. Below
     `md` the carousel sits under the text. These are MudBlazor grid breakpoints.
   - **Body** (`NewsArticleBody`):
     - It has `lang` and a `70ch` measure.
     - Each paragraph is a `<p>`, and line breaks are `<br>`.
     - It renders the **first 50 paragraphs**, then **Show the rest (N more paragraphs)**. Pressing it
       renders the next 200 per press and moves focus to the first newly shown paragraph, which carries
       `tabindex="-1"`.
     - Runs of more than one blank line are collapsed.
   - **Carousel**: `OdsImageCarousel` (§6.1).

There is **no action row** in the expanded card. **Mark read** / **Mark unread** and **Tags…** live
only in the `⋯` menu. In-card sections set `OdsRecordSection` `HeadingLevel = 4` explicitly.

#### 3.2.7 Body states inside the expanded card

| State | What the reading panel shows |
|---|---|
| `Body.Loading` | On the first expand, `OdsSkeleton` lines with `aria-busy="true"` while the detail request runs. Expanding announces "Loading article…" once, and nothing when the body arrives within 1 s. Focus stays on the card header |
| `Body.Error` | An inline `OdsAlert` (error, `role="alert"`) reading "Couldn't load this article." with **Retry**. For `503 news-storage-unavailable` it reads "The article text isn't available right now." with **Retry**. Focus stays on the card header |
| `Body.Unavailable` | `200` with `bodyUnavailable: true`: an `OdsAlert` with severity **Info** and `role="status"`: "The article text isn't available right now." The sentence "Ask an administrator to check the Background jobs page." and its link appear only for a caller with `system-settings.read`, the `/background-jobs` gate; other callers see the first sentence only. The alert is announced once, when the card expands. The list, the read state and the tags keep working, and a read-state change still applies |
| `Body.Omitted` | `200` with `bodyOmittedReason` set (`TooLarge` or `TooComplex`): an Info `OdsAlert` with `role="status"`, "This article is too large or complex to show here.", followed by the article's `OdsExternalLink` to the original (with its sr-only "(opens in a new tab)"). No body is rendered. The carousel is still shown if the article has images. The alert text is announced once by the page's `OdsLiveAnnouncer` when the card expands, as for `Body.Unavailable`. *(Added at merge, from the backend cap decision.)* |

#### 3.2.8 Mark read on open, Undo and Hide read

**Mark read on open** (user decision):

- Expanding an unread card sends `PUT …/read-state {isRead:true}` straight away, optimistically.
- One `ISnackbar` message, "Marked as read", carries an **Undo** action. It stays for **8 s**
  (`VisibleStateDuration` 8000 ms). If MudBlazor 9.12.0 pauses on hover or focus, the spec relies on
  that; if it does not, the Undo snackbar uses `RequireInteraction` and stays until it is dismissed. It
  can be dismissed with Escape.
- The change is announced once by the page's `OdsLiveAnnouncer` (§6.7), not by the snackbar.
- **Mark unread** in the card's `⋯` menu does the same as Undo with no time limit. That satisfies
  2.2.1: the snackbar is a convenience and the menu item is the untimed equivalent.
- Expanding a card that is already read sends nothing.
- Under Status = Unread, a card that has just been read stays in the list until **Hide read**, a fresh
  first page or a reload.
- **Undo after its card is gone.** Undo is **withdrawn** as soon as the card is no longer on screen:
  after Hide read, any fresh first page or a reload. The snackbar then reads "Marked as read — use Mark
  unread on the article to undo", with no action.
  - While Undo is available, it returns focus to the card header.
  - Once withdrawn, it has no focus target.

**Hide read** (user decision) is a `PageHeader` action beside **Mark all read**.

- It is rendered only while the Status filter is exactly **Unread** and at least one card in view has
  been read.
- Pressing it is a fresh first page (§3.2.4). Focus follows §6.6.
- It makes **one** announcement, combining the two: "3 read articles hidden. 1,231 articles."

#### 3.2.9 Mark all read

- A confirm `OdsModal`. It shows a number **only** when an exact count is known: Status is Unread, no
  search is active, the list total is not capped, and the selection is either no feed and no tag filter (the overall
  unread total) or exactly one feed alone or exactly one tag alone. The number then comes from
  `unread-counts`. Any other combination shows no number. When the only filter is one archived feed,
  which `unread-counts` does not include, it shows no number rather than 0. With a number,
  it reads "Mark 42 unread articles matching these filters as read?"; otherwise "Mark all unread
  articles matching these filters as read?".
- The scope sentence names the selection in words:
  - "…in *Example News* and 2 other feeds";
  - "…tagged *Climate* or *Energy*";
  - "…from 3 feeds, tagged *Climate*";
  - "…in *Example News* tagged *Climate*";
  - a search as "matching 'election'".
- **Mark read** sends `POST /api/news-articles/mark-read` with the list's own filters (search, feeds,
  tags, Status) and the **`asOfUtc` echoed from the list response**, which is server time.
- **Repeats.** One request marks at most 5,000 articles and returns `hasMore`. While `hasMore` is true,
  the client repeats the call with the same filters and the same `asOfUtc`.
  - During the repeats, the confirm button shows "Marking…" and is `aria-disabled`, and the article
    list is `aria-busy="true"`.
  - A `role="status"` line in the dialog reads "Marking… 5,000 so far". It updates at most once every
    5 s, plus once at the end.
  - **Cancel** and **Escape** stay available. They stop further calls after the in-flight one. The
    snackbar then reads "5,000 articles marked as read. Stopped before finishing."
  - A `news-busy` partway through waits out `Retry-After` and repeats the same call once. A second
    `news-busy` stops the run, with the partial-total snackbar.
  - **If a later call fails:** the counts are refreshed and the snackbar says "Some articles were marked
    as read; try again to finish." It stays until the user dismisses it, as all error snackbars do.
- **When it finishes**, the list takes a fresh first page and the snackbar reports the **total** of
  every `markedCount`: "1,234 articles marked as read." The total is announced exactly once, by the page's
  `OdsLiveAnnouncer` with the same text as the snackbar (§6.7). The snackbar itself is not a live
  region. The partial-total and stopped snackbars follow the same rule.
- After the dialog closes, whether the run finished, was stopped or failed, focus moves as §6.6 says
  for Mark all read.

#### 3.2.10 The article `⋯` menu

- Named "Actions for ‹title›", since it is the only way to reach Mark unread (2.4.6).
- Items: **Mark read** / **Mark unread** · **Tags…** · **Show articles from ‹feed›**, which navigates
  to `/news?feedId=…` (a deep link, §3.1).
- After Mark read / Mark unread from the menu, focus returns to the card's `⋯` button, as for every
  other menu action.

#### 3.2.11 Tag dialog

An `OdsModal` titled *Tags for "‹title›"*:

- an `OdsTagMultiSelect` over the active news tags;
- the inherited tags as read-only chips, captioned "From the feed. Change them on the Feeds page.";
- **Save**, which sends `PUT …/tags`.

**Removing a single own tag** is done only in this dialog, reached from **Tags…** in the `⋯` menu:
clearing the tag and pressing **Save** removes it with one `PUT …/tags`. There is no per-chip remove
button, because the chips render inside the card's expand button, where nothing interactive may
render. The client does not call `DELETE …/tags/{tagId}` from the card.

### 3.3 `/news-feeds` — feed management

**`PageHeader`.**

- Sub-line *"18 feeds · 2 need attention"*. "Need attention" is the shared
  `NewsFeedHealthSets.NeedsAttention` and counts `QuotaReached` feeds too.
- **New feed** (Filled), rendered only with `news-feeds.create`.
- **Overview**, as on Accounts, with two tiles computed from the full feed set the page already holds:
  - **By tags:** one row per tag, with its feed count, plus **No tag**.
  - **By status:** **Active**, **Paused** and **Archived**. These are disjoint, so the tile's total
    equals the feed count. "Needs attention" is deliberately **not** a row here, because it overlaps
    the three statuses and would double-count. It stays in the sub-line.

**Headings and list.**

- No visible "Feeds · N" divider. An sr-only `h2` *Feeds* is kept for the outline only and is never a
  focus target.
- **Loading.** The page holds its full set by requesting `GET /api/news-feeds` with **`state=All`** (the
  API default is `Active`) in consecutive pages of 200 until a page has fewer than 200 rows, with 5
  requests as a hard limit. It removes duplicates by id. If any request fails, the partial set is
  discarded and the page shows its load error with Retry.
- It then uses `OdsInfiniteList` in the existing **in-memory** mode.

**Filter bar**, laid out as on Accounts. Filtering and sorting are **client-side** over the full set.

| # | Control | Details |
|---|---|---|
| 1 | `OdsSearchField` | "Search name or site", min 280 px |
| 2 | `OdsMultiSelect` **Status** | Active, Paused, Archived. Active is ticked by default. With none ticked, all are shown and the trigger reads "Status: All" |
| 3 | `OdsMultiSelect` **Tag** | Archived tags listed last, with "(archived)" in the name |
| 4 | `OdsSortSelect` | Name, Unread, Last checked |
| 5 | `OdsPageSizeSelect` | "Load N at a time", presets **25 · 50 · 100**, default 50 |

Each feed's status is derived: **Archived** when `archivedAtUtc` is set, else **Paused** when
`!isActive`, else **Active**. **Review ‹feed›** from the News Attention panel opens Feeds with Status
cleared to All and the feed revealed.

**Feed card** (h3):

| Slot | Content |
|---|---|
| `Name` | `displayTitle` (never a fetch host), with the feed's `lang` |
| `Chips` | `NewsFeedHealthChip`. Its text carries the state: *Pending first fetch* · *Healthy* · *Failing* · *Gone* · *Paused* · *Archived* · *Quarantined* · *Storage limit reached*. Icon and tone are supplementary |
| `Meta` | The site host, from `siteUrl` only, omitted when null · "Checked 12 min ago" from `lastCheckedAtUtc`, in `<time>` · "Every 60 min" · tags |
| `Counts` | unread · articles |

**"Storage limit reached".** The chip covers both the daily article cap and the text quota. The feed row
gains a description, linked to the chip by `aria-describedby`, driven by `quotaReason`:

| `quotaReason` | Text |
|---|---|
| `DailyCap` | "Daily article limit reached. Resumes tomorrow (UTC)." |
| `TextQuota` | "Text storage quota is full. An administrator can raise it in Settings." |

The same text is shown visibly in the feed's `management` block.

**Expanded feed card:**

- **Status** (h4). Rendered only when `management` is non-null, which needs `news-feeds.update`:
  - the feed URL, **query masked by default**: `https://example.com/feed.xml?…`, with an sr-only
    "(query hidden)" after the visible `?…`, a **Show full address** toggle button with
    `aria-pressed`, and **Copy**;
  - the last result as text (`lastErrorMessage`);
  - the count of consecutive failures;
  - today's articles and image MB;
  - the `quotaReason` text, when it applies;
  - the https → http note, when it applies;
  - the moved-feed line and **Use new address**, when `suggestedUrl` is set (§3.3, *URL suggestions*).
- **Settings** (h4): the fetch interval, *Download images* and *Active*.
  - On every feed that is not archived, including a quarantined one, the section ends with a visible
    `<p>`: "To delete this feed, archive it first. Archive is in the ⋯ menu on this card." It is
    ordinary text in the accessibility tree, never a tooltip, hidden text or `aria-describedby`-only.
    It appears only for callers holding both `news-feeds.update` and `news-feeds.delete`.
- **View articles**: a link to `/news?feedId=…`.

**Row `⋯` menu.** An action the caller lacks the claim for, or that the feed's state rules out, is
**absent**, not dimmed. No row menu on the news pages contains a disabled item. `OdsMenu` is not
extended.

- Items: **Edit** · **Tags…** · **Refresh now** · **Pause** / **Resume** · **Archive** / **Unarchive**
  · **Release from quarantine** · **Delete…**.
- **Delete…** appears only on an archived feed. **Release from quarantine** appears only on a
  quarantined feed. **Resume** replaces **Pause** while the feed is paused, and **Unarchive** replaces
  **Archive** while it is archived.
- **State swaps are announced.** After Pause, Resume, Archive or Unarchive, the page announcer says
  "Example News paused", "…resumed", "…archived" or "…unarchived". When the change also moves the card
  out of the current filter, the card is removed, focus moves per §6.6, and the sentence is "Example
  News paused and moved to Paused" instead.

**New / Edit feed dialog** (`OdsFormDialog`). *New feed* is confirmed with **Create feed**; *Edit feed*
with **Save**. Required fields carry the `*` **and** the visible word "Required" in their helper line.

| Field | Control | Helper |
|---|---|---|
| Feed URL `*` | `OdsField` (type url) | "Required. An RSS, Atom or JSON Feed address starting with http:// or https://. Odyssey checks it on the first fetch." |
| Custom title | `OdsField` | "Optional. Leave empty to use the feed's own title." |
| Check every | `OdsNumberField`, unit *min* | "Optional. 15–1440 minutes; empty uses the default." (interpolated from `NewsLimits`) |
| Download images | `OdsSwitch` | |
| Tags | `OdsTagMultiSelect` | Create only |
| Active | `OdsSwitch` | Edit only |

**Edit and the feed version.**

- `ExistingNewsFeed.version` is kept from the read, and the edit dialog sends it on `PUT`.
- On `409 news-feed-changed`, the top of the dialog shows an `OdsAlert` with `role="alert"`: "This feed
  was changed elsewhere. Reload it to see the latest settings." Focus moves to it (`tabindex="-1"`).
- **Reload** refetches the feed and stores its new `version`.
  - A field the user changed and the server did not keeps the user's value.
  - A field the server changed **wins**, whether or not the user changed it too.
  - After Reload, the alert is replaced by an `OdsAlert` with `role="status"`: "Reloaded. Updated from
    the server: Title, Interval." (or "Reloaded. Nothing changed on the server."). Focus moves to the
    first field updated from the server, else to the dialog's first field.
  - If Reload fails, the alert stays (`role="alert"`) and reads "Couldn't reload this feed. Try again."
    Reload stays enabled and keeps focus.
  - If Reload returns `404`, the alert reads "This feed was deleted." Save is disabled, and focus moves
    to **Close**.
- After a feed save, the row and the dialog show the `url` from the `PUT` response, not the form value.
- **URL suggestions.** A suggested address is never applied automatically; only a `PUT` applies it.
  When `ExistingNewsFeed.suggestedUrl` is set, the feed card's `management` block (§3.3, *Status*)
  shows:
  - a visible line, "This feed has moved to ‹url›.", with the URL rendered by the `OdsExternalLink`
    rules (`<bdi dir="ltr">`, the punycode host token, a link only for an absolute http/https URL
    without user info);
  - a **Use new address** button, rendered only with `news-feeds.update`. It sends `PUT` for the feed
    with `url` = `suggestedUrl` and the current `version`. On success the card shows the `url` from the
    `PUT` response, the snackbar reads "Feed address updated", the page announcer says it once, and
    focus stays on the card header. A `409 news-feed-changed` follows the Reload flow above.

**Create flow:**

1. Saving sends `POST`.
2. The dialog closes, and focus moves to the **new card's header**, which is expanded and marked
   *Pending first fetch*.
3. The page polls that feed every 10 s for at most 2 min.
4. Beside the chip are **Stop checking** and **Check again** buttons (2.2.2).
5. The first change of state is announced once: "Example News: fetched" or "Example News: failing". The
   poll then stops.
6. A poll that reaches its 2-minute limit announces "Example News hasn't been fetched yet. Check again
   later."

**Delete flow:**

1. **Delete…** opens a confirm `OdsModal` reading "Delete *Example News* and its 311 articles? This
   cannot be undone." The title is rendered as an encoded `<em>`.
2. An **"I understand this deletes the feed and all its articles"** checkbox enables **Delete feed**
   (destructive tone). While disabled, the button's reason is given in its helper text.
3. A `202` removes the card. Focus moves per §6.6. The snackbar reads "Feed deleted. Its articles are
   being removed in the background.", and the announcer says it once.

### 3.4 `/news-tags`

This is a dedicated page. It does **not** use `OdsTagAdmin`, which is bound to `Guid`-keyed
`ITagsApiClient<TRow>` rows, archive by `PUT` replay, and paged lists with no in-use delete. The page is
built from existing pieces:

- `PageHeader` with **New tag** (rendered with `news-tags.create`), an `OdsSearchField` and an *Include
  archived* `OdsSwitch`. The filter bar uses the same markup as Accounts: every control shares one
  baseline and the Accounts heights.
- A visible `h2` *Tags*, to which the list's caption is visually tied.
- The list reads the unpaged `GET /api/news-tags` and shows each tag with an icon avatar, the name and
  a description line, its *Feeds* and *Articles* counts, its *Status* ("Archived" as text), and the row
  menu.
- The list is an **`OdsRecordTable`** with the caption "News tags", windowed by the in-memory "Load N
  at a time" pattern, as the design system's `NewsTags` does (v227). The table is **not** nested inside
  `OdsInfiniteList`:
  - The page holds the full tag set, and sorts and filters it itself.
  - It passes the current window (the first N rows of the sorted, filtered set) to `OdsRecordTable` as
    `Rows`, with `ServerSort="true"`, so the table's sort header asks the page to re-sort the **whole**
    set rather than sorting the loaded rows.
  - `OdsInfiniteList` in its **footer-only mode** (§4.2), rendered after the table, supplies the
    sentinel and the pill that grow the window. Growing the window keeps the sort order.
  - `OdsRecordTable` keeps the loading, error and empty states.
- A "Load N at a time" select (presets 25 · 50 · 100, default 50) sits in the bar. At 320 px the table
  switches to its stacked layout.
- The create and edit dialog (`OdsFormDialog`) with **`OdsTagIconPicker`** and **`OdsTagColorPicker`**,
  reused unchanged, plus the name and description fields.
- **Archive** and **Unarchive** call the dedicated endpoints.
- After a tag is created, focus moves to the new row's **name button** (the row's edit trigger).
- **Delete…** on a tag in use (`409`) turns the dialog into an explanation:
  - "Used by 3 feeds and 41 articles."
  - A **Remove from all and delete** button, with an acknowledgement checkbox, shown only when the
    caller holds the claims for every link class present.
  - Otherwise: "You need permission to edit feeds to remove this tag from them."

The existing tag pages stay untouched. Its visuals follow the existing tag pages' kit
(`TransactionTags.jsx`) as closely as the list allows.

### 3.5 Earlier versions dialog

An `OdsModal` titled *Earlier versions of "‹title›"*:

- The count line: "3 earlier versions. Up to `NewsLimits.MaxRevisions` are kept, and the original is
  always kept." The number is interpolated from the shared constant.
- A **Version** `OdsSelect`, labelled "Version", filled from `GET …/revisions`, which returns metadata
  only. Its options run newest first, for example "Version 3 · replaced 12 Nov 2026", and the last is
  "Original · first fetched 10 Nov 2026".
- Selecting a version loads that one body from `GET …/revisions/{n}`.
  - While it loads, the panel shows a skeleton and is `aria-busy="true"` and silent.
  - Selecting another version cancels the load in flight. A response for a version that is no longer
    selected is discarded.
  - "Showing version N" is announced only **after** the selected version's body has loaded.
  - The failure state is an `OdsAlert` with `role="alert"`: "Couldn't load this version." with
    **Retry**. After a successful Retry, focus moves to the panel's heading (`tabindex="-1"`). After a
    failed Retry, focus stays on Retry.
- **Omitted version.** A revision whose body response has `bodyOmittedReason` set renders no revision
  body. Its panel shows the same Info `OdsAlert` with `role="status"` as `Body.Omitted` (§3.2.7), "This
  article is too large or complex to show here.", and the alert text is announced once through the
  page's `OdsLiveAnnouncer`, in place of "Showing version N".
- **Only the selected version is rendered.** It uses `NewsArticleBody` with the same chunking, plus its
  title, author and link, each with `lang` set.

### 3.6 Admin surfaces

**`/background-jobs`:**

- A `BackgroundJobPresentation` entry for `JobType 9`, named *News feeds*.
- A run-detail body for `NewsFeedJobRun`, showing feeds fetched, items stored, items revised, images
  stored and feeds quarantined, each as a labelled number. A manual run's detail also shows **Feeds
  marked due**.
- Held-reason copy that fits the existing "{name} is held: {reason}" pattern: `LowSpace`: "the file
  volume is low on space". When storage is unavailable, a held job uses the existing
  `StorageUnavailable` copy.
- While `textAndSourceQuotaReached` is true, the news job card shows a **warning line**: "Text storage
  quota reached — raise it in Settings", with a link to `/settings`. It sits beside the existing
  image-quota warning and uses the same component.

**News status panel** (`file-storage.read`). It reads `GET /api/news-status` and shows:

- the storage state in words and the volume;
- the article and feed counts;
- **image bytes** against the image quota, as a progress bar labelled "Image storage";
- **text-and-source bytes** (`textAndSourceBytes`) against `textAndSourceQuotaBytes`, as a second
  progress bar labelled "Text storage". The client holds no quota number of its own. When
  `textAndSourceQuotaReached` is true, the bar reads "Full";
- free space and quarantined feeds;
- the top 10 feeds by image bytes, as links using the `displayTitle` from the status response, with
  each feed's `lang`.

The panel renders in the `Ready`, `Degraded` and `Unconfigured` storage states.

**Quota bars.** Both bars follow one rule:

- `role="progressbar"`, `aria-valuemin`, `aria-valuemax` and `aria-valuenow`;
- `aria-labelledby` points at the visible label ("Image storage", "Text storage");
- `aria-valuenow` is clamped to `aria-valuemax`;
- `aria-valuetext` is "3.1 of 20 GiB", or "Full, 20 of 20 GiB" when the quota is reached, with the
  same text visible beside the bar;
- "Full" is also visible text with a warning icon, so it is not shown by colour alone.

**`/settings` → News group.** The icon is `Icons.Material.Filled.RssFeed`. Each row is an
`OdsSettingField` in the catalogue (`Settings.razor.cs`), with bounds served by the API (§5.4).

- The allow-list row is **not rendered** for a caller without `system-settings.security.update`. For a
  caller who has it:
  - It sits in an `OdsSettingField` whose **`Advisory`** slot reads "Never list mariadb, api, worker or
    other internal service names."
  - The page's **Save** opens a confirm `OdsModal` that lists the **added** and **removed** hosts as
    text: "The worker will be allowed to fetch from: nas.lan, 192.168.1.20:8080."
  - The field validates with the shared `SettingItem.Rule` delegate.

### 3.7 UI states

| # | State | What the user sees |
|---|---|---|
| 1 | `Loading` | `OdsSkeletonRow` card shapes, `aria-busy` on the list, announced as "Loading articles…" |
| 2 | `Refetching` | A fresh first page: the old rows stay under `OdsListStatus`'s refetch bar, with `aria-busy` |
| 3 | `Empty.NoFeeds` | `OdsEmptyState`: "No feeds yet" · "Subscribe to a site's RSS, Atom or JSON feed to start reading." · **New feed** with the claim, otherwise "Ask an administrator to add feeds." |
| 4 | `Empty.AllRead` | "You're all caught up" · **Show all articles** |
| 5 | `Empty.Filtered` | "No articles match these filters" (the search term echoed as text) · **Clear filters**, which resets Feed, Tag and Search and keeps Status at Unread |
| 6 | `Empty.Pending` | "Your feeds haven't been fetched yet. This usually takes a minute." |
| 7 | `Populated` | The cards |
| 8 | `Appending` | The pill reads "Loading more articles…" (§4.4) |
| 9 | `Append.Failed` | Loaded rows stay; the pill reads "Couldn't load more. Retry" |
| 10 | `Busy.FirstLoad` | The region's `news-busy` state (§7.1) |
| 11 | `Saving` | The button reads "Saving…" and is `aria-disabled`, and the row is `rowBusy` |
| 12 | `Error.Load` | "Couldn't load articles." · **Retry** · `role="alert"` |
| 13 | `PermissionDenied` | The client's standard not-authorised view |
| 14 | `ImageUnavailable` | The slide shows "Image unavailable" as text and stays counted |
| 15 | `Body.Loading` / `Body.Error` / `Body.Unavailable` / `Body.Omitted` | Inside an expanded card (§3.2.7) |
| 16 | `Attention` | The `PageHeader` Attention problems (§3.2.2); there is no alert above the list |

## 4. Component Inventory

### 4.1 New

| Component | Kind | Notes |
|---|---|---|
| `OdsImageCarousel` | Interactive | Full specification in §6.1 |
| `OdsExternalLink` | Interactive, small, **shared design-system component** | Implements the design system's `ExternalLink` (`Foo.jsx` ⇄ `OdsFoo.razor`) and is usable app-wide. Takes `Url`, `Text` and `Copyable`. It renders an `<a href>` **only** for an absolute `http`/`https` URL without user info; anything else renders as plain text with **Copy**. The URL text sits in `<bdi dir="ltr">`, with the host shown as a separate token from `Uri.IdnHost`, which is punycode. The external link has `target="_blank" rel="noopener noreferrer"` and an sr-only "(opens in a new tab)". Copy announces "Link copied" or "Couldn't copy. Select the text instead." |
| `NewsArticleBody` | Presentational | Encoded text only. Chunked rendering (§3.2.6). `lang`. Its container carries the reading panel's scoped class |
| `NewsFeedHealthChip` | Thin | `OdsChip` with an icon, a **text** label and a tone, from the `NEWS_FEED_HEALTH` registry |
| `OdsRelativeTime` | Helper | `OdsRelativeDay` is day-granular and static. This one renders "12 min ago" in `<time datetime>`, with the absolute date as sr-only text. It refreshes once a minute with no announcement. Every `OdsRelativeTime` on a page shares **one** 60-second ticker service |
| `NewsStoreGate` | Service (scoped) | One place that handles `news-storage-unavailable` on the article detail and revision calls, using `ApiResult.RetryAfter`. Image failures never reach it (§6.1). `news-busy` is handled per context (§7.1) |
| `NewsCatalogueCache` | Service | The feed catalogue and tag list, keyed by the signed-in user id and cleared on every `AuthenticationStateChanged`. It holds no `management` data, because the catalogue carries none |

### 4.2 Changed

Each change is opt-in or invisible to existing pages unless stated.

| Component | Change |
|---|---|
| `OdsInfiniteList` | Gains a **remote mode** (§4.4), and a **footer-only mode**: only the sentinel and the pill, with no list body and no loading, error or empty states of its own, rendered after another component (the News Tags `OdsRecordTable`, §3.4) to grow a page-held window. Both are off by default; every existing page stays in the in-memory card mode and is unchanged |
| `OdsMultiSelect` | Gains an optional `int? MaxSelected`. When the cap is reached, the unselected options become `aria-disabled` and a `role="status"` line in the popover states the cap (the page supplies the text). **Select all** is hidden while `MaxSelected` is lower than the option count. Pages that do not set it are unchanged |
| `PageHeader` | `PageHeaderProblem` gains an optional `ActionAriaLabel`, which overrides the action's accessible name when set. The action's trailing " →" is wrapped `aria-hidden` on **every** page. The problems toggle's name stays the existing "‹label›, ‹count›" |
| `OdsSortSelect` | Its field menu opens at exactly the field's width on every page. Long option labels **wrap**, never clip (1.4.10, 1.4.4). (The 180 px minimum on the split and segmented variants existed in the design-system kit only and is removed there.) Checked on the narrow Background jobs and weather filter bars |

### 4.3 Reused

| Component | Notes |
|---|---|
| `OdsSearchField`, `OdsSelect`, `OdsSwitch`, `OdsPageSizeSelect`, `OdsListStatus` | |
| `OdsRecordCard` + `OdsRecordBody` + `OdsInfoTileGrid` + `OdsRecordSection` | The fixed body order is used as is. Cards use `HeadingLevel = 3`; sections `HeadingLevel = 4` |
| `OdsRecordTable` | News Tags, caption "News tags". Receives the page's current window as `Rows` with `ServerSort="true"`, and keeps its own loading, error and empty states; `OdsInfiniteList`'s footer-only mode follows it |
| `OdsTagIconPicker`, `OdsTagColorPicker`, `OdsTagChips`, `OdsTagMultiSelect`, `OdsChip` | |
| `OdsModal`, `OdsFormDialog`, `OdsCheckbox`, `OdsErrorSummary` | |
| `OdsAlert`, `OdsProblemAlert`, `OdsEmptyState`, `OdsSkeleton`, `OdsSkeletonRow`, `OdsProgressBar`, `OdsLiveAnnouncer` | |
| `OdsField`, `OdsNumberField`, `OdsTextInputField`, `OdsSettingField` | |
| `OdsIconButton` | Carousel controls, Copy |
| `OdsMenu` | Not extended: unavailable items are absent (§3.3) |
| MudBlazor `ISnackbar` | Undo and confirmations. `OdsToast` has no action slot |

Not used on any News page: `OdsPager`, `OdsSegmentedControl`, `OdsDateRangePicker`, and
`OdsSectionDivider` under the header.

### 4.4 `OdsInfiniteList` remote mode

| Parameter | Meaning |
|---|---|
| `bool Remote` | Turns on remote mode. Off by default, so every existing page is unchanged |
| `int? RemoteTotal`, `bool RemoteTotalCapped` | The server total from the first page, for the pill |
| `bool HasMoreRemote` | `nextCursor != null` |
| `EventCallback<bool> LoadMore` | Asks the page for the next page. The argument is `true` when the user pressed the pill, so the page can apply the pill focus rule (§6.6) |
| `bool LoadingMore`, `bool LoadMoreFailed` | The append state, owned by the page |
| `object? ResetKey` | Changes on every fresh first page |

Rules:

- **No windowing in remote mode.** Every item in `Items` renders. The in-memory window logic
  (`BatchSize`, the reset on a change in item count) is skipped. Only a change of `ResetKey` resets the
  component.
- **One load at a time.** The component calls `LoadMore` only while
  `HasMoreRemote && !LoadingMore && !LoadMoreFailed`. After a failure it stops loading on scroll until
  the user presses Retry.
- **No stall.** After each append, `infinite-scroll.js` re-checks whether the sentinel is still in view
  and, if so, asks again. A tall screen therefore fills itself.
- **Stale responses are dropped.** The page keeps a generation number and cancels the request in flight
  when it starts a fresh first page. A response for an older generation is discarded.
- **A `400` keyed `cursor`** (for example a revoked key) restarts from a fresh first page. It is not
  shown as a failure.

**The pill.**

- Its accessible name in remote mode is its visible text; the component sets no `aria-label`.
- The button sits **outside** the live region. A separate visually hidden `role="status"` element
  carries the append announcements, so the button's own text changes are not announced.

| State | Button text | Button state |
|---|---|---|
| Idle | "50 of 1,234 articles loaded · Load more" | Enabled |
| Idle, capped | "50 of 10,000+ articles loaded · Load more", with "10,000+" carrying an sr-only "more than 10,000" | Enabled |
| Loading | "Loading more articles…" | `aria-disabled="true"`, not natively disabled, so focus stays |
| Failed | "Couldn't load more. Retry" | Enabled; pressing it retries the same cursor |

- The pill's `role="status"` region announces **appends only**: "50 more articles loaded, 100 of
  1,234." or "Couldn't load more articles."
- **The status container stays rendered** after the last page removes the button, so the final "N more
  articles loaded" is still announced.

## 5. API Consumption

All shapes, statuses and claims are in the backend spec. The paths here are pointers only. Typed clients
follow the existing naming: `INewsArticlesApiClient`, `INewsFeedsApiClient`, `INewsTagsApiClient` and
`INewsStoreApiClient`.

### 5.1 Reader

| Endpoint | When | Client behaviour |
|---|---|---|
| `GET /api/news-articles` | A fresh first page; each append (with `cursor`) | Filters: search, feed ids, tag ids, `isRead` (absent when Status is both or neither), sort, `limit`. Read as its own `NewsArticlePage` record through `ValueOrToast`. Keeps `nextCursor`, the total and `totalCountCapped`, and the response's **`asOfUtc`** for Mark all read |
| `GET /api/news-articles/{id}` | First expand | Body, images, `bodyUnavailable` and `bodyOmittedReason`. Cached for the card's lifetime |
| `PUT …/{id}/read-state` | Expand (when unread), Mark read or unread, Undo | Optimistic with rollback, serialised per article |
| `POST /api/news-articles/mark-read` | Mark all read | The list's filters plus the echoed `asOfUtc`; repeated while `hasMore` (§3.2.9) |
| `PUT …/{id}/tags` | The tag dialog, including removing a single tag | One call per Save. `DELETE …/{id}/tags/{tagId}` is not called from the card |
| `GET …/{id}/revisions` | Show earlier versions | Lazy; metadata only |
| `GET …/{id}/revisions/{n}` | Selecting a version | One body at a time; superseded responses discarded |
| `GET …/{id}/images/{imageId}/content` | Carousel `<img src>` | Built with the same API-base helper `PhotoImageSources` uses, from the two ids only. The URL is used **only** as an `<img src>`, never as a link, a new-tab target, an `<object>`, `<embed>` or `<iframe>` |
| `GET /api/news-feeds/unread-counts` | Load; after read-state writes, debounced 1 s | Counts, including the Mark all read number |
| `GET /api/news-feeds/catalogue` | Load, through `NewsCatalogueCache` | Feed options, the overview tiles, the Attention problems and the `language` fallback |
| `GET /api/news-tags` | Load, through the cache | Tag options |
| `GET /api/news-status` | Load, only for `file-storage.read` holders | Quota rows in Attention |

### 5.2 Feeds

| Endpoint | When | Client behaviour |
|---|---|---|
| `GET /api/news-feeds?state=All` | Load | Consecutive pages of 200, at most 5 requests (§3.3). `management` is rendered only when non-null |
| `GET /api/news-feeds/{id}` | The pending poll; after a write; Reload | Refresh of one card |
| `POST`, `PUT /api/news-feeds…` (with `version`), `PUT …/tags`, `POST …/refresh`, `…/archive`, `…/unarchive`, `…/release-quarantine`, `DELETE` | Dialogs and the menu | As in §3.3. The row shows the `url` from the `PUT` response. Every write invalidates `NewsCatalogueCache` |

### 5.3 Tags and admin

| Endpoint | Where |
|---|---|
| `GET/POST/PUT /api/news-tags…`, `…/archive`, `…/unarchive`, `DELETE …[?detachLinks=true]` | `/news-tags` |
| `GET /api/news-status` | The News status panel and the job card's text-quota warning line |
| Existing `/api/background-jobs/…` and `/api/system-settings` | The job card and the settings group |

### 5.4 Settings rows (`/settings`, group *News*)

| Row | Key | Control |
|---|---|---|
| Default check interval | `NewsDefaultFetchIntervalMinutes` | `OdsNumberField` in `OdsSettingField`, unit *min* |
| Maximum feeds | `NewsMaxFeeds` | `OdsNumberField` |
| Images per article | `NewsImageMaxPerArticle` | `OdsNumberField` |
| Image size limit | `NewsImageMaxSizeKb` | `OdsNumberField`, unit *KB*. Its maximum is `min(range max, NewsImageMaxSizeKbCeiling)` from `SystemSettingsDto`, and the help line interpolates the effective number |
| Image storage quota | `NewsImageStoreMaxGiB` | `OdsNumberField`, unit *GiB* |
| Text storage quota | `NewsTextStoreMaxGiB` | `OdsNumberField`, unit *GiB* |
| Articles per feed per day | `NewsFeedMaxArticlesPerDay` | `OdsNumberField` |
| Image MB per feed per day | `NewsFeedMaxImageMbPerDay` | `OdsNumberField`, unit *MB* |
| Private hosts allowed | `NewsPrivateHostAllowList` | `OdsTextInputField` in `OdsSettingField` with `Advisory`; requires `system-settings.security.update` (§3.6) |

Nine rows, or eight for a caller without `system-settings.security.update`. The bounds always come from
the API.

### 5.5 Failure surfacing and limits

- Every call site uses `OrToast`, `ValueOrToast`, `ItemsOrToast` or `PagedOrToast`. The exceptions are
  the inline treatments in §7: `news-storage-unavailable` goes through `NewsStoreGate`, and `news-busy`
  and the search errors follow §7.1 and §7.4.
- **No server value is restated as a literal.** Field limits, the interval range, the filter-id cap
  and the revision count come from `NewsLimits` in `Odyssey.Dtos`. Admin-editable caps come from the
  settings DTO, from `GET /api/news-status` and from validation responses. Source lints enforce this
  (§8.5).

## 6. Accessibility

WCAG 2.2 AA, EN 301 549, Section 508 and *Forskrift om universell utforming av IKT*. Everything here is
an **MVP requirement**.

### 6.1 `OdsImageCarousel`

| Requirement | Specification |
|---|---|
| Structure | `<div role="group" aria-roledescription="carousel" aria-label="Images for ‹article title›">`, so it is **not** a landmark. Each slide is `<div role="group" aria-roledescription="slide" aria-label="Image 2 of 5">`. Only the current slide is visible; the others are `hidden` |
| No auto-rotation | No timer exists |
| Controls | **Previous image** and **Next image** (`OdsIconButton`), ≥ 40×40 CSS px. At the ends they are `aria-disabled="true"`. Pressing one there announces "First image" or "Last image". No wrap-around |
| Counter | The visible "2 / 5" is `aria-hidden`. Only the live region speaks |
| Live region | Polite, inside the carousel, filled only on a user action: "Image 3 of 5: ‹alt›", or "Image 2 of 5" for an image with no alt text. Alt text spoken there is truncated to 140 characters |
| Keyboard | Tab reaches Previous, then Next. ArrowLeft and ArrowRight work while focus is on either button. No other key handling, and no trap |
| Images | `alt` = the publisher's alt text, with `lang`. With **no** alt text: alt "Image 2 of 5, no description from the publisher". The visible caption then reads only "No description from the publisher" and is `aria-hidden` |
| Frame | **One** frame ratio for the carousel: the first image's `width`/`height` (each clamped to `1…4096`), with the ratio clamped to 4:3–16:9. Every slide uses `object-fit: contain` inside that frame. So there is no layout shift, and at 320 px the image scales inside the frame |
| Loading | The current slide loads eagerly and the **adjacent** slides are preloaded once the carousel is visible. Hidden slides are never left in a `loading="lazy"` state that would never load |
| Pointer | Swiping is optional and never the only way to move (2.5.1, 2.5.7) |
| Focus | `:focus-visible` uses `--focus-ring` (≥ 3:1). Focus never moves on its own |
| Single image | A plain `<figure>` without controls |
| Failure | Any non-`200` image, including a `503`, shows "Image unavailable" as text, and the count stays truthful. `<img>` cannot read the error key, so `NewsStoreGate` is not involved |
| Reduced motion | No animation; otherwise a fade of at most 150 ms |

### 6.2 `OdsExternalLink`

- It is a link (`<a>`) or plain text, never a button pretending to be a link.
- Its accessible name is the visible text plus "(opens in a new tab)".
- **Copy** is an `OdsIconButton` named "Copy link", ≥ 24 px, with a result announcement (§6.7).

### 6.3 Reused components

All other interactive parts are existing accessible `Ods*` components. Specific notes:

- `OdsRecordCard` keeps its disclosure (`aria-expanded`) and uses `HeadingLevel = 3`. Its `Actions`
  sit outside the trigger.
- The News Tags `OdsRecordTable` has the caption "News tags".
- The Version select is labelled "Version".
- The acknowledgement checkboxes are real labelled `OdsCheckbox`es.

### 6.4 Meaning never by colour or icon alone

| Meaning | Carried by |
|---|---|
| Read / unread | Chip text: visible "Unread" or sr-only "Read". The icon is neutral |
| Updated | Chip text plus the alert sentence |
| Feed health | Chip text; option text "(needs attention)" / "(paused)" / "(archived)" in the Feed filter |
| Storage limit reason | The `quotaReason` description text |
| Inherited tag | sr-only "(from feed)", plus the outlined variant **and** the `rss_feed` glyph |
| Archived tag | "Archived" in the *Status* column; "(archived)" in filter options |
| Attention severity | The row text |
| Quota | `aria-valuetext` plus the visible "12.4 of 50 GiB"; "Full" as text with a warning icon |

### 6.5 Contrast

- All text is ≥ 4.5:1 in both themes, including `Meta`, captions, the carousel counter, helper lines and
  placeholders (`--mud-palette-placeholder`).
- Read cards use the normal text tokens, so no dimmed variant needs checking.
- Focus rings, button boundaries, chip borders, the reading panel's border and the progress-bar track
  are ≥ 3:1.

### 6.6 Focus management

**The visible-target rule.** On `/news` and `/news-feeds` the `h2` is sr-only and never receives
focus. Wherever focus would otherwise go to "the list", it goes to:

- the **first card's header** when the list has rows;
- else the **empty state's heading** (`tabindex="-1"`, visible);
- if a reset's first page fails and the focused element is gone, the error state's **Retry**.

While a reset is still loading, focus stays where it was until the new first page arrives, and then
follows this rule. Focus moves **only when the reset removed the focused element**. A filter, sort or
search change leaves focus on its control, and focus is never pulled out of the search field while the
user types (3.2.2). On `/news-tags` the `h2` *Tags* is visible and stays a valid target.

| Event | Focus |
|---|---|
| Route change | The page `h1` |
| Card expand or collapse | Stays on the card header |
| Mark read / Mark unread from the `⋯` menu | Back to the card's `⋯` button |
| Undo in the snackbar (while available) | The card header it refers to |
| A menu action that keeps the card | Back to the `⋯` trigger |
| A menu action that removes the card from the current filter (Pause, Archive, Unarchive) | The next card's header, else the visible-target rule |
| Hide read | The visible-target rule (the pressed button is removed by the reset) |
| Mark all read dialog closes (finished, stopped or failed) | The visible-target rule |
| Create feed | The new card's header |
| Use new address succeeds | Stays on the card header |
| Create tag | The new row's name button |
| Delete feed | The next card's header, else the visible-target rule |
| Delete tag | The next row, else the `h2` *Tags* |
| A 404-removed card | The next card's header, else the visible-target rule |
| A fresh first page that removed the focused element | The visible-target rule |
| A fresh first page that keeps the focused element | Not moved |
| The user presses the pill and the append succeeds | The **first new card's header**, also when it was the last page and the pill disappears |
| The append fails | Stays on the pill, which now reads Retry |
| A load started by scrolling | Not moved |
| Show the rest | The first newly shown paragraph (`tabindex="-1"`) |
| A first-load `news-busy` automatic retry succeeds | If focus was on Retry: the region's heading (`tabindex="-1"`); for the article list, the visible-target rule |
| A first-load `news-busy` automatic retry fails | Retry is re-enabled and keeps focus |
| Feed edit `409` | The conflict alert (`tabindex="-1"`) |
| Feed Reload succeeds | The first field updated from the server, else the dialog's first field |
| Feed Reload fails | Stays on Reload |
| Feed Reload returns `404` | **Close** |
| Version Retry succeeds / fails | The version panel's heading (`tabindex="-1"`) / stays on Retry |
| Dialog open | The first field; for confirms, **Cancel** |
| Dialog close (all others) | The opener |
| Validation failure | The first invalid field, plus `OdsErrorSummary` |

Focus is never left on `<body>` after an action.

**Not obscured (2.4.11).**

- Cards, rows and headings get `scroll-margin-block` equal to the sticky header's height (token-based).
  Focus moves scroll the target clear of sticky bars.
- Snackbars sit bottom-right with a bottom inset token, so a focused element is never left fully covered
  by one.
- No news page adds a sticky footer.

### 6.7 Announcements

One polite `OdsLiveAnnouncer` per page. **Announcements never rely on the snackbar.**

- Every status change the snackbar shows is announced **once** by the page's announcer: "Marked as
  read", "Feed deleted…", "Couldn't update. Changed back.", "1,234 articles marked as read." and the
  like, with the same text as the snackbar.
- A bare `MudSnackbarProvider` cannot be assumed to announce, and the repo's own `ContactAliasSection`
  notes it. A component test asserts that the snackbar container is **not** a live region. If MudBlazor
  makes it one, the announcer call is skipped, so the message is never spoken twice.

| Trigger | Announcement |
|---|---|
| A fresh first page loaded (also when it fits on one page and no pill appears) | "1,234 articles", "More than 10,000 articles" or "No articles match these filters." |
| Search settled (300 ms debounce) | The same count sentence, once |
| An append | "50 more articles loaded, 100 of 1,234." or "Couldn't load more articles." (the pill's own region, §4.4) |
| *Relevance* fell back to Published, newest first | "Sorted by newest, because the search was cleared" |
| Hide read | "3 read articles hidden. 1,231 articles." (one announcement) |
| Expanding a card | "Loading article…" once; nothing when the body arrives within 1 s |
| `bodyUnavailable` or `bodyOmittedReason` set | The alert text, once, through the page's `OdsLiveAnnouncer`, when the card expands (an alert inserted with its text is not reliably announced on its own) |
| A search error | The message, once, when it first appears or changes (§7.4) |
| Attention count changed after the first load | "1 feed needs attention" or "No feeds need attention" |
| Pending feed changed state | "Example News: fetched" or "…: failing", once; the poll then stops |
| Pending poll reached 2 minutes | "Example News hasn't been fetched yet. Check again later." |
| Use new address succeeded | "Feed address updated", once |
| Feed state swap | "Example News paused", "…resumed", "…archived" or "…unarchived" |
| Card removed from the filter | "Example News paused and moved to Paused" |
| Retry before `Retry-After` | "Retrying in 25 seconds" |
| Copy | "Link copied", or "Couldn't copy. Select the text instead." |
| Version change | "Showing version 2", only after that version's body has loaded; for an omitted version, the omitted alert text instead, once |
| Carousel | Its own region (§6.1) |

Errors use `role="alert"` through the alert components.

**Snackbar provider configuration**, set in `App.razor` and verified against MudBlazor 9.12.0 at
implementation: bottom-right position, `ShowCloseIcon`, `PreventDuplicates`, and `VisibleStateDuration`
8000 ms for the Undo message. **Error snackbars stay until dismissed** (`RequireInteraction`): the
failed-update, busy, network and Mark all read partial-failure snackbars.

### 6.8 Language, text and reflow

- `lang` is set from the article `language` (validated BCP 47 from the API, with the catalogue as
  fallback) on the title, excerpt, body, alt text and every revision panel. An absent or invalid value
  sets no `lang`.
- Each feed's `language` (from the catalogue) is applied to its `displayTitle` and `description`
  wherever they are shown: feed cards, the Feed filter's options, card meta and the status panel's
  links.
- The body is real text that zooms to 200 %, reflows at 320 CSS px with no horizontal scroll, and
  survives text-spacing overrides. Long URLs wrap (`overflow-wrap: anywhere`).
- **Reflow at 320 px / 400 % zoom.**
  - The filter bar stacks.
  - Dialogs go full-screen below `sm`.
  - The Version select replaces any tab strip.
  - The News Tags record table switches to its stacked layout.
  - The carousel frame scales.
  - `OdsSortSelect` option labels wrap.
- Target sizes are ≥ 24×24 CSS px for every control, including icon buttons,
  with an aim of 40.

## 7. Loading, Empty and Error States

### 7.1 `news-busy`

| Context | Behaviour |
|---|---|
| A region's **first load** (article list, counts, feed list, catalogue, tags), and an append with the pill as the region | An `OdsAlert` with `role="status"`: "News is busy. Retrying in a moment." with **Retry**. After `Retry-After` it retries once by itself. **Retry** stays enabled during `Retry-After`; pressing it waits out the remaining time (§7.3). While the automatic retry runs, Retry stays in place with `aria-disabled` and the text "Retrying…"; it is never removed while it has focus. On failure, Retry is re-enabled and keeps focus, and the message becomes "News is busy. Try again in a moment." No snackbar |
| A **user action** (mark read, mark all read, save, archive, delete) | Snackbar "Busy. Try again in a moment." It stays until dismissed. Its retry action never becomes a dead button: it stays enabled and schedules the retry for the moment allowed (§7.3). Mark all read's mid-run rule is in §3.2.9 |
| **Search** | §7.4 |

### 7.2 Failure classes

| Backend failure | On screen |
|---|---|
| `news-storage-unavailable` (503), article detail | `Body.Error`: "The article text isn't available right now." with **Retry**, through `NewsStoreGate` |
| `news-storage-unavailable` (503), a revision | "Couldn't load this version." with **Retry** (§3.5) |
| Any non-`200` image | `ImageUnavailable` (§6.1) |
| `200` with `bodyUnavailable: true` | `Body.Unavailable` (§3.2.7) |
| `200` with `bodyOmittedReason` set | `Body.Omitted` (§3.2.7) |
| Validation 400 | Inline field errors (§8.1) |
| Search 400, 429, 503 | §7.4 |
| `400` keyed `cursor` | A fresh first page; not shown as a failure |
| 404 | The card or row is removed, and the snackbar reads "No longer available." Focus moves per §6.6 |
| `news-feed-duplicate` (409) | Inline on *Feed URL*: "You already follow this feed.", with a **Go to feed** link (`existingFeedId`) |
| `news-feed-changed` (409) | The edit-dialog conflict flow (§3.3) |
| `news-tag-duplicate` (409) | Inline on *Name*: "A news tag with this name exists." |
| `news-tag-in-use` (409) | §3.4 |
| `NewsFeedUrlRejected` (422, `url`) | Inline. The server's message wins over any client result |
| `news-feed-limit` / `news-tag-limit` (422) | An `OdsAlert` in the dialog: "The limit has been reached. An administrator can raise it in Settings." The tag limit is a constant, so its alert drops the Settings sentence |
| `news-feed-not-archived` / `-inactive` / `-not-quarantined` (422) | A snackbar with the server's message. Only a race reaches this |
| `NewsTagNotUsable` (422) | Inline on the tag picker: "One of these tags is archived." |
| `NewsMarkReadScopeInvalid` / `asOfUtc` (422) | "The list is out of date. Refresh and try again." with **Refresh**. The scope error covers an unknown id only |
| A pending-deletion feed id (422) | Never reached on load: stale ids are dropped first (§3.1) |
| Detach 403 | The dialog names the missing permission |
| Network failure | `Error.Load`, with **Retry**; an action's network snackbar stays until dismissed |

### 7.3 Retry-After

A **Retry** facing a `Retry-After` stays **enabled**, never a focusable dead button. It shows "Available
in 25 s" as text, updated at most every 5 s without being announced. Pressing it early schedules the
retry for the moment allowed and announces "Retrying in 25 seconds".

### 7.4 Search messages

- One helper element under the field (`id="news-search-help"`), linked by `aria-describedby`. While
  there is no message, it holds only **sr-only** text: "Searches titles, authors and summaries, not the
  full article." Nothing visible renders under the field, as on the other pages.
- On an error, the message shows **visibly** in that element, under the field, and disappears when the
  message clears:

| Response | Message | What happens next |
|---|---|---|
| `400` (all terms shorter than 3 characters, stopwords included) | "Use words of at least 3 letters." | Nothing is re-sent |
| `429` | "Too many searches. Searching again in a moment." | The search re-sends once by itself after `Retry-After` if the query is unchanged |
| `503 news-busy` | "Search is busy. Searching again in a moment." | The search re-sends once by itself after `Retry-After` |
| The automatic re-send fails as well | "Search didn't run. Press Enter to try again." | **Enter** in the field sends the search again |

- `aria-invalid="true"` is set **only** for the `400`.
- A visually hidden `role="status"` region announces each error message **once**, when it first appears
  or changes, not on every keystroke.
- The message clears when a search succeeds. No error snackbar appears, and the field never needs a
  Retry button.

## 8. Client-Side Validation, Rendering Rules and Feedback

### 8.1 Validation

The client mirrors the backend's annotations through **`NewsLimits`**. The server stays the enforcer,
and **a server `400`/`422` always wins** over the client result.

| Field | Check | Message |
|---|---|---|
| Feed URL | Required; ≤ `MaxUrlLength`; an absolute `http`/`https` URI; no user info | "Enter the feed's full address, starting with http:// or https://." |
| Custom title | ≤ `MaxCustomTitleLength` | "Keep it under 256 characters." (interpolated) |
| Check every | Empty, or `MinFetchIntervalMinutes`–`MaxFetchIntervalMinutes` | "Use 15 to 1440 minutes, or leave empty." (interpolated) |
| Tag name | Required; ≤ `MaxTagNameLength` (64) | as stated |
| Tag description | ≤ `MaxTagDescriptionLength` (256) | as stated |
| Search | ≤ `ListDefaults.MaxSearchLength` | A counter appears near the limit |
| Allow-list | The shared `SettingItem.Rule` delegate | Its message |

Errors use `aria-describedby` and `aria-invalid`, and focus moves to the first invalid field.
Private-address and port rules are never pre-checked on the client.

### 8.2 Publisher text

Every string from a feed is rendered **only** as Blazor-encoded text, through components:

- titles, authors, excerpts and bodies;
- alt text;
- tag names on news chips;
- the feed `displayTitle` and `description`.

The rules:

- **No `MarkupString`, no `@((MarkupString)…)`, no `AddMarkupContent` and no JS `innerHTML`** anywhere
  under `Pages/Internet/**`, in the new components (`OdsImageCarousel`, `OdsExternalLink`,
  `NewsArticleBody`, `NewsFeedHealthChip`, `OdsRelativeTime`), in the News settings rows or in the News
  status panel. Copy with emphasis, such as confirm text, is a `RenderFragment` with `<em>@title</em>`.
- **Hostile-payload test.** `'<img src=x onerror=alert(1)>'` and `javascript:alert(1)` are placed in
  every publisher-sourced field of the stubbed API responses. They must render as visible text and
  create no element.

### 8.3 Data lifecycle

- `NewsCatalogueCache` and all page caches are **keyed by user id** and **cleared on
  `AuthenticationStateChanged`**. One user's data never shows to the next user, even without a reload.
- A **feed URL** never appears in a route, a query string, a tooltip, the live announcer, `PageTitle`
  or a client log. It is shown masked (§3.3).
- **Accepted residual:** a feed hosted on an allow-listed internal server may declare an internal
  `siteUrl`, and that host is shown to readers. It is the publisher's own declared link.

### 8.4 Success feedback

- A snackbar confirms completed actions: create, delete, mark all read, refresh and settings save.
- Toggles (read state, tags) change inline.
- Nothing navigates on success, except **Go to feed**, **View articles** and **Show articles from
  ‹feed›**.

### 8.5 Source lints and structural tests

In `Odyssey.Client.Tests`:

1. **Markup lint.** No `MarkupString`, `@((MarkupString)…)`, `AddMarkupContent` or `innerHTML` in the
   paths listed in §8.2.
2. **Restated-limits lint.** No numeric literal for any `NewsLimits` value, and no `const` holding an
   admin-editable cap, in the news client code.
3. **Text-quota lint.** No `NewsTextStoreMaxGiB` default in the News pages; the text bar and the warning
   line read only `GET /api/news-status`.
4. **Snackbar test.** The snackbar container is not a live region (§6.7).

## 9. Design System Compliance

- Tokens only. `70ch` is the one deliberate typographic unit.
- Dark is primary, light is first-class, and every state is verified in both.
- Tide and Sea are brand colours only. Health uses semantic tones, always with text.
- Tag colours go through `HexColor.Normalize`.
- `docs/frontend-mudblazor-gotchas.md` is followed for the new components.
- Accounts is the reference page for the header overview, the filter bar and the Feeds page; Contracts
  for the filter bar order and the Attention problems.

### 9.1 Design-system additions

Published to the artifact (version 227 is the reference), with the owner's approval to publish
directly:

1. `ImageCarousel` (§6.1) and `ExternalLink` (§6.2) as shared components: component, `.d.ts`, README
   and preview.
2. The `NEWS_FEED_HEALTH` registry and `NewsFeedHealthChip`, with the shared `NeedsAttention` helper
   matching `Failing`, `Gone`, `Quarantined` and `QuotaReached`.
3. `ui_kits/web/News.jsx`, `NewsFeeds.jsx` and `NewsTags.jsx`, with mock data, and `news.css` (the
   `.nw-reading` panel). They show the §3.7 states, Hide read, the versions dialog, the feed dialogs, the
   header overview and Attention, and the in-use tag delete.
4. The Internet module in the Drawer and module-switcher specimen.
5. README catalogue rows and page-map rows.
6. The kit matches §4.2: the `PageHeader` problems toggle's "‹label›, ‹count›" name and the
   `OdsSortSelect` field-width menu.

The kit carries no re-initialise dialog, *Lost* state or visible search helper line. The client syncs
through `odyssey-design-system-changes`.

## 10. Performance and Perceived Responsiveness

- **Payload.** List rows carry excerpts only (≤ 300 characters). Bodies load on expand, and render in
  chunks of 50, then 200 per press. Only the selected revision loads and renders.
- **Pagination** is server-side and cursor-based, through the remote `OdsInfiniteList`. Previous rows
  stay visible on a fresh first page.
- **Skeletons** on first load only.
- **Optimistic writes** for read state:
  - On failure the change rolls back, the counts revert and the snackbar reads "Couldn't update. Changed
    back." Focus does not move.
  - Writes are serialised per article, and the last one wins.
- **Counts** refresh with a 1 s debounce after read-state writes.
- **Images.** The current slide loads first and adjacent slides are preloaded. The aspect frame
  prevents layout shift. Images revalidate with `no-cache` + `ETag`.
- **Polling** happens only for a pending feed: every 10 s, for at most 2 min, and it can be stopped.
- `OdsRelativeTime` ticks once a minute on one shared ticker, without announcements.
- The service worker does not precache news assets and never serves `/api/news-*` responses or news
  image bytes from its cache.

## 11. Acceptance Criteria

1. **Navigation and routes.** A `news.read` holder sees the Internet module, with News, Feeds and News
   Tags, in the switcher, the rail and the palette. Each page has its `PageTitle` and focuses its `h1`
   on navigation. Without `news.read`, none of them is shown, and a direct visit gets the standard
   not-authorised view (route policy).
2. **Reader happy path.** Unread articles are listed newest first as `h3` cards under the sr-only `h2`
   *Articles*, with the chips, meta and counts as specified.
3. **Expand marks read.**
   - Expanding an unread card sends one `read-state` call with `true`.
   - The *Unread* chip becomes the sr-only *Read* chip, and the counts drop.
   - One snackbar with **Undo** appears. It lasts ≥ 8 s and pauses on hover or focus, or else requires
     interaction (§3.2.8). The change is announced exactly once.
   - Undo, and **Mark unread** in the `⋯` menu, each restore unread with one call.
   - Expanding a read card sends nothing.
4. **Stability and Hide read.**
   - Under Status = Unread, read cards stay in place.
   - Pressing **Hide read** takes a fresh first page, makes the one combined announcement and moves
     focus per §6.6.
5. **Mark all read.** The request carries the list's filters (search, feeds, tags and Status) and the
   `asOfUtc` from the list response, and repeats while `hasMore`. With both a feed and a tag set, it
   carries both and marks exactly the articles the list showed as unread. An article first seen after
   `asOfUtc` stays unread. A `422` shows the out-of-date message with **Refresh**.
6. **Layout and reflow.**
   - At ≥ 960 px the carousel sits beside the text (8/4). Below that it sits underneath.
   - At 320 px, and at 400 % zoom on a 1280 px viewport, no news page or dialog scrolls horizontally.
     The filter bar stacks, dialogs go full-screen, the News Tags record table stacks and long URLs wrap.
7. **Publisher text.** The hostile-payload test (§8.2) renders every payload as visible text, with no
   element created. The source lint finds no `MarkupString`, `AddMarkupContent` or `innerHTML` in the
   listed paths.
8. **Long body.**
   - A 256 Ki-character body of single-line paragraphs renders 50 paragraphs and **Show the rest (N
     more paragraphs)**.
   - Pressing it renders 200 more and focuses the first new one.
   - The page stays responsive: no long task over 200 ms when expanding the card (measured in the E2E
     smoke).
9. **Carousel a11y.**
   - `role="group"` with the roledescription, and no landmark.
   - Previous and Next work by Tab, Enter, Space and the arrow keys.
   - At the ends, a press announces "First image" or "Last image".
   - The counter is `aria-hidden`, and the live region speaks only on user action.
   - An image without alt text gets the fallback alt, an `aria-hidden` caption, and "Image 2 of 5" alone
     in the live region.
   - A single image renders as a plain figure.
   - A failure shows "Image unavailable".
   - Adjacent slides are loaded.
   - Nothing auto-advances.
10. **External link.** `https://…` renders a link with `rel="noopener noreferrer"`, and its name
    includes "(opens in a new tab)". `javascript:…`, `data:…` and a URL with user info each render as
    plain text with Copy, with no `href`. An IDN host shows its punycode token. Copy announces success
    or failure.
11. **Earlier versions.** The Version select lists versions newest first, ending with *Original*, from
    metadata only. Selecting one loads only that body, renders it with `lang`, and announces it after it
    loads.
12. **Tags.**
    - The dialog saves own tags with one `PUT`.
    - Inherited tags show as read-only with "(from feed)", the outlined variant and the glyph.
    - Removing a single own tag happens only in the dialog, opened from **Tags…** in the `⋯` menu, and
      takes one `PUT` on Save. No `DELETE …/tags/{tagId}` is sent from the card.
    - Nothing interactive renders inside the card's expand button; the chips carry no remove button.
13. **Feeds.**
    - Create validates on the client and lets a server error win.
    - It focuses the new pending card, polls at most 2 min, and offers **Stop checking** and **Check
      again**.
    - It announces the first state change once.
    - A duplicate shows the inline error with **Go to feed**.
14. **Feed menu.** Items the caller lacks the claim for, or that the feed's state rules out, are absent.
    Delete needs the acknowledgement checkbox.
15. **Management and leaks.**
    - With only `news.read`: no feed URL, no error detail and no fetch host appear anywhere in the DOM,
      page title, announcer or tooltips. This is tested with a fixture whose `siteUrl` host differs from
      the fetch host.
    - With `news-feeds.update`: the URL shows masked until **Show full address** is pressed.
    - After signing out and signing in as another user without a reload, no cached feed or tag data from
      the first user is shown.
16. **News Tags.**
    - The page lists, creates, edits, archives, unarchives and deletes news tags, using the existing
      icon and colour pickers.
    - An in-use delete shows the counts, and offers **Remove from all and delete** only to a caller
      holding the claims for every link class present.
    - The existing tag pages are unchanged.
17. **States.** Each state in §3.7 is reachable in a component test, including every body state.
18. **Failure classes.** Each row in §7 is produced by a stubbed `ApiResult` and renders as specified.
    Retry before `Retry-After` stays enabled, schedules the retry and announces it.
19. **Optimistic rollback.** A failed read-state write restores the chip and counts, and the snackbar
    reads "Couldn't update. Changed back."
20. **Keyboard and focus.** Every flow in §3 completes by keyboard alone, with focus as in §6.6. Focus is
    never on `<body>` after an action. No focused element is hidden behind a sticky bar or a snackbar
    (2.4.11).
21. **Screen reader.** With NVDA + Firefox and with VoiceOver + Safari:
    - Card names, states, chips, the carousel, the external link, the quota progressbars and every
      announcement in §6.7 are spoken as specified.
    - "Unread" is spoken once per card.
22. **Contrast and themes.** An automated AA check passes on all three pages and the admin panel, in
    dark and light. Read cards use the normal text tokens.
23. **Settings.**
    - The News group shows nine rows (eight for a caller without `system-settings.security.update`),
      with bounds served by the API. The *Image size limit* maximum is
      `min(range max, NewsImageMaxSizeKbCeiling)` and its help line shows that number.
    - The allow-list Save lists the added and removed hosts before writing.
24. **Admin panel.** The panel renders in the `Ready`, `Degraded` and `Unconfigured` storage states. The
    job card shows the `NewsFeedJobRun` counters.
25. **Language.** For an article whose feed language is `nb`, the title, excerpt, body, alt text and
    revision panels carry `lang="nb"`. An invalid language sets no `lang`.
26. **No restated limits.** A source lint finds no numeric literal for any `NewsLimits` value, and no
    `const` holding an admin-editable cap, in the news client code.
27. **Times.** Every relative time is a `<time datetime>` with the absolute date as text. No date is
    shown only in a tooltip.
28. **Service worker.** The service worker never serves news API responses or images from its cache.
29. **One announcement.** Each status change is announced exactly once. The snackbar container is not a
    live region, and error snackbars stay until they are dismissed.
30. **Undo withdrawn.** After **Hide read** or any fresh first page, an open Undo snackbar loses its
    action and shows the Mark-unread sentence.
31. **Mark all read count.** Under Status = Unread, with no search and an uncapped total, the confirm
    shows a number with no feed or tag filter (the overall unread total), with exactly one feed alone, and
    with exactly one tag alone. Every other combination shows no number. When the only filter is one archived feed,
    which `unread-counts` omits, it shows no number rather than 0.
32. **Unavailable menu items.**
    - No row menu on the news pages contains a disabled item.
    - Every non-archived feed, including a quarantined one, shows the visible "archive it first"
      sentence to a caller holding both claims, and to no other caller.
    - Each state swap is announced exactly once.
33. **Scrolling list.** The list appends a page per `LoadMore` using `nextCursor`. The pill shows "of
    10,000+" while capped and disappears at `nextCursor: null`.
34. **`news-busy`.** On a first load it shows the region's Retry state, retries once after
    `Retry-After`, and shows no snackbar. On an action, it shows the snackbar.
35. **Search errors.** The search error is announced once, sets `aria-invalid` only on `400`, and a
    `429` re-sends once after `Retry-After`.
36. **Feed edit conflict.** On a `409`, focus moves to the alert. Reload stores the new version, keeps
    the user-only fields, lists the server-updated fields and moves focus as §3.3 says.
37. **Mark all read cancelled.** Cancelling stops after the in-flight call. The partial total is shown
    in the snackbar and announced once, never twice: by the page's `OdsLiveAnnouncer`, with the
    snackbar container not a live region.
38. **Quota bars.** Both quota bars have an accessible name, a clamped value and "Full" in
    `aria-valuetext` at the quota.
39. **Text quota source.** The text bar and the warning line read only `GET /api/news-status`. A
    source lint finds no `NewsTextStoreMaxGiB` default in the News pages.
40. **Search re-send.** After a `429` or a `503` on search, the search re-sends once. A second failure
    shows the Enter hint, and Enter re-sends.
41. **Reload failures.** A failed Reload keeps the alert and keeps focus on Reload. A `404` disables Save
    and moves focus to Close.
42. **Version race.** A late response for a version that is no longer selected is discarded and not
    announced.
43. **Mark all read focus and progress.** After Mark all read in any outcome, focus is on the first card
    header or the empty-state heading. The progress status updates at most once every 5 s.
44. **Busy auto-retry.** A first-load automatic retry never removes a focused Retry. On success, focus
    moves to the region's heading, or for the article list to the visible target (§6.6).
45. **Append failure and reset.** A failed append keeps the loaded rows and offers Retry on the pill. A
    filter change resets the list to a fresh first page.
46. **Size controls.** No News page renders `OdsPager`. The only size control on a News page is the
    "Load N at a time" select: in-memory on Feeds and Tags, and the request `limit` on the article list.
    The in-memory `OdsInfiniteList` pages are unchanged by the remote mode (their tests pass unchanged).
47. **Pill focus.** A pill press moves focus to the first new card header, also on the last page. A reset
    that removes the focused card moves focus to the first card header or the empty-state heading.
48. **One load at a time.** The component never calls `LoadMore` concurrently. After a failure it does
    not load on scroll until Retry. A sentinel still in view after an append triggers the next load.
49. **Stale responses.** A response from before a filter change is discarded. A `400` keyed `cursor`
    restarts from a fresh first page.
50. **First-page announcement and pill name.** Each fresh first page is announced with its count (or
    "More than 10,000", or "No articles match"). The pill's accessible name equals its visible text in
    every state.
51. **Expanded card.** The expanded card renders no Mark read/unread or Tags button, and no *Feed* tile.
    The `⋯` menu, named "Actions for ‹title›", holds Mark read/unread, Tags… and Show articles from
    ‹feed›. Nothing interactive renders inside the card's expand button.
52. **Filter bar.** The filter bar renders, in order, `OdsSearchField`, three `OdsMultiSelect` (Feed,
    Tag, Status), `OdsSortSelect` and `OdsPageSizeSelect`. No segmented control, switch or date range is
    rendered.
53. **Status filter.** With Status = Unread only, the request carries `isRead=false`. With both or
    neither selected, it carries no `isRead`.
54. **Search helper.** With no search message, nothing visible renders under the search field, and the
    sr-only description is present. A `400` shows the message visibly and sets `aria-invalid`.
55. **Hide read placement.** Hide read appears among the header actions only under Status = Unread with
    a read card in view.
56. **Filter cap and deep link.** With 50 feeds selected, the other options are `aria-disabled`, the
    status line shows, and the request never carries 51 ids. A `?feedId=` link preselects exactly that
    feed.
57. **Overview and Attention.** No alert renders above the article list. The overview tiles show the
    catalogue counts. The Attention problems list exactly the `NeedsAttention` feeds, with the
    severities in §3.2.2. Quota rows appear only with `file-storage.read`, and their Settings → link
    only with `system-settings.read`. Review → reveals the feed on `/news-feeds` even when it is paused
    or archived.
58. **Reading panel.** The expanded card's body and carousel render inside one reading-panel container
    that has no role and no tab stop.
59. **No visible divider.** No visible divider sits between `PageHeader` and the list on News or Feeds.
    The sr-only `h2` exists and never receives focus. Where focus moves to the list, it lands on the
    first card header or the empty-state heading, with a visible focus indicator.
60. **Feeds overview and Status.** On Feeds, the By status rows sum to the feed count. The Status filter
    defaults to Active, and "Status: All" shows when none is ticked.
61. **Sort menu and baseline.** The `OdsSortSelect` menu width equals its field's width. The Tags filter
    controls share one baseline.
62. **Problem actions.** On every page, the problem action arrows are `aria-hidden`, and
    `ActionAriaLabel` overrides the action's name when set.
63. **Omitted body.** *(Added at merge, from the backend cap decision.)* For an article whose detail has
    `bodyOmittedReason` `TooLarge` or `TooComplex`, the reading panel renders no body, shows an Info
    `OdsAlert` with `role="status"` reading "This article is too large or complex to show here.",
    followed by an `OdsExternalLink` to the original, and still shows the carousel when the article has
    images. The alert text is announced exactly once through the page's `OdsLiveAnnouncer` when the card
    expands, and the same announcer call is used for `Body.Unavailable`. With `bodyOmittedReason` null,
    the body renders as usual. In the earlier versions dialog, a revision with `bodyOmittedReason` set
    renders no revision body, shows the same Info alert, and announces its text once in place of
    "Showing version N".
64. **Tags sort over the whole set.** On `/news-tags`, sorting orders the whole tag set, not just the
    loaded rows, and growing the window with the pill or the sentinel keeps that order.
    `OdsRecordTable` is not nested inside `OdsInfiniteList`, which renders only its footer after the
    table.
65. **Suggested feed address.** With `suggestedUrl` set, the management block shows "This feed has moved
    to ‹url›." with the URL under the `OdsExternalLink` rules, and **Use new address** only for
    `news-feeds.update` holders. Pressing it sends one `PUT` with `url` = `suggestedUrl` and the current
    `version`; success shows "Feed address updated", announced once, and leaves focus on the card
    header. A `409` opens the Reload flow. No feed URL changes without a `PUT`.

## 12. Rollout Plan

### 12.1 Phase 1 (MVP)

1. Design-system additions published and synced (§9.1).
2. The changed shared components (§4.2), each with its existing tests passing unchanged.
3. The new components and services (§4.1).
4. The `/news`, `/news-feeds` and `/news-tags` pages, the Internet module in `NavModel`, and the palette
   entries.
5. The News group on `/settings` and the `/background-jobs` presentation and panel.
6. Tests:
   - component tests in `Odyssey.Client.Tests` for every state and failure class, plus the source lints
     (§8.5);
   - one E2E smoke in `Odyssey.E2ETests` via `E2ESignIn`: subscribe to a local fixture feed, read an
     article, mark all read, and expand a long body.

**Dependency.** The backend (`news-reader-backend.md`, issue #470) lands first.

### 12.2 Committed follow-up

- **"Fold news tags into a key-generic `OdsTagAdmin`."** Filed as its own issue when this spec is
  implemented. The module has no milestone, so that issue has none either.

## 13. Future Work (not committed)

- Full-article-text search. v1 searches the title, author and summary only.
- A published-date range in the filter bar (the API already accepts `from`/`to`).
- Clickable links in the body.
- Keyboard shortcuts with an off switch.
- A split reading pane.
- A revision diff view.

## 14. Assumptions and Decisions

**Assumptions.**

1. The module is called "Internet" and the reader page "News".
2. The default reader filter is Status = **Unread**.
3. "Load N at a time" presets are 25, 50 and 100, with 50 as the default, on all three pages.

**Resolved with the user.**

- Expandable record cards; mark-read on open with Undo; a new Internet module; no shortcuts.
- **Hide read** is included, as a header action.
- The design-system additions are published directly to the artifact.
- Unavailable menu actions are omitted, not disabled.
- `OdsExternalLink` is a shared design-system component.
- Feed and tag filters combine.
- The article list scrolls; it has no pager.
- The expanded card does not repeat the header (no action row, no *Feed* tile, no tags).
- The standard filter bar, with a Status multi-select instead of an Unread/All switch, and no date
  range.
- One reading panel; the header overview and Attention replace the feeds-need-attention alert.
- No visible separator under the header; the Feeds page and filter bar follow Accounts.
- Search covers title, author and summary only; full-body search is out of v1.

**Open items to confirm at implementation** (with the backend):

- That `GET /api/news-status` keeps its `file-storage.read` gate.
- Whether the image endpoint also sends `Content-Security-Policy: sandbox`.

## 15. Decisions Taken at Merge

Each was a conflict the source amendments did not settle. All are resolved.

1. **Single-tag removal: resolved.** Only through the `⋯` menu's **Tags…** dialog, with no per-chip
   remove button and no `DELETE …/tags/{tagId}` from the card, because the chips render inside the
   expand button, where nothing interactive may render.
2. **Mark all read total: resolved.** Announced exactly once by the page's `OdsLiveAnnouncer`, with the
   snackbar's text, because the general rule (AC 29) keeps the snackbar out of the live regions.
3. **Mark all read number: resolved.** Shown with no feed or tag filter and with exactly one feed or one
   tag alone, because only those have an exact count; no number for any other combination.
4. **News Tags list: resolved.** An `OdsRecordTable` fed the page-held, page-sorted window as `Rows`
   with `ServerSort="true"`, followed by `OdsInfiniteList` in a new footer-only mode, because a table
   cannot be nested in the list and sorting must cover the whole set, as in design system v227.
5. **Attention row text: resolved.** "‹feed›. ‹health text›", because it is the component's existing
   format; the colon variant is removed.
6. **Settings row label: resolved.** "Image size limit", because the later wording wins.

## 16. Acceptance Criteria Mapping

Old ids are the issue's numbering. Ids 33–40 were never assigned.

| Old | New | Old | New | Old | New |
|---|---|---|---|---|---|
| 1 | 1 | 23 (rows amended to nine; ceiling added) | 23 | 52 | 44 |
| 2 | 2 | 24 (replaced: storage states) | 24 | 53 | 45 |
| 3 | 3 | 25 | 25 | 54 (replaced twice; size select restored) | 46 |
| 4 (Hide read now a fresh first page) | 4 | 26 | 26 | 55 | 47 |
| 5 (amended: feed + tag, repeats) | 5 | 27 | 27 | 56 | 48 |
| 6 | 6 | 28 | 28 | 57 | 49 |
| 7 | 7 | 29 | 29 | 58 | 50 |
| 8 | 8 | 30 | 30 | 59 | 51 |
| 9 | 9 | 31 (number rule settled) | 31 | 60 | 52 |
| 10 | 10 | 32 (replaced: absent items) | 32 | 61 | 53 |
| 11 | 11 | 41 (pager versions withdrawn; scrolling-list version) | 33 | 62 | 54 |
| 12 (rewritten: dialog-only removal) | 12 | 42 | 34 | 63 | 55 |
| 13 | 13 | 43 | 35 | 64 | 56 |
| 14 (replaced: absent items) | 14 | 44 | 36 | 65 | 57 |
| 15 | 15 | 45 (focus sentence withdrawn) | 37 | 66 | 58 |
| 16 | 16 | 46 | 38 | 67 | 59 |
| 17 | 17 | 47 | 39 | 68 | 60 |
| 18 | 18 | 48 | 40 | 69 | 61 |
| 19 | 19 | 49 | 41 | 70 | 62 |
| 20 | 20 | 50 | 42 | *new* (omitted body; revision case added) | 63 |
| 21 | 21 | 51 (focus target changed) | 43 | | |
| 22 | 22 | | | *new* (tags sort order) | 64 |
| | | | | *new* (suggested feed address) | 65 |
