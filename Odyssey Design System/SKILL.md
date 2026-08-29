---
name: odyssey-design
description: Use this skill to generate well-branded interfaces and assets for Odyssey, either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the README.md file within this skill, and explore the other available files.

If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, you can copy assets and read the rules here to become an expert in designing with this brand.

If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

## What's here

- `README.md` — product context, content fundamentals, visual foundations, iconography.
- `colors_and_type.css` — every design token as a CSS custom property. Names mirror MudBlazor's `--mud-palette-*` so they wire 1:1 into a custom `MudTheme.PaletteDark` / `PaletteLight`.
- `assets/` — Odyssey logomark + wordmark SVGs and the Odyssey compass logomark rasterized as the favicon set (16 / 32 / 192 / 512).
- `preview/` — small HTML cards specifying type, color, spacing, components.
- `ui_kits/web/` — React + JSX recreation of the Odyssey web app (Blazor WebAssembly / MudBlazor v8). Open `index.html` for a click-thru.

## Quick rules of thumb

- Stack: .NET 10 / Blazor WebAssembly / **MudBlazor v8** / Roboto + Roboto Mono + Material Icons.
- Dark mode is the **primary** surface; light is a first-class alternate.
- **Tide** (phosphor teal) is brand. Sea (sky-cyan) is secondary. **Never** use brand colors to encode income/expense — use mint and coral for those.
- No emoji. No gradients in product chrome. No decorative illustration. The product is a financial instrument; stay quiet.
- Numbers always tabular. Currency code is per-account ISO 4217. Negatives use `−` and the expense color — not parentheses.
- Icons: Material Icons font (already loaded via Google Fonts). Filled weight at 24px default, 20px in dense rows.
- Spacing: 4px base. Use `--space-1..16`. MudBlazor's `pa-N` / `ma-N` utility classes match 1:1.

## When you build something new

1. Start the file with `<link rel="stylesheet" href="…/colors_and_type.css">` (or `@import` it). All tokens flow from there.
2. Component vocabulary follows MudBlazor: buttons are `Filled` / `Outlined` / `Text`; cards are `Outlined` or elevated; text fields use `Variant.Outlined`; nav is a `MudDrawer` + `MudNavLink`. Reuse the components in `ui_kits/web/Components.jsx` rather than re-inventing.
3. Layouts: authed views use the App Shell pattern (left `Drawer` 240px is the only chrome — brand lockup at top, primary nav, then a footer group of Preferences / User Account / About; no top AppBar) with a max-width `Large` container. Auth screens are a centered 420px card.
4. Use the icon mappings in `README.md` for product concepts — don't pick new Material Icons for `Accounts`, `Budgets`, `Transactions`, etc.
5. Voice: short, second person, no marketing hype. Error messages follow `"Unable to *do thing*. *Recovery action*."`.

## When you're unsure

Look at:
- `ui_kits/web/Dashboard.jsx` for the standard page header + stat-tile + card pattern.
- `ui_kits/web/Transactions.jsx` for the standard table + filter pattern.
- `ui_kits/web/Contacts.jsx` for the flat, searchable, filterable list on the shared `RecordTable` (expand-to-detail + inline edit), and the **vCard (.vcf) import/export** pattern (overflow-menu export all / export filtered + per-row export, UID-matched update-in-place import → created/updated/skipped summary; `ContactImportModal.jsx`).
- `ui_kits/web/Contracts.jsx` / `ui_kits/web/Subscriptions.jsx` for the expandable **record-card** list (`.acct-list` / `.acct-item`: avatar + name + status chip + tag line + right-hand figure, expand-to-detail + inline edit); Subscriptions adds the `BillingInterval` pickers/chips and independent Paused / Archived states.
- `ui_kits/web/TaxStatements.jsx` / `ui_kits/web/Insurance.jsx` for the expandable-record + derived-status + per-currency-rollup pattern (sisters of the Accounts/Budgets list).
- `ui_kits/web/Contracts.jsx` for the expandable-record + derived-status + one-of-three polymorphic link (parties) + library-file reference pattern (`/contracts`).
- `ui_kits/web/Journal.jsx` for the shared, searchable journal (record cards + `JournalPhotoGallery` + attachments + id-only contact links with an "Unavailable" fallback) and `ui_kits/web/Tasks.jsx` for the `TaskBoard` kanban (Backlog/Doing/Done, drag-and-drop + keyboard move path) with a flat list view (`/journal`, `/tasks`).
- `ui_kits/web/SystemSettings.jsx` + `system-settings-data.js` for the **System settings** catalogue (63 rows across sixteen sections) — including the **file-analysis runtime** rows added in issue #439: the live kill switch, the model, and the shape-validated provider base URL, plus the advisories each one carries. The consent-gate and audit-trail halves of that feature are `AnalyzeFileModal.jsx` (feature-off state + the `409 disclosure_changed` re-prompt) and `FileAnalysisLog.jsx` (processor / region / destination host in force). Specimens: `components/file-analysis-runtime.html`, `preview/28`.
- `ui_kits/web/AcceptTerms.jsx` for the **License / ToS acceptance** interstitial (`/accept-terms`, own layout — two-step scroll-gated wizard, independent accept/decline, unavailable-document Continue fallback) and `ui_kits/web/LegalDocuments.jsx` for the admin **Legal Documents** panel in System Settings (versioned ToS authoring + publish + retained history + on-demand viewer, `UsersManage`-gated). `/register` (in `Login.jsx`) adds the License/ToS review checkboxes with read-in-Modal links; seed content is `legal-data.js`. Specimens `preview/103-105`.
- `ui_kits/web/ForgotPassword.jsx` + `ui_kits/web/ResetPassword.jsx` for the **forgotten-password recovery** pair (`/forgot-password`, `/reset-password?code=…`), reached from the **Forgot your password?** link on `/login`. Enumeration-safe neutral confirmation, single-use 1-hour token, and the shared **`PasswordRules`** requirement checklist (`components/PasswordRules.jsx` + `PASSWORD_POLICY`) — the one source of the 16-char + four-class rules, also consumed by Register and `/account` change-password. Specimens `preview/106-107` (pages) and `preview/21` (the checklist).
- `ui_kits/web/Calendar.jsx` for the shared household **Calendar** (`/calendar`, under the Journal module): colour-coded calendars with one-off + recurring events across **month / week / day / agenda**, keyboard-navigable date/time pickers, and **iCalendar (.ics) import + VEVENT export** (overflow-menu export all / export filtered with scope prefill, per-event export with an occurrence-vs-series prompt; `ExportCalendarEventsModal.jsx`, `ImportCalendarModal.jsx`). Tasks (VTODO) and Journal (VJOURNAL) mirror the same import/export family — see `ImportTasksModal.jsx` / `ImportJournalEntriesModal.jsx` and the README's *Import & export* section. Built on the net-new DS atoms **`CalendarGrid`** (month), **`TimeField`** (24h `HH:mm`), and **`ColorSwatchSelect`** (curated calendar palette) — specimen `components/calendar-module.html`.
- `preview/*.html` for state matrices on individual atoms (numbered in Design System tab order: brand → colors → type → spacing → components → reference data → dialogs).

If something's missing (a new screen, a new component), copy the closest existing pattern and adapt — don't invent novel layouts.
