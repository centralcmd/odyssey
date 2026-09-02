# Odyssey Design System

A design system for **Odyssey** — a personal-finance product that lets people track bank accounts, log transactions, build budgets, and upload receipts. The brand idea: **navigation** — maritime in spirit, terminal in texture. Finance as a long voyage you can chart, not a sprint you have to win.

> **Stack reality check.** Odyssey is a .NET 10 / Blazor WebAssembly app whose frontend is built on **MudBlazor v8** (Material-Design-flavored components) with **Roboto** + **Material Icons** loaded from Google Fonts. The codebase ships with MudBlazor's default theme (no custom palette wired in yet), so this design system is the proposed visual identity — anchored to what's installed today, free to define everything that isn't.

This system targets **dark mode as the primary surface**, with a fully-mapped light mode.

> **New here? Start with [Quick reference](#quick-reference) below** — the cheat sheet, the full component catalog (name → purpose → specimen), the token map, and the page/template map. The long prose sections under it are the *why* and the per-feature detail; the Quick reference is the *what* and *where*.

### Contents

- [Quick reference](#quick-reference) — cheat sheet · component catalog · token map · page & template map
- [Sources used to build this](#sources-used-to-build-this)
- [Product context, in one breath](#product-context-in-one-breath)
- [Brand idea](#brand-idea)
- [Index — what lives where](#index--what-lives-where)
- [Content fundamentals](#content-fundamentals) — voice, casing, numbers, don'ts
- [Visual foundations](#visual-foundations) — color, type, spacing, layout, cards, buttons, motion
- [Accessibility](#accessibility) — contrast, keyboard, focus, targets, SR semantics
- [Components — data table, menu & form controls](#components--data-table-menu--form-controls)
- [Iconography](#iconography)
- [Components — Dialogs](#components--dialogs)
- Reference data: [Contact types](#reference-data--contact-types) · [File types](#reference-data--file-types) · [Term kinds](#reference-data--term-kinds) · [Insurance types](#reference-data--insurance-policy--document-types) · [Contract types](#reference-data--contract--document-types) · [Billing interval](#reference-data--billing-interval)
- Feature pages: [Budgets](#components--budgets-page) · [Tax Statements](#components--tax-statements-page) · [Insurance](#components--insurance-policies-page) · [Contracts](#components--contracts-page) · [Subscriptions](#components--subscriptions-page) · [Transactions](#components--transactions-page) · [Files](#components--files-page) · [Users](#components--users) · [User Account](#components--user-account) · [Authentication](#components--authentication)
- Account record sections: [Rate & fee history (Terms)](#components--account-rate--fee-history) · [Value estimates](#components--account-value-estimates) · [Custodian](#components--account-custodian) · [Detail chips & menu conventions](#components--account-detail-chips--menu-conventions)
- Overlays: [File viewer](#components--file-viewer) · [Analyze file](#components--analyze-file)
- [Substitutions to flag](#substitutions-to-flag)
- [Reading further](#reading-further)

---

## Sources used to build this

- **GitHub:** `centralcmd/odyssey` — main branch. Browse it for deeper context:
  https://github.com/centralcmd/odyssey
- Specifically read: `Odyssey.Client/wwwroot/index.html`, `Odyssey.Client/Layout/*`, `Odyssey.Client/Pages/Auth/Login.razor`, `Odyssey.Client/Pages/Auth/Register.razor`, `Odyssey.Client/Pages/Preferences.razor`, `Odyssey.Client/Theme/*`, `Odyssey.Finance.Dtos/*`, the repo `README.md`, `CLAUDE.md`.
- Iconography from MudBlazor's bundled set (`Icons.Material.Filled.*`, `Icons.Custom.Brands.GitHub`), which under the hood uses Google's [Material Icons](https://fonts.google.com/icons?icon.set=Material+Icons) font — the same one declared in `wwwroot/index.html`.

If you have access to the repo, read further to deepen this system: the `Odyssey.Finance` services and `Odyssey.Finance.Dtos` enumerate the full domain model (account types, transaction statuses, budget categories, file analysis jobs, contacts), and the `.razor` pages under `Odyssey.Client/Pages/Finance/` are the ground truth for layouts.

---

## Product context, in one breath

Odyssey lets a person:

1. **Add bank accounts** — credit card, debit, savings, loan, investment — each in any currency.
2. **Log transactions** against an account, tied to a **contact** (merchant, person, org…) and a **transaction tag**.
3. **Build budgets** with income and expense items, then **report** actual vs. planned by period.
4. **Upload receipts and statements**; the backend runs file-analysis jobs that propose candidate transactions for the user to review/approve/flag.

Tone is **utilitarian, calm, numerate** — not playful, not aspirational. The user is doing financial admin and wants the app to get out of the way.

---

## Brand idea

Odyssey is a **journey** product, not a goal-tracker. The visual system reflects that with two reference points:

1. **Maritime navigation.** The logomark is a compass with a north-star needle. The product chrome treats money like cargo on a long voyage — tracked, accounted for, reviewed at port. Backgrounds are deep navy, like sea at night.
2. **The old finance terminal.** Trading desks and bank back-offices ran on CRT phosphor screens for decades. Odyssey nods to that lineage with a soft **phosphor teal** accent (`--tide-400`) and heavy use of monospaced numbers — *without* the costume of pixel fonts, scanlines, or green-screen kitsch. The lineage is in the **palette and typography**, not in skeuomorphic effects.

The two ideas align: a navigator's chart was the original financial dashboard, and phosphor displays were the first interactive ones. Odyssey sits in the line between them — calm, instrument-like, soft enough to live in for hours.

---

## Index — what lives where

| Path | What it is |
|---|---|
| `colors_and_type.css` | All design tokens as CSS custom properties (and the single inlined Material Icons `@font-face`). Names mirror MudBlazor's `--mud-palette-*` semantics so they wire straight into `MudTheme.PaletteDark` / `PaletteLight`. **Also ships a framework-neutral `--color-*` alias layer** (`--color-surface`, `--color-text`, `--color-primary`, `--color-danger`, …) mapping 1:1 onto the Mud tokens, so a consumer who isn't on MudBlazor can theme against intent names instead of Mud vocabulary — both resolve to the same per-theme value. `@import`s `components.css`. |
| `components.css` | Portable, token-driven styles for the typed components, prefixed `.odc-*` so they never collide with the reference kit. `@imported` by `colors_and_type.css` so they ship with the tokens. |
| `components/` | Typed, **consumable** components — each a `.jsx` + `.d.ts`. **Atoms:** `Button`, `IconButton`, `FieldShell`, `Field`, `SearchField`, `AmountField`, `MoneyField`, `CurrencySelect`, `NoteField`, `NumberField`, `TextInputField`, `ErrorSummary`, `FormRow`, `Select`, `Chip`, `Badge`, `Card` (+ `CardHeader` / `CardBody` composition slots), `StatTile`, `InfoTile`, `Alert`, `EmptyState`, `Avatar`, `MIcon`, `SeverityIcon`. **Brand:** `BrandMark` (the compass-rose logomark as inline SVG). **Scaffolds:** `PageHeader` (title + sub + chips, composed action cluster, toggleable Signal / Overview / Search / Reference regions — every screen mounts it first), `SettingRow` (icon + label + description | one control — the Preferences card row), `SettingField` (label notched into the field's outline, control inside, description + last-changed stamp on one helper line — the System settings grid block), `AddRow` (the dashed list-closing create affordance). **Navigation:** `Drawer` (+ `NavItem`). **Overlays:** `Modal`, `Tabs`, `Tooltip`, `Menu`. **Disclosure:** `Collapsible` (header row + count pill + optional leading `icon` and a right-aligned `action` slot — the single component behind the Files / Transactions / Terms record sections, the Budgets item list, and the Users role-permissions reveal; the reference kit consumes it through the `Components.jsx` bridge, no second copy). **Form controls:** `Switch`, `Checkbox`, `RadioGroup`, `SegmentedControl`, `Combobox`, `MultiSelect`, `TagMultiSelect` (the multi-tag picker for transactions), `DatePicker` (+ `DateField`, its labelled form-field form), `FileUpload`, and the typed registry pickers `AccountTypeSelect`, `ContactTypeSelect` / `ContactTypeMultiSelect`, `AccountFileTypeSelect` / `-MultiSelect`, `TransactionFileTypeSelect` / `-MultiSelect` — the file-type pickers delegate to the shared `RegistrySelect` / `RegistryMultiSelect` engines (registry in, themed control out). **Account custodian:** `CustodianChip` (the read-only "held at" chip on the account card) + `CustodianSelect` (the optional custodian picker on the create dialog + inline edit grid, a reuse/extension of `Combobox`). `AccountTypeChip` renders the account type as a chip (the sibling of `CustodianChip`) in the detail metadata grid; `AccountStatusChip` does the same for the account status (a tone-colored dot + label + muted date, e.g. "Open · since Mar 14, 2021"). **Data:** `Table`, `RecordTable` (+ its atoms `SortHeader`, `ActionMenu`, `MetaTile`), `TxnTable` (the transactions ledger), `FilesTable` (the attachments table), `TagChips` (the read display of a transaction's tag set). **Feedback:** `Skeleton` (+ `SkeletonRow`), `Toast` (+ `ToastStack`), `Spinner`, `ProgressBar`, `ProblemAlert` (the fix-it block of the problem/signal pattern). **Charts:** `Sparkline` (axis-less trend strip), `LineChart` (the axis'd trend card), `Donut` (+ `DonutLegend`). **Indicators:** `Delta` (one component for variance · directional · signed change). A consuming project reads them off `window.OdysseyDesignSystem_d5aa51` after loading the compiled `_ds_bundle.js`. The reference kit in `ui_kits/web/` consumes the same components through thin prop-name bridges in `Components.jsx` — there is no second implementation. `PageHeader` specimens: `components/pageheader.html` (live) and `preview/20-components-page-header.html`. |
| `assets/` | Brand marks (logomark, wordmark) and the rasterized Odyssey favicons (16 / 32 / 192 / 512). |
| `preview/` | Bite-sized HTML cards used by the Design System tab — type specimens, color swatches, component states. Edit these to iterate on tokens. |
| `ui_kits/web/` | Hi-fi recreations of Odyssey's product screens (dashboard, accounts list, transactions, budgets, receipt review) as React + JSX components. Open `ui_kits/web/index.html` for a click-thru. |
| `SKILL.md` | Cross-compatible Agent Skill manifest — drop this folder into Claude Code and invoke as `odyssey-design`. |

---

## Quick reference

A scannable map of the system. Every entry below is detailed in prose further down — this is the *what* and *where*; follow a link or specimen for the *why*.

### Cheat sheet — the rules that never bend

- **Stack:** .NET 10 / Blazor WebAssembly / **MudBlazor v8** / **Roboto** + **Roboto Mono** + **Material Icons** (all from Google Fonts).
- **Dark is primary**, light is a first-class alternate; every token has both values.
- **Tide** (phosphor teal) is the only color the brand owns — logomark, primary buttons, focus, links, active nav. **Never** use tide/sea to encode money.
- **Money semantics:** income = mint, expense = coral, pending = amber — always paired with a sign/icon/label, never color alone. Negatives use `−` + expense color, **not** parentheses. Numbers are always tabular.
- **No** emoji · **no** gradients in chrome · **no** decorative illustration/photography · **no** hand-drawn SVG icons (Material Icons covers every concept). Hand-built SVG is sanctioned **only** for the chart primitives.
- **Spacing:** 4px base, `--space-1..16` (maps 1:1 to MudBlazor `pa-N`/`ma-N`). **Radius:** 4px controls · 8px cards · 12px modals · pill chips.
- **Buttons:** `Filled` primary · `Outlined` secondary · `Text` tertiary. **Create convention:** trigger *New X* → dialog *New X* → confirm *Create X* (upload is the exception).
- **Layout:** authed = left `Drawer` (240px) only, no app bar, `Large` (1280px) container; auth = centered 420px card.
- **Focus is always visible** (2px `--focus-ring`, theme-safe ≥3:1); targets ≥24px (aim ≥40px primary); honor `prefers-reduced-motion`.
- **Loading is a first-class state** — render the shape and `Skeleton`-shimmer, never a blank panel or centered spinner.

### Component catalog

Consumable, typed components in `/components` (`.jsx` + `.d.ts`), exported on `window.OdysseyDesignSystem_d5aa51`, styled `.odc-*`. Purpose first, specimen card / file second.

**Atoms**

| Component | Purpose | Specimen |
|---|---|---|
| `Button` · `IconButton` | Filled/Outlined/Text CTA · icon-only action (needs `ariaLabel`) | `preview/16` |
| `Field` · `SearchField` | Labelled text input (`multiline` for long values) · the canonical search/filter input | `preview/17` · `components/searchfield.html` |
| `AmountField` | Money / numeric input with a currency-or-unit adornment (`prefix`/`suffix`), `md` + `lg` sizes — now for non-money numerics (rates, percentages, units) | `components/amountfield.
| `MoneyField` | The canonical **money editor** — amount plus its ISO currency code as one control; the code sits right, either a searchable picker or locked to static text (`currencyEditable={false}`). Optional leading `sign` + `tone` for signed amounts | `components/moneyfield.html` |
| `CurrencySelect` | Currency-**only** picker — the same ISO list, search box and keyboard behaviour as `MoneyField`'s segment, in standard Select chrome (account currency, base / reporting currency) | `components/currencyselect.html` |html` |
| `NoteField` | Multi-line note / description input with a live `len/max` character counter | `components/notefield.html` |
| `NumberField` | Labelled numeric input (native `type=number`) emitting `number \| null` — counts, years, figures; `unit` pins a static `%` / `MB` / `days` inside the input's trailing edge | `components/numberfield.html` |
| `TextInputField` | Labelled single-line **text** input — `NumberField`'s shape for strings. Use when the control must be labelled or described by elements it doesn't own (a `SettingRow` title, a table header, an inline edit); `Field` for ordinary form entry, `SearchField` for filter boxes | `components/textinputfield.html` |
| `CapacityField` | Capacity-limit control — a right-aligned `NumberField` + a "No limit" `Switch`; a finite number **or** explicitly unbounded (toggling "No limit" retains the number). The count-cap control on the System settings import/export groups | `components/capacityfield.html` |
| `CoordinateField` | Paired latitude / longitude entry — two `NumberField`s in a `FormRow`, each range-enforced (lat −90…90, lng −180…180) with an inline out-of-range error; value is a `{lat,lng}` pair | `components/forms.html` |
| `StepperField` | Compact integer + trailing auto-pluralizing unit ("every 2 weeks", "after 10 occurrences") — the count sibling of `AmountField`; consolidates the calendar recurrence interval / occurrence-count controls | `components/calendar-module.html` |
| `DateField` | Labelled date field — the `DatePicker` calendar wrapped in `FieldShell`, so dates read like every other labelled control | `components/upload.html` |
| `FieldShell` | The labelled-field wrapper (label + required/optional marker + helper/error line) shared by every control — wrap a `Combobox`/`MultiSelect`/segmented control/locked display in it | `components/fieldshell.html` |
| `FormRow` | Equal-width column grid for paired form fields (the component form of `.aam-row2`) | `components/formrow.html` |
| `Select` | Single-select dropdown (optional per-option `icon`/`iconColor`) | `preview/17` |
| `Chip` · `Badge` | Status/label pill · count badge | `preview/18` |
| `Card` · `CardHeader` · `CardBody` | Outlined (forms) or elevated (tiles) surface · titled header row · padded body (composition slots) | `components/card.html` |
| `StatTile` · `InfoTile` | Headline figure tile · labelled fact tile (icon + label + value + foot) | `preview/15` |
| `BreakdownTile` | Labelled icon·label·count distribution tile (By type / By status / By currency) | `components/breakdown.html` |
| `Alert` · `EmptyState` | Inline severity message · one-sentence absence + CTA | `preview/22` |
| `Avatar` · `MIcon` · `SeverityIcon` | Icon/initials tile · Material Icons glyph · info/warning/error glyph | — |
| `BrandMark` | Compass-rose logomark as inline SVG | `preview/01` |

**Scaffolds & navigation**

| Component | Purpose | Specimen |
|---|---|---|
| `PageHeader` | Title + sub + chips + actions, toggleable Signal/Overview/Search/Reference regions — every screen mounts it first | `preview/20` |
| `SettingRow` · `AddRow` | Preferences/settings row (label + desc \| control; `descId` to associate the hint, `footer` for the full-width tinted well below the row — where every content-width control goes, since the control column is `flex:none` and never wraps; `warning` for a non-blocking amber advisory band, `dirty` for the unsaved dot, which sits with the TITLE so it survives a footer control) · dashed list-closing "create" affordance | `preview/19` |
| `ErrorSummary` | Compact "n problems · Review" button placed before a **disabled** primary action on a page long enough that the blocking field is off-screen; pressing it focuses the first blocking control. Pairs with `Button`'s count `badge` | `components/settingrow.html` |
| `SettingField` | One setting as a **notched-outline field block** — the MudBlazor `Variant.Outlined` shape: label on the outline (a real `fieldset`/`legend`, so the browser cuts the notch), control inside, and one always-visible helper line carrying the description + the "last changed" stamp. The half-width alternative to `SettingRow`: a section card holds an `.odc-sfield-grid` of related settings instead of one card per setting. `wide` spans both columns; switches and actions use the `.odc-sfield-tile` shape | `components/settingfield.html` |
| `SecretSettingField` | `SettingField`'s shape for a value the API stores but **never returns** — an encrypted credential in the settings store. Renders the store's three read results as three different things to tell an administrator: a **fixed-length dot mask** for `found` (fixed, because the real length is itself a disclosure), an **inline entry input** for `not-set` (the one state with nothing to protect and something to do), and a coral **"Cannot be decrypted"** for `unreadable` — which never reads as merely unset, since an absent row is a healthy configuration and an undecryptable one is a live fault with a feature failing closed behind it. Replacing a stored value takes an explicit **Replace** first; entry is a password input with a reveal toggle and an as-you-type printable-ASCII check. `kind="derivation"` marks a key that cannot be re-issued | `components/secretsettingfield.html` |
| `SecretClearOnSaveDialog` | The gate in front of a page **Save** that clears a stored secret as a *side effect* of changing something else — a new SMTP host, or STARTTLS switched off. Two copy variants, one per trigger; because it gates a whole-page batch save rather than one field, the copy states that the change and the clear commit in one transaction, that Confirm submits every pending edit, and that Cancel discards none of them | `components/secretclearonsavedialog.html` |
| `Drawer` (+ `NavItem`) | The single left-chrome surface — brand lockup, nav, footer group | `preview/19` |
**Overlays & disclosure**

| Component | Purpose | Specimen |
|---|---|---|
| `Modal` | The one dialog shell — scrim, tinted head + lead icon, scrollable body, footer, focus trap, Esc/click-out | `preview/37` |
| `Tabs` · `Tooltip` · `Menu` | Tab strip · hover tip · `more_vert` overflow dropdown (a disabled item's `note` says why) | `components/data.html` · `components/file-analysis-runtime.html` |
| `Collapsible` | Header row + count pill + optional lead icon + action slot — every record disclosure section | `preview/28` |
| `RecordCard` · `InfoTileGrid` · `SectionDivider` | **The expandable record card** every record list is built from — dense identity header + a body whose order is fixed by the component (alert → details → content → sections) · the auto-fitting `InfoTile` grid the `details` slot is made of (the record's full field set; `dense` for many-short-fact record types) · the uppercase-label + rule + mono-meta divider that introduces each band/section | `components/recordcard.html` · `components/record-card-rules.html` |

**Form controls**

| Component | Purpose | Specimen |
|---|---|---|
| `Switch` · `Checkbox` · `RadioGroup` | On/off · multi-select (+ indeterminate) · single choice — native inputs under styled chrome | `components/controls.html` |
| `SegmentedControl` | 2–3 inline options (e.g. party-kind selector) | `components/controls.html` |
| `Combobox` | Searchable single-select, optional inline create (`clearable`, `loading`, per-option icon) | `components/controls.html` |
| `MultiSelect` | Checkbox-list filter with count badge — every ledger header (any-of match) | `components/controls.html` |
| `TagMultiSelect` · `TagChips` | Multi-tag picker for forms · read-only tag-set display (caps + `+N`) | `components/tags.html` |
| `MatchIndicator` | Per-cell AI-match annotation — source + confidence **as text** (`Suggested by AI` / `Created here` / `You chose` / `No match`), plus the sub-threshold **Use ‹name›** / dismiss action and the No-match **Create ‹name›** action (Analyze dialog) | `components/matchindicator.html` |
| `DatePicker` · `FileUpload` | Bare calendar popover (ISO value, keyboard grid) · drag-and-drop upload (rename/retype/remove rows) | `components/upload.html` · `components/uploadcap.html` |
| Consent-gate disclosure states | The analyze-file gate once its four processor-disclosure values are served rather than compiled — skeleton / resolved / degraded, with the affirmation disabled until it resolves | `components/consentgate.html` |
| `DateRangePicker` | Inline filter-bar range pill — two `DatePicker`s joined by a dash (icon + caption, ordered `{from,to}`, clear) | `components/daterangepicker.html` |
| `DateField` | The labelled form-field form of `DatePicker` (`DatePicker` in `FieldShell`) | `components/upload.html` |

**Typed registry pickers** — the single-selects all delegate to one shared engine, **`TypeSelect`** (`components/typeselect.html`): the base Select's themed trigger + popover, each row a colored category glyph + label with the **selected check pinned far right**, and optional `groups` for sectioned lists (Assets / Liabilities). Don't use `TypeSelect` directly — reach for the domain wrapper below; each feeds its canonical registry in (value = enum key, pass `types` to subset). The `-MultiSelect` siblings remain thin wrappers over `MultiSelect`.

| Component(s) | Registry constant | Specimen |
|---|---|---|
| `TypeSelect` | _(shared engine — wrappers feed it)_ | `components/typeselect.html` |
| `AccountTypeSelect` | `ACCOUNT_TYPES` (+ `ACCOUNT_TYPE_GROUPS`) | `preview/25` |
| `ContactTypeSelect` / `-MultiSelect` | `CONTACT_TYPES` | `preview/25`, `preview/33` |
| `AccountFileTypeSelect` / `-MultiSelect` | `ACCOUNT_FILE_TYPES` | `preview/27` |
| `TransactionFileTypeSelect` / `-MultiSelect` | `TRANSACTION_FILE_TYPES` | `preview/27` |
| `TaxStatementFileTypeSelect` / `-MultiSelect` | `TAX_STATEMENT_FILE_TYPES` | `preview/27` |
| `ContractTypeSelect` | `CONTRACT_TYPES` | `components/typeselect.html` |
| `BudgetCategoryTypeSelect` | `BUDGET_CATEGORY_TYPES` | `components/typeselect.html` |
| `InsurancePolicyTypeSelect` | `INSURANCE_POLICY_TYPES` | `preview/35` |
| `PolicyFileTypeSelect` / `-MultiSelect` | `POLICY_FILE_TYPES` | `preview/35` |
| `BillingIntervalSelect` / `-MultiSelect` | `BILLING_INTERVALS` | `components/subscription-pickers.html` |

**Data tables**

| Component | Purpose | Specimen |
|---|---|---|
| `Table` (+ `SkeletonRow`) | Read-only sortable data table (controlled sort) | `components/data.html` |
| `SortSelect` (+ `SortHelpers`) | The filter-bar **"Sort by"** control — curated field select + typed direction toggle bound to one `{key,dir}`; `SortHelpers` owns default directions, typed labels, and the stable null-last/id-tiebreak `sortRows` for hand-rolled lists | `components/sortselect.html` |
| `RecordTable` (+ `SortHeader` · `ActionMenu` · `MetaTile`) | The admin/ledger table — sort (uncontrolled or `sort`/`onSortChange`-controlled) + expand-to-detail + inline edit + row menu | `components/record.html` |
| `TxnTable` | THE transactions ledger (Transactions · Accounts · Budgets · Dashboard); `hideAccount` to drop a column | `components/txntable.html` |
| `FilesTable` | THE files surface — a `RecordTable` preset (Accounts · Transactions · Files) | `components/filestable.html` |
| `Pager` | The shared list pager for **server-paged** pages — Prev/Next + the canonical `Showing X–Y of N` (`0 results` when empty); `aria-disabled` no-op at bounds, focus never lost | `preview/30` |

**Account-record pieces**

| Component | Purpose | Specimen |
|---|---|---|
| `AccountTypeChip` · `AccountStatusChip` | Type chip (glyph + Asset/Liability) · status chip (dot + label + date) | `components/custodian.html` |
| `CustodianChip` · `CustodianSelect` | "Held at" link display · optional custodian picker (extends `Combobox`) | `components/custodian.html` |
| `ContactChip` | Read display of a linked/tagged contact (type glyph + name; archived + Unavailable states). How tagged People — Contacts of type Person — Journal links, and merchants read | `components/contactchip.html` |
| `CoverageStatusChip` | Derived insurance coverage status (sibling of `AccountStatusChip`) | `templates/insurance` |
| `BillingIntervalChip` | A subscription's cadence + DERIVED per-cycle anchor ("Monthly · day 15") | `components/subscription-pickers.html` |
| `SubscriptionStatusChip` | A subscription's Paused / Ended (derived) / Archived states as chips (registry `SUBSCRIPTION_STATES`) | `components/subscription-pickers.html` |
| `AccountSmartTagsSection` | Per-account saved-filter watchlist disclosure | `components/accountsmarttags.html` |

**Journal module** — the composites the Journal + Tasks pages add.

| Component | Purpose | Specimen |
|---|---|---|
| `TodoStatusChip` | A to-do task's kanban status (Backlog · Doing · Done · Archived) as a chip; meaning carried in text (registry `TODO_STATUSES`) | `components/journal-module.html` |
| `JournalPhotoGallery` | Responsive, keyboard-focusable lazy thumbnail grid over an entry's photos; striped placeholder when no `src` | `components/journal-module.html` |
| `TaskBoard` | Three-column kanban (Backlog/Doing/Done) with drag-and-drop **and** a keyboard move-button path; moves announced via a live region | `components/journal-module.html` |

**Calendar module** — the net-new atoms the Calendar page adds.

| Component | Purpose | Specimen |
|---|---|---|
| `CalendarGrid` | Month grid — colour-coded event chips (title always shown), multi-day all-day spanning strips, per-cell “+N more” popover, **drag-a-chip-to-reschedule** (`onEventDrop`), roving-tabindex keyboard grid + full ARIA | `components/calendar-module.html` |
| `TimeField` | Labelled 24-hour `HH:mm` time-of-day entry (loose typed parse + step suggestion list) — the timed sibling of `DateField`. Opens on click / type / ArrowDown (never bare focus); full keyboard nav (↑/↓ move the highlight, Home/End, Enter selects, Space opens/selects without wiping the value, Esc closes) driven by a native `keydown` listener so it works inside body-portaled Modals | `components/calendar-module.html` |
| `ColorSwatchSelect` | Single-select grid over the curated, contrast-vetted calendar palette (registry export `CALENDAR_SWATCHES`, lookup `swatchFor`) — **not** a free hex picker | `components/calendar-module.html` |
| `RevealPanel` | A segmented toggle that reveals a **connected** panel below it — the toggle becomes the header of one bordered surface, the controlled fields attach beneath a divider (the recurrence “Does not repeat / Repeats” toggle + its rule fields). General-purpose, not calendar-specific | `components/calendar-module.html` |

**Feedback, change & charts**

| Component | Purpose | Specimen |
|---|---|---|
| `Skeleton` · `SkeletonRow` | Loading placeholder (shimmer, static under reduced-motion) | — |
| `Toast` · `ToastStack` | Terse snackbar (bottom-right positioner) | — |
| `Delta` | The one change indicator — `variance` / `directional` / `signed` modes | `components/delta.html` |
| `ProblemAlert` | Severity-tinted fix-it block with navigate-to-fix CTA | `components/problemalert.html` |
| `Sparkline` · `LineChart` | Axis-less trend strip · axis'd chart with gridlines + delta | `preview/24` |
| `Donut` · `DonutLegend` | Allocation ring (watermark hole) + slice ledger · the ledger standalone | `preview/26` |

### Token map — the ones you reach for most

Tokens live in `colors_and_type.css` (268 total). MudBlazor `--mud-palette-*` names are canonical; a `--color-*` alias layer mirrors them for non-Mud consumers.

| Intent | Token | Note |
|---|---|---|
| Page / surface / divider | `--mud-palette-background` · `--surface` · `--mud-palette-divider` | per-theme |
| Primary text / secondary | `--mud-palette-text-primary` · `--text-secondary` | secondary AA-safe |
| Brand fill / brand text | `--tide-400` (dark) / `--tide-600` (light) · `--brand-text` (`--tide-ink` on light) | tide-as-text steps to `--brand-text` for AA |
| Secondary accent | `--sea-400` | informational only |
| Money | `--finance-income` (mint) · `--finance-expense` (coral) · `--finance-pending` (amber) | never tide/sea |
| Categorical (tags/charts) | `--violet-500` · `--chart-1…6` · `--chart-grid` · `--chart-axis` | charts step darker in light |
| Spacing / radius | `--space-1..16` (4px base) · `--radius-md` (8px) · `--radius-pill` | `pa-N`/`ma-N` map 1:1 |
| Density | `--row-h` 48 / `--row-h-dense` 36 · `--control-h` 40 / `--control-h-dense` 32 | `dense` prop wires these |
| Type | `--font-sans` (Roboto) · `--font-mono` (Roboto Mono) · `--font-icons` | numbers tabular |

### Page & template map

Every product screen has a reference build in `ui_kits/web/` and a copyable starting folder in `templates/<slug>/`. Open `ui_kits/web/index.html` for the click-thru.

> **Copying a template: bring `templates/kit-app.js` with it.** Each `templates/<slug>/<Screen>.dc.html` is a thin mount — the page itself is the kit's React build, pulled in by the **shared loader one level up** (`<script src="../kit-app.js">`), which loads the token + kit stylesheets, `_ds_bundle.js`, the seed-data files and the kit's JSX in dependency order. A template folder is therefore **not** self-contained: copy `templates/<slug>/` **and** `templates/kit-app.js`, keeping the same relative position (`kit-app.js` as the folder's sibling), plus the `colors_and_type.css` / `components.css` / `ui_kits/web/` / `_ds_bundle.js` paths it resolves from the project root. If your target tree differs, the only line to change is `ROOT` at the top of `kit-app.js`. Every template loads its page this way — do not hand-roll a per-template loader.

| Screen | Reference build | Template |
|---|---|---|
| Dashboard | `Dashboard.jsx` | `templates/dashboard` |
| Accounts | `Accounts.jsx` (+ `AccountTerms`/`AccountEstimates`/`AccountTwoFactor`) | `templates/accounts` |
| Transactions | `Transactions.jsx` | `templates/transactions` |
| Budgets | `Budgets.jsx` | `templates/budgets` |
| Tax Statements | `TaxStatements.jsx` | `templates/tax-statements` |
| Insurance | `Insurance.jsx` | `templates/insurance` |
| Contracts | `Contracts.jsx` | `templates/contracts` |
| Subscriptions | `Subscriptions.jsx` (Contracts-style record cards) | `templates/subscriptions` |
| Journal | `Journal.jsx` (record cards + `JournalPhotoGallery`) | `templates/journal` |
| Tasks | `Tasks.jsx` (`TaskBoard` kanban + list view) | `templates/tasks` |
| Calendar | `Calendar.jsx` (`CalendarGrid` month + week/day/agenda + header calendar filter) | `templates/calendar` |
| Files | `Files.jsx` | `templates/files` |
| Transaction Tags · Contacts · Currencies · Exchange rates | `TransactionTags.jsx` · `Contacts.jsx` · `Currencies.jsx` · `ExchangeRates.jsx` | `templates/transaction-tags` · `contacts` · `currencies` · `exchange-rates` |
| Users · Roles · Settings | `Users.jsx` · `Roles.jsx` · `SystemSettings.jsx` | `templates/users` · `roles` · `settings` |
| Legal documents | `LegalDocuments.jsx` | `templates/legal-documents` |
| Analysis log | `FileAnalysisLog.jsx` | `templates/analysis-log` |
| User Account · Preferences | `Account.jsx` · `Preferences.jsx` | `templates/user-account` · `preferences` · `account-2fa` |
| Login · Confirm email | `Login.jsx` · `ConfirmEmail.jsx` | `templates/login` · `confirm-email` |
| Forgot · Reset password | `ForgotPassword.jsx` · `ResetPassword.jsx` | `templates/forgot-password` · `reset-password` |
| Register · Accept terms | `Login.jsx` (`Register`) · `AcceptTerms.jsx` | `templates/register` · `accept-terms` |

---

## Content fundamentals

**Voice.** Plain English, second person ("you"), short sentences. The product never tells you what to do with your money — it tells you what's *in* it. No motivational copy ("Let's crush your goals!"), no emoji, no exclamation points outside error messages.

**Casing.** Title Case for navigation, page titles, primary buttons, and column headers. Sentence case for body copy, helper text, and toast messages.

**Numbers.** Always tabular figures (`font-variant-numeric: tabular-nums`). Currencies use the ISO code prefix or symbol the user picked per-account; the codebase stores `CurrencyCode` as ISO-4217 strings (`"USD"` default), so format as e.g. `USD 1,234.56` or `$1,234.56` and **never** mix styles in the same view. Negative amounts use a minus sign and the expense color — not parentheses. **Glyph spacing:** the sign and symbol cluster tight, then a single space precedes the digits — `−$ 1,234.56`, `+$ 3,250.00` (this is what `data.js` `money()` / `signedMoney()` emit). The leading space separates the sign-symbol pair from the tabular digits so columns of mixed-sign amounts align cleanly; keep it consistent — don't render `−$104.20` and `−$ 104.20` in the same view.

**Tone, by surface.**

- *Navigation / menus:* one-word labels where possible — `Dashboard`, `Accounts`, `Budgets`, `Transactions`, `Tags`, `Contacts`, `Currencies`, `Preferences`. Exactly the labels the live `NavMenu.razor` uses.
- *Buttons:* verb-first. `Save`, `Approve`, `Flag`, `Import statement`, `Attach receipt`. Avoid `Submit`, `OK`, `Done` — too generic.
- *Create / new convention (a rule of the system, not a preference):* creating an entity reads **New <thing>** in all three positions where the user meets it — the **trigger** (page-header primary, action-menu item, `AddRow`, empty-state CTA), the **dialog title**, and then **Create <thing>** on the dialog's primary button, because that button is the one place where the create actually happens. So: `New account` → *New account* → **Create account**; `New transaction` → *New transaction* → **Create transaction**; `New party` → *New party* → **Create party**. Never `Add <thing>` for entity creation, and never a bare `Save` / `Submit` / `OK` on a create dialog.

  **Exceptions are allowed, with a reason.** The rule holds unless the real-world verb for the action is something other than *create*, in which case that verb wins and the same word is used in all three positions. The established ones:

  | Action | Wording | Why |
  |---|---|---|
  | Files | `Upload file` → *Upload files* → **Upload** | The user uploads a document that already exists; nothing is authored. |
  | Attaching an existing record to another (a contact to a photo, a tag to an account, an insurer to a policy) | `Add <thing>` / `Tag a person` | It links a record that already exists — the multi-select's inline control, not a create flow. |
  | Enabling a capability | `Add two-factor authentication`, `Set up 2FA` | Nothing is created; a feature is turned on. |
  | Importing | `Import statement`, `Import contacts` | The data comes from elsewhere. |

  Adding a new exception is a design decision: it needs a stated reason (put it in this table), and once stated it applies to the trigger, the title and the button alike — never `New file` on the trigger and `Upload` on the button.
- *Empty states:* one sentence stating the absence, one CTA. "No transactions yet. **Import a statement** to get started."
- *Errors:* the live login uses *"Unable to sign in. Please check your username/email and password."* — pattern is **"Unable to *do thing*. *Recovery action*."**, ending with a period. Mirror this everywhere.
- *Success:* live preferences page just saves silently or shows a MudBlazor snackbar — keep success terse. "Saved." "Approved 3 transactions." No celebrations.

**Don'ts.** No emoji. No icon-as-text decoration in body copy. No "we" — Odyssey is a tool, not a team. No marketing hype: avoid *seamless, effortless, beautiful, magical, AI-powered*. The word "AI" is reserved for the receipt-analysis feature, and only when describing it; everywhere else, just say what happened ("Matched 4 candidates from `statement.pdf`").

**Examples lifted from the codebase.**

- `"Sign in"` / `"Need an account? Register"` — `Login.razor`
- `"Create account"` / `"Already have an account? Login"` — `Register.razor`
- `"Preferences"` / `"Dark mode"` / `"Save"` — `Preferences.razor`
- `"Unable to load preferences: {ex.Message}"` — `DarkModePreferenceService.cs`
- `"Unable to sign in. Please check your username/email and password."` — `Login.razor`

---

## Visual foundations

**Mode.** Dark is default; light is a first-class alternate. Every token has both values. The user's choice persists server-side via `IUserPreferenceService` (a `UserPreferences` JSON payload — see `Odyssey.Client/Theme/UserPreferenceService.cs`), and `Odyssey.Client/Layout/OdysseyThemeProvider.razor` paints the last-known value before first render to avoid a flash.

**Color philosophy.**

- **Ink ramp** (`--ink-50` … `--ink-950`) — cool navy neutrals. The dark surfaces lean blue-black, evoking a deep-water horizon and the calm of an old terminal at rest.
- **Tide** (`--tide-500 #2DD4BF`, anchor; `--tide-400 #4FD7CB` on dark; `--tide-600 #14B8A6` on light) — the primary brand accent. A soft phosphor teal that reads as both **maritime** (the color of clear shallow water) and as **CRT terminal glow** — a callback to old finance dashboards, refit for a modern surface. Used for the logomark, primary buttons, focus rings, links, active-nav highlight. **Tide is the only color the brand owns.** **For tide-colored *text* on light surfaces, use `--brand-text` (→ `--tide-ink #0A7A6B`), not the fill colors** — `--tide-600`/`--tide-700` fall under the 4.5:1 AA threshold as text on white, so links and active-nav labels step to the dedicated `--tide-ink` (~5.3:1). On dark, `--brand-text` is the bright `--tide-400`. Filled buttons keep `--tide-400`/`--tide-600`.
- **Sea** (`--sea-400 #38BDF8`) — a clearly bluer secondary, used for informational chips and the rare neutral-cool accent. Distinct in hue from tide so the two never read as the same color.
- **Finance semantics** — `--finance-income` (mint), `--finance-expense` (coral), `--finance-pending` (amber). Never use brand tide/sea to encode income/expense; they are reserved for product chrome.
- **Categorical accents** — `--violet-500` for tags so they sit clearly outside the brand palette. Future chart palettes can extend from tide → sea → mint → violet → coral.

> **Icon font.** Material Icons ships as a **single base64-inlined `@font-face`** in `colors_and_type.css` (not a `url()` reference). This is deliberate: the Design-System-tab thumbnails and offline/sandboxed previews are captured by a DOM-to-image step that can embed an inline font but cannot fetch an external binary or follow a face across an `@import` — so the glyphs only survive the capture when the face lives in the directly-linked token sheet. An earlier build carried two faces (base64 + a `url()` woff2) for the same set; that redundancy has been removed.

**Typography.** A single family: **Roboto** (300/400/500/700), plus **Roboto Mono** for transaction IDs, amounts, dates, file analysis output — anywhere the user is reading a ledger. Roboto is the codebase's declared font (loaded in `wwwroot/index.html`) and MudBlazor's default; it ships everywhere, reads cleanly at every size, and stays out of the way. Roboto Mono is the natural companion — same designer, matched proportions — and carries the terminal/phosphor lineage of the system without forcing us into a retro pixel-font costume.

The scale matches MudBlazor's `Typo.h1`–`Typo.body2` so every `<MudText>` slots in without a custom Typo set. Headings use weight 300 ("Light") for h1/h2 to feel calm; weight 500 for buttons + h5/h6 to feel pressable. Numbers always tabular (`font-variant-numeric: tabular-nums`).

**Spacing.** 4px base unit. Use the `--space-1..16` scale exclusively. MudBlazor utility classes (`pa-2`, `ma-4`, `mt-1`) map 1-to-1 — `pa-2` = 8px. Cards have `pa-4` (16px) by default; dense lists use `pa-2`.

**Layout rules.**

- Authenticated screens use `MudLayout` with a single left `MudDrawer` (responsive, `ClipMode.Always`, elevation 1) holding the full chrome — brand lockup at the top, primary nav, and a footer group of Preferences / User Account / About. **There is no top `MudAppBar`.** The drawer is the only chrome surface, and a `MudMainContent` holds a `MudContainer MaxWidth="Large"`. Adjust `Layout/MainLayout.razor` to drop the `MudAppBar` and move its actions into the drawer footer.
- Auth screens (`/login`, `/register`) use `AuthLayout` — a centered `MudCard` 420px wide. No drawer, no app bar.
- Drawer is 240px wide on desktop, collapses to icon-only or overlay on mobile via `DrawerVariant.Responsive`. The brand lockup at the top of the drawer matches the auth-card lockup (compass + tide-glow caps wordmark), sized down to fit the 240px column (56px compass).
- Content max width is `Large` (1280px); never full-bleed except for the future Dashboard's hero strip.

**Backgrounds.** Solid colors only. **No gradients** in product chrome. The only exception is *protection gradients* — subtle bottom-fade on scroll containers when a sticky footer overlaps content, achieved with `background: linear-gradient(0deg, var(--mud-palette-background) 0%, transparent 100%)`. **No** decorative photography, hand-drawn illustrations, repeating patterns, or texture overlays. The product is a financial instrument; surfaces stay quiet.

**Cards.** `border-radius: 8px` (`--radius-md`); `background: var(--mud-palette-surface)`; `border: 1px solid var(--mud-palette-divider)`. Default elevation is `--mud-elevation-1` (essentially a 1px tinted inset + soft drop). Outlined cards (`MudCard Outlined="true"`, used in `Preferences.razor`) drop the drop-shadow entirely and rely on the border. Both styles exist; prefer outlined for forms, elevated for stat tiles.

**Buttons.** MudBlazor variants we use:
- `Variant.Filled` `Color.Primary` — primary CTA. On dark: background `--tide-400`, text `--ink-950`. On light: background `--tide-600`, text white. The dark-mode button has a soft phosphor glow against the navy background — keep it intentional, don't over-elevate.
- `Variant.Outlined` — secondary action.
- `Variant.Text` — tertiary / nav links (see `NavMenu.razor`).
- Density: default for forms, `Dense` for nav and table actions.
- Radius: 4px (MudBlazor default). Never pill-shaped except for chips.

**Borders.** 1px hairlines (`--mud-palette-divider`) for table rows and card outlines. 2px (`--border-strong`) only for focused inputs (`--mud-palette-primary` ring).

**Shadows.** Use MudBlazor's elevation scale (0/1/2/4/8/16 exposed as `--mud-elevation-N`). Dark-mode shadows are deep (40–70% black) and combined with a 1px inset highlight so cards don't disappear into the bg. Light-mode shadows are soft and shallow.

**Corner radii.**
- 4px — buttons, inputs, chips, MudNavLink (`Rounded="true"` already sets 4px).
- 8px — cards.
- 12px — modals, dialogs, large tile groups.
- pill — only for chips and avatars-as-status.

**Animation.** Quiet. MudBlazor's defaults are fine: 250ms cubic-bezier for drawer + dialog, no bouncy easing, no entrance choreography. Hover is **instant** (0ms transition on bg-color); only opacity/elevation tween (150ms). The `MudProgressCircular` indeterminate spinner is the only continuous motion.

**Hover states.** A 6% (dark) / 4% (light) bg overlay on rows and clickable surfaces (`--mud-palette-action-default-hover`). Primary buttons darken from `--tide-400` to `--tide-500` on dark, and from `--tide-600` to `--tide-700` on light. Links don't underline by default — only on hover. only on hover.

**Press states.** No size shrink, no transform. The hover overlay deepens to 12%, no further. We deliberately avoid the iOS-style press shrink because the app is keyboard-and-mouse first.

**Focus rings.** Always visible: 2px solid `--focus-ring` at 2px offset. Never `outline: none`. `--focus-ring` is the primary teal on dark and steps to `--tide-700` on light — tide-600 is only ~2.5:1 on white, under the 3:1 WCAG 1.4.11 floor for focus indicators. Inputs get an inset `--focus-ring` border on focus instead of a halo. The consumable `.odc-*` components implement this with `:focus-visible` in `components.css` (so it shows for keyboard/AT users without firing on mouse press) — `Button`, `Chip`, the icon button, `Tabs`, and the field/select controls all carry it.

**Transparency & blur.** Used sparingly:
- Drawer is solid (no glass effect). Avoids contrast issues over scrolling tables.
- Dialog scrim is `rgba(8, 12, 24, 0.6)` — no `backdrop-filter: blur()`.
- Disabled controls drop opacity to roughly 38% via `--mud-palette-text-disabled` / `--mud-palette-action-disabled`.

**Imagery vibe.** There is no decorative photography in the product. Receipts (user-uploaded) are shown at native fidelity, no warming/cooling, no grain. Account-source logos (when we add them) should be flat brand marks on a neutral surface — never on colored panels.

**Fixed elements.** Drawer pinned to left, full-height. Dialogs center-screen. Toasts (`MudSnackbar`) bottom-right. No floating action buttons. No top app bar.

---

## Accessibility

The product is a financial instrument used for long sessions on desktop. Accessibility is treated as load-bearing, not a coat of paint — the rules below are already enforced by the tokens and `.odc-*` components; this section states the targets explicitly so new work holds the line.

**Contrast targets.** We meet **WCAG 2.1 AA**: **4.5:1** for body and UI text, **3:1** for large text (≥24px, or ≥19px bold) and for the meaningful boundary of interactive components (borders, focus rings, control outlines). This is why tide *text* on light steps to `--brand-text` (`--tide-ink #0A7A6B`, ~5.3:1) instead of the `--tide-600`/`--tide-700` fills, which fall under 4.5:1 as text on white — and why the light-mode focus ring (`--focus-ring`) steps to `--tide-700`, and light-mode warning/`--status-closed`/`--chart-6` step to `--amber-600` (amber-500 is ~2.2:1 on white, under even the 3:1 graphics floor). Both modes carry compliant pairings; when you introduce a new foreground/background combination, verify it before shipping rather than assuming a token is safe everywhere. Disabled text at ~38% opacity is deliberately exempt (per WCAG) but must never be the only way to read a value.

**Never encode meaning with color alone.** Income/expense/pending always pair their semantic color with a sign, icon, or label — color is reinforcement, never the sole signal. Status reads from the chip's text, not just its tone. This is the same rule that keeps brand tide/sea off financial semantics.

**Keyboard.** Everything operable by mouse is operable by keyboard. The app is **keyboard-and-mouse first** — that's why press states never shrink or transform. Native inputs sit under the styled chrome of `Switch` / `Checkbox` / `RadioGroup` so tab order and form submission work for free; `Select`/`TypeSelect`/`Combobox`/`MultiSelect`/`Menu` implement ↑/↓ to move, Enter to pick, typeahead where the list is long, Esc to dismiss, and close on outside-click. No keyboard traps; modal focus is contained while open and returns to the trigger on close — popovers restore trigger focus the same way.

**Layered dismissal (Esc).** Esc closes exactly one layer, innermost first: a Menu/Select/DatePicker/Tooltip open inside a Modal captures Esc (`keydown` capture phase + `stopPropagation`) so the Modal stays open for the next press. Any new popover must follow this rule — never let one Esc collapse two layers.

**Focus is always visible.** 2px solid `--focus-ring` at 2px offset, never `outline: none` (theme-safe: primary on dark, `--tide-700` on light). Inputs take an inset `--focus-ring` border on focus instead of a halo. The `.odc-*` components use `:focus-visible` so the ring shows for keyboard/AT users without firing on mouse press — Button, Chip, the icon button, Tabs, and the field/select controls all carry it.

**Target sizes.** Minimum **24×24px** for any pointer target (WCAG 2.2 AA, 2.5.8), and we aim for **≥40px** on primary touch/click targets — default-density buttons, nav links, and row-action icon buttons all clear this. Dense table actions stay above 24px and keep adequate spacing; never pack hit targets tighter than the dense scale allows. Controls that must *look* smaller than 24px (the smart-tag remove ×, the toast close) keep a ≥24px invisible hit area via an absolutely-positioned pseudo — reuse that pattern, don't shrink the target.

**Screen readers & semantics.** Build on native HTML first; reach for ARIA only to fill gaps. Icon-only controls carry an `aria-label` (theme toggle, modal close, row menus); tablists use `role="tablist"`/`tab` + `aria-selected`; toggles use `role="switch"`/`aria-checked`; radio chip-groups use `role="radio"`/`aria-checked`. Live regions follow severity — error `Toast`s fire `role="alert"`, everything else `role="status"`. Every form control has a programmatically associated label; required/optional is marked in text (the `*` / "Optional" convention), not by color or placeholder alone.

**Motion.** Animation is already quiet (instant hover, 150ms opacity/elevation, no entrance choreography). The skeleton shimmer and the `MudProgressCircular` spinner are the only continuous motion, and the shimmer falls **static under `prefers-reduced-motion`**. Honor that query for any motion you add.

**Timing & transient content.** `Toast` auto-dismiss pauses while hovered or focused (WCAG 2.2.1), and action-bearing toasts stay ≥8s. `Tooltip` shows on hover *and* focus and is dismissable with Esc without moving focus (WCAG 1.4.13). Never put essential information only in a transient surface.

**Images & color independence.** No decorative photography or hand-drawn illustration to alt-text; user-uploaded receipts are shown at native fidelity and should carry a meaningful `alt` (the file name/type). The UI must remain usable in grayscale — test it.

> **Checklist for new work:** AA contrast on every new color pairing (focus rings through `--focus-ring`) · color never the only signal · operable + visible-focus by keyboard · Esc closes one layer at a time · labels on all controls and icon buttons · expand/collapse controls carry `aria-expanded` · targets ≥24px (pseudo hit-area pattern for smaller visuals) · transient UI is pausable & Esc-dismissable · `prefers-reduced-motion` honored.

---

## Components — data table, menu & form controls

The consumable layer (`/components`) now covers the load-bearing patterns the product is actually built from, not just atoms. All are token-driven, theme-aware, prefixed `.odc-` in `components.css`, and exported on the DS namespace. Specimens: `components/data.html` (Table · Menu), `components/controls.html` (Switch · Checkbox · RadioGroup · Combobox · MultiSelect), and `components/upload.html` (FileUpload).

**`Table`** — the data-table primitive behind every ledger screen (Transactions, Files, Users) and the shared `TxnTable`. Declarative: pass `columns` (each with an optional `cell` renderer) + `rows`. Sorting is **controlled** — give `sort` `{key,dir}` and an `onSort(key)` handler; the component renders the indicator, you own the sort. A column with `align:'end'` right-aligns and renders monospace tabular figures (amounts, dates). `dense` for nav-embedded tables; `onRowClick` for the expand-in-place record rows. Maps to `MudTable` + `MudTableSortLabel`. Use it for read-only lists and dashboards; reach for `RecordTable` when rows need to expand, edit, or carry a row menu.

**`RecordTable`** — the *admin/ledger* table: everything `Table` does **plus** expand-to-detail rows, an inline Edit panel, and a per-row overflow menu. It owns the whole sort / accordion-expand / edit / delete / "Saved"-flash state machine, so a page only declares its `columns` (each with a `cell(row, ctx)` renderer + `sortValue`), an optional `leading` avatar cell, the `actions(row, ctx)` menu items, and the `renderDetail` / `renderEdit` panels. `multiOpen` keeps several rows open; `keepDirOnColumnChange` + `tiebreak` tune sorting (append-only logs). **Edit surface, per page.** The inline `renderEdit` panel is optional: **Users** and **FilesTable** still edit in place, but **Transaction tags, Currencies, and Exchange rates** now route the row's **Edit** action to their create dialog reused in edit mode (`AddTagModal` / `AddCurrencyModal` / `RecordRateModal`) instead of an inline panel — omit `renderEdit`, point the Edit item at the dialog, and Save commits through `onSave(id, patch)`. (The record-card pages **Accounts, Budgets, Transactions, Subscriptions, Contracts, Insurance policies, Tax statements and Journal entries** do the same with their create dialog reused in edit mode — `AddAccountModal` / `AddBudgetModal` / `AddTransactionModal` / `AddSubscriptionModal` / `AddContractModal` / `AddInsurancePolicyModal` / `AddTaxStatementModal` / `AddJournalEntryModal`.) This is the single source of truth behind **Transaction tags, Contacts, Currencies, Exchange rates, and Users** — pages that each used to hand-roll ~120 lines of identical row/sort/expand machinery. Its three reused atoms are also exported standalone: **`SortHeader`** (a sortable `<th>`), **`ActionMenu`** (the `more_vert` row menu), and **`MetaTile`** (a labelled detail-grid well). Specimen: `components/record.html`. Styled with the kit's `.ua-tbl` / `.acct-detail` / `.meta-grid` / `.acct-menu` classes (from `ui_kits/web/kit.css` + `admin.css`), so it depends on those sheets in addition to the token sheet.

**`Menu`** — the `more_vert` row-actions / overflow dropdown used on every record row. Self-managing open state; closes on outside-click and Esc. Items are a flat list — `{divider:true}` for separators, `{header:'…'}` for group labels, `{danger:true}` for destructive actions. A `{disabled:true}` item can carry a **`note`** — one line saying *why* it is unavailable, rendered under the label and wired as the item's `aria-describedby`; such an item is marked `aria-disabled` rather than `disabled`, so it keeps its place in the roving-focus order and a keyboard or screen-reader user reaches the reason instead of a silently skipped item (meaning as text, never the dimmed state alone). Defaults to an icon-button trigger; pass your own `trigger` to anchor it to a Button. Maps to `MudMenu`.

**`TxnTable`** — **THE transactions ledger**, promoted from the kit: the sortable, expandable table behind the **Transactions page, the Accounts per-account section, the Budgets matched-transactions panel and the Dashboard recent list**. It owns the canonical column set (direction-tinted type avatar · Description · Contact · Account · Tags · Status · Amount · Date · row menu), the status→tone chip mapping (`statusTones`) and the signed income/expense amount encoding, so a transaction row reads identically on every surface. The **Tag column is multi** — a row carries a `tags` set (an array of label strings or `{id,label}`); the column shows up to two `tag` chips then a `+N` overflow, the legacy single `tagLabel` still honored as a one-element fallback. Rows are plain **denormalized objects** (`accountLabel` string, `tags` array, no store lookups) — the kit joins ids→names in a thin bridge. Sorting, accordion expansion (rows mid-edit never auto-collapse), the in-place `renderDetail` / `renderEdit` swap and the "Saved" flash all live inside; `hideAccount` drops the Account column inside a single account. **Pagination** is now a first-class list pattern — see **`Pager`** below; on a server-paged page the parent owns `page`/`pageSize`/`totalCount`, feeds `TxnTable` one page's `rows` (with `ServerSort`-style controlled sort raised to the parent) and renders the `Pager` beneath. The interim client-window behaviour (fetch a large first page, render the filtered list whole, no pager) still holds until a page migrates to the server contract. Specimen: `components/txntable.html`. Like `RecordTable`, it's styled with the kit's `.ua-tbl` classes, so it depends on `kit.css` + `admin.css`.

**`FilesTable`** — **THE files surface**, promoted from the kit: the attachments table shared by the **Accounts detail, the New / Edit transaction dialog and the flat Files page** — a **preset of `RecordTable`**, so file rows follow the same record-row lifecycle as every admin table (sortable headers, click-to-expand detail, inline Edit, Saved flash). Columns: kind avatar · Name · Type · Size · Uploaded (default sort, newest first) · actions. A row expands into a read-only MetaTile detail (File name · Document type · Size · Uploaded); with `onSave(id, patch)` the menu gains **Edit**, which swaps in the inline panel for a file's only mutable fields (name + document type — `kinds` feeds the type picker). `typeFor(file)` resolves each row's kind visuals (`{icon,color,soft}` — the `ACCOUNT_FILE_TYPES` registry shape) so a Statement reads the same here as in the upload picker; unknown kinds fall back to the slate document glyph. `actions(file)` supplies only the file-specific items (**Preview** — the document viewer — / Download / Analyze / Copy ID), slotted into the conventional menu between Edit and Delete; any modals those open are hosted **outside** the table. "Preview" opens the document, "View details" expands the record — never the same word for both. An optional per-row **`statusBadge`** (`{ text, tone?, icon?, ariaLabel? }`) renders an `OdsChip` next to the file name — e.g. a **"Review pending · N"** hint for a file with an open, resumable analysis review; meaning lives in the text and absent rows render exactly as before. Specimen: `components/filestable.html`. Styled by the kit's `.ua-tbl` / `.acct-detail` classes (`kit.css`).

**`AccountSmartTagsSection`** — **the per-account Smart Tags disclosure**, the third section in the expanded account record (after Files and Transactions). It pins a curated set of existing `TransactionTag`s to one account as a **saved filter**, then surfaces every transaction on that account carrying any of them — a persistent, per-account watchlist that saves re-filtering the Transactions page. Self-contained: it renders its own `.odc-collapsible` shell (bundle components can't import each other), a **tag-management bar** (removable `tag` chips + a dashed **Add tag** control whose checklist maps **check → add** / **uncheck → remove**, one call per toggle to mirror the idempotent `POST`/`DELETE …/smart-tags/{tagId}` endpoints), and the spec's five states — **NoSmartTags** (empty + first-tag picker), **Loading** (an indeterminate `.odc-progress` while the matches re-fetch), **NoTransactions** ("No matching transactions"), **HasTransactions**, and an **error/retry** panel. The header `count` pill is the **matching-transaction count** (same as the Transactions section), and the management bar shows the **net total** of the watched transactions (income positive / expense negative, in the income/expense color) — a per-account at-a-glance figure formatted by an injectable `formatAmount` (the kit passes the account's `signedMoney`). The ledger is **injected via `renderTable(transactions)`** — pass the shared `TxnTable hideAccount` — so the section never couples to the table's render contract. `canWrite={false}` keeps the chips + table for read-only viewers but drops every add/remove control; `maxTags` (default 20) blocks new checks at the cap. Data-prop driven (`tags` · `tagOptions` · `transactions` · `onAddTag` · `onRemoveTag`), nothing global. Maps to an `OdsCollapsible` + `OdsTxnTable` in the Blazor `AccountSmartTagsSection`. Specimen: `components/accountsmarttags.html`; styled `.odc-smarttags-*` plus the kit's `.ua-tbl` classes (`kit.css`).

> **The account row header counts every detail section.** The collapsed account row's subtitle (`.acct-counts`) carries an icon + number for each expandable section so the user sees what's inside before opening: **transactions** (`receipt_long`) and **files** (`attach_file`) always show; **estimates** (`monitor`), **terms** (`percent`), and **smart tags** (`sell`) show only when non-zero, to keep the row quiet. The smart-tag count is live — it tracks the watched set as tags are added/removed in the expanded `AccountSmartTagsSection`.

**`Switch`** — binary on/off (Preferences' Dark mode). **`Checkbox`** — multi-select boolean with an indeterminate ("select all") state, for the Analyze-file candidate rows and batch grids. **`RadioGroup`** — single choice from a small set (transaction direction Money in / Money out). All three are native inputs under styled chrome, so keyboard nav and form submission work for free. Map to `MudSwitch` / `MudCheckBox` / `MudRadioGroup`.

**`FileUpload`** — pass `maxMegabytes` and never write a size limit into `hint` as a literal: the cap is a runtime setting, so a typed number goes stale silently and the field ends up claiming a limit the server does not enforce. Where a surface has its own tighter product limit, `maxMegabytes` is `min(surfaceConstant, serverCap)` — a surface may tighten the global cap, never override a lowered one — and `FileUpload.overMaxError(...)` composes the matching rejection from the same number. The drag-and-drop upload field, now the single upload surface behind **every** kit modal (Upload file, New transaction, edit-transaction files, Insurance, Tax statements, Contract files) — the hand-rolled `AfmUpload` is gone. A click-or-drop dropzone over a ready-file list where each row can be renamed, retyped via an inline file-kind picker, and removed. Runs controlled (`files` + `onChange`) or uncontrolled (`defaultFiles`); each file is `{uid,name,kind,sizeBytes}` and `onChange` fires the full next array on every edit. `compact` gives the horizontal low-profile dropzone for tight modals; `showKinds={false}` drops the kind picker for a plain list; **`guessKind(name)`** overrides the extension→kind guess with a domain vocabulary (tax / insurance / contract); **`renderFileExtra(file, patch)`** renders a per-row editor beneath each file (the account upload's Valid-from/to · Issued metadata). Styled `.odc-upload-*`; specimen in `components/upload.html`.

**`Combobox`** — searchable single-select with optional inline create — the contact picker (the **tag** picker is now multi; see `TagMultiSelect`). Type to filter, ↑/↓ to move, Enter to pick; provide `onCreate` to offer a "Create …" row for an unmatched query. Maps to `MudAutocomplete`. **`MultiSelect`** — the checkbox-list filter behind every ledger header (account / status / tag / direction), with a count badge on the trigger and Clear / Done in the popover. The **tag** filter is now an *any-of* match — a transaction shows if it carries **any** selected tag. Maps to `MudSelect` with `MultiSelection`.

**`TagMultiSelect`** — the multi-tag picker for the transaction **forms** (the New / Edit transaction dialog), replacing the single tag `Select` now that a transaction carries a list of `TransactionTag`s. Its control box shows each selected tag as a removable `tag` chip; an "Add tag" affordance opens a searchable, checkable list — consistent with the header `MultiSelect`, but labelled and chip-displaying for data entry. `value` is an array of tag ids; `onChange(nextIds)` fires the full next set on every add/remove; `onCreate(text)` offers an inline "Create …" row (the affordance the single tag Combobox had) and adds the new tag to the selection. The popover is body-portaled so it escapes a modal/card/collapsible. **`TagChips`** is its read-only counterpart — renders a transaction's tag set inline (none → em-dash · one · many), with `max` to cap visible chips and roll the rest into a `+N` (the dense ledger column uses it; the detail tile shows them all). Specimen: `components/tags.html`.

> **Field markers — one standard.** Required vs. optional is marked exactly one way across the whole system: **required → a `*`** after the label (`.odc-field-req`); **optional → a muted, sentence-case "Optional"** hint after the label (`.odc-field-opt`, caption size, `text-secondary`). Both are built into the base `Field` / `Select` / `TagMultiSelect` via the `required` / `optional` props — never hand-roll a label marker. Within a single form, mark only the minority case (Odyssey forms are mostly-optional, so they mark **Optional**; a mostly-required form marks **\***) — don't mark both on every field. The kit's legacy `.atm-opt` is aliased to the same look, so hand-built modal labels read identically to DS fields; new work should prefer the `optional` prop over the class.

> **Long values go multi-line.** `Field` takes a **`multiline`** prop (with optional **`rows`**, default 3) that swaps its `<input>` for a vertically-resizable `<textarea>` (`.odc-input-multiline`), sharing the field's label / help / error chrome. Use it for free-text that can run long — descriptions, notes — so the value isn't trapped in a one-line input; keep single-line `Field` for short scalars (names, numbers, identifiers). In the inline record-edit grid, fields sit **one per column** (no full-width spanning) and the multi-line field simply occupies its single column.

**`SearchField`** — the canonical free-text **search / filter** input that leads every list page (Accounts, Transactions, Contacts, Files, Budgets, Exchange rates, Users, Settings, Preferences). A thin, intent-typed wrapper over `Field`, pre-set for one job: a leading `search` glyph, a clear (×) button once there's a value (`clearable` on by default), and `type="search"` for native semantics (the webkit cancel decoration is suppressed so the DS clear never doubles up). It sits on `flex:1` at the head of the filter bar, growing beside the type / status **`MultiSelect`** filters. Reach for it over a bare `Field` on every search box so the affordance reads identically everywhere; use `Field` for labelled data entry and `Combobox` for search-or-create. Specimen: `components/searchfield.html`.

**`AmountField`** — the canonical **money / numeric** input, consolidating the money entry that was hand-rolled four different ways across the kit (`.trm-value` in Terms, `.est-value` in Estimates, `.atm-amount` in New-transaction, and the local `MoneyField` inside `AddRenewalModal`). One labelled control built on the same `.odc-field` shell as `Field`, with an adornment **inside** the box — `prefix` for a currency symbol (`$`, `€`, `kr`), `suffix` for a unit (`%`, `bps`). The numeric text is monospaced + tabular so digits line up; values stay strings so partial entries (`3.`, `1,2`) aren't clobbered, and input is sanitized to digits/separators (plus a leading minus when `allowNegative` is set, for rates and deltas) — parse to a number on submit. Two sizes: default **`md`** for data-entry rows and **`lg`** for a hero amount input (the Estimate value). Carries the same `error` / `help` / `required` / `optional` contract as `Field`. Specimen: `components/amountfield.html`.

**`MoneyField`** — the canonical **money editor**: the amount and its **ISO 4217 code** as one control, the code on the right inside the same box. Odyssey is multi-currency and several currencies share a glyph, so the code is always shown, never a symbol. The code is a searchable picker (filter box above the list once it passes `searchThreshold`) or locked to static text for an account / base currency — identical metrics either way, so locking never shifts a row. Invalid keystrokes are blocked as typed: letters dropped, a second decimal separator or non-leading minus rejected outright. `sign` + `tone` render a leading −/+ colored by direction for signed amounts (the transaction dialog). Behind Premium/Coverage (renewals), Price (subscriptions), Estimated value, term amounts, budget planned amounts and the transaction hero.

**`CurrencySelect`** — the **currency-only** picker for wherever a currency is chosen without an amount (an account's currency, a budget's or tax statement's base currency): the same ISO-code list, search box, option rows and listbox keyboard pattern as `MoneyField`'s segment, wearing the standard `Select` chrome so it lines up in a form row.

**`FieldShell`** — the labelled-field **wrapper** every control shares: the label row (with the required `*` / muted "Optional" marker, and an optional right-aligned `aside` slot for a counter), the control (`children`), and the helper/error line. It's the composition primitive behind `Field`, `AmountField`, `NoteField` and `NumberField` (each reads it off the namespace at render, with an inline fallback), and the thing to reach for when labelling a control the kit doesn't field-wrap — a `Combobox` (Insurer, Insured account), a `MultiSelect`, a segmented control (Party kind), a locked-value display (Account), an upload dropzone (Attachments). It replaces the hand-rolled `.field` + `.label` + `.atm-opt` + `.helper`/`aam-err` markup so the label, optional hint and error line read identically everywhere. Pass `htmlFor` matching the control's `id` for label association + a `<htmlFor>-help` helper id. When **both** `help` and `error` are set it renders **two** nodes with distinct ids (`<htmlFor>-help` and `<htmlFor>-help-error`) so a hint and an error can coexist; a single node is unchanged when only one is set. Specimen: `components/fieldshell.html`.

**`NumberField`** — a labelled **numeric** input (native `type=number`, so it gets the platform stepper and numeric keypad) for plain quantities — counts, years, declared figures — where there's no currency symbol to show (for money use `AmountField`). Emits a parsed **`number`, or `null`** when cleared. Consolidates the `ATS_NumField` / `TS_NumField` helpers that were duplicated verbatim in the tax-statement dialogs (now thin aliases over it). `ariaLabelledBy` labels the input from an external element (e.g. a settings-row title); `ariaDescribedBy` is **appended** to — never replacing — the internal help/error association. Composes `FieldShell`. Specimen: `components/numberfield.html`.

**`CapacityField`** — the **capacity-limit** control: a right-aligned `NumberField` paired with a **"No limit"** `Switch`, for a cap that is either a finite number or explicitly **unbounded**. The count-cap control on the System settings import/export groups (contacts/events/tasks/entries per import & per export). Tri-state and page-owned: the caller keeps both `unlimited` and `value`, and toggling "No limit" **on** disables the input but **retains** the entered number (so toggling back off loses nothing — the number is simply not sent while unlimited). Emits `onValueChange(number|null)` and `onUnlimitedChange(bool)` separately; the number input is labelled by the row title (`ariaLabelledBy`) and described by the row hint (`ariaDescribedBy`), while the switch carries its own composed `aria-label`. `variant="inline"` is the `SettingField` form — one line inside the notched frame: the value, then a pill carrying the **inverse action** ("No limit" when a number is set, "Set a limit" when unlimited), so the pill never repeats the words already showing as the value and the pressed state reads as a state rather than a stutter. Specimen: `components/capacityfield.html`.

**`SettingField`** — one setting as a self-contained **field block**, in MudBlazor's `Variant.Outlined` shape: the label sits **on** the field's outline, the control sits inside it, and one **always-visible** helper line below carries the description and the "last changed" stamp (`meta`, rendered dimmer). The outline is a real `fieldset`/`legend`, so the **browser** cuts the notch — the gap tracks the label's own text metrics at any font size or zoom, and nothing has to be painted to match the card behind it. Inside the frame the child control is just its value: its border, background, padding and own helper slot are flattened by the sheet, and focus **thickens the outline** (2px primary, padding given back so nothing shifts) instead of adding a second ring. `error` renders above the helper line and turns the outline coral **without displacing the description**, so the reader keeps the definition while fixing the value; `dirty` shows the unsaved dot. Where `SettingRow` spends a full card's width on one value, `SettingField` folds label, control, description and provenance into a half-width block — so a section card holds an `.odc-sfield-grid` of related settings instead of a stack of one-setting cards (`wide` spans both columns for a content-width control; switches and actions use the `.odc-sfield-tile` shape, which has no text value to notch a label into).

Two further slots, both about a value that is **legal but not free**. `advisory` is an amber `role="status"` band below the helper line, opening with the literal word **"Advisory"** — for a raise that costs memory, response payload or third-party spend, or a check the server can only make heuristically. It never blocks Save, never sets `aria-invalid`, and its meaning is in its text rather than in the tint or the `aria-hidden` glyph. The word "Advisory" is set in **text-primary**, not amber: amber text on an amber tint clears 4.5:1 in **neither** theme (amber-500 measures ~2:1 on the light tint, amber-700 ~4.25:1), so the amber carries only the icon and the frame border, through the theme-aware `--pending-text` — which clears the 3:1 non-text minimum in both. The icon also runs at full opacity, the trap a 0.8-alpha group note falls into. `bound="lower-only" | "raise-only"` puts a small marker in the outline **beside the label**, because that is where the bound lives — for a setting whose opposite direction is refused rather than discouraged: a cap whose cost survives being lowered back (one row per generated occurrence is still written), or a control that **fails open** when its table fills, so a smaller number weakens it instead of tightening it. The reason belongs in `help`; the marker only says which way. Specimen: `components/settingfield.html`.

**`SecretSettingField`** — the same shape for a value that can never be read back. The settings store encrypts a secret on write and returns only whether it is there, so the control cannot be a text field with the value in it: it is a **state display plus an action**. The three states the store can answer with are three different messages. `found` is a fixed sixteen-bullet mask — fixed, because a mask that tracked the real length would leak it — with **Replace** and **Clear**, and "Value stored, hidden" for a screen reader, since a run of bullets says nothing. `not-set` renders its entry input immediately: it is the only state with no stored value to overwrite by accident and something that has to be typed, and its `consequence` goes in the amber advisory band, because after the upgrade that introduces a secret **every** row starts here and the page has to say what is not working meanwhile. `unreadable` is coral: the value is present, this instance's key ring cannot open it, and the consumer is failing closed right now — it shares no colour, glyph or wording with `not-set`, and `affects` names the feature that stopped, which nothing about a key's name conveys. Replacing from `found` or `unreadable` takes an explicit **Replace** first, so a stored credential cannot be overwritten by a stray keystroke, and the old value stays in force until Save. Entry is a password input **with a reveal toggle** — not a concession: a mistyped key fails silently and can never be read back to check, so the one moment it is legible is while it is being typed. The store's printable-ASCII rule (`0x20`–`0x7E`) is checked as you type and named in the error rather than left to arrive as a bare `400`; `allowNonAscii` drops the client check for a descriptor that has taken the relaxation. `kind="derivation"` marks the value in the outline, for a key with no provider to re-issue it from.

**`SecretClearDialog`** — the confirmation in front of clearing one, in two copy variants, because the two kinds carry different losses and one wording would either overstate or understate one of them. A **rotatable credential** is recoverable: the copy names what stops working and says the row returns to *not set*. A **derivation key** is not: a coral callout states that it cannot be re-issued and that anything already derived with it can never be re-derived. A third variant softens the copy when the row is already `unreadable`, where clearing breaks nothing currently working. One confirm button, not a typed confirmation — the action already sits behind Replace/Clear, and a value that cannot be read back cannot be re-typed to prove intent.

**`SecretClearOnSaveDialog`** — the other clear, the one nobody asked for. When a setting change *implies* destroying a stored secret — a new SMTP host, STARTTLS switched off — the confirmation cannot be `SecretClearDialog`: that one is fired by an immediate single-field action, and this one gates the settings page's single whole-page `Save()`. So the shape is reused and the copy is not. It names the change first (where mail will now be relayed, or that the connection is no longer encrypted), then the consequence (which secrets are cleared, in the same transaction — either both land or neither does), then the recovery (re-enter them in Email; until then mail is sent unauthenticated). The last line is the one a batch save makes necessary: **Cancel discards nothing** — every pending edit stays on the page, so the offending row can be put back by hand and the rest saved. The confirm button counts what it is about to submit ("Save 3 changes and clear"). — `components/secretclearonsavedialog.html`

**`TextInputField`** — a labelled **single-line text** input: `FieldShell` around a native `<input type="text">`, i.e. exactly `NumberField`'s shape. Reach for it over `Field` when the control has to be labelled or described by elements it does **not** own — a `SettingRow` title and description, a table column header, an inline edit row — because the input is rendered here, so `aria-labelledby` / `aria-describedby` land on the input itself rather than travelling through MudBlazor's attribute splat. An external `ariaDescribedBy` is **appended** to the internal help/error ids, never replacing them. `maxLength` + `showCount` render a live counter in the shell's `aside` slot. Emits a string. — `components/textinputfield.html`

**`ErrorSummary`** — the compact **"n problems · Review"** button that sits immediately before a **disabled** primary action, answering the question a greyed-out Save can't: what is blocking it, and where. Required on any page long enough that the offending field can be off-screen when the action is in view (the System settings catalogue, 42 rows across 11 sections); on a single-screen dialog the field-level error is enough. It is a **button, not a banner** — pressing it moves focus to the first blocking field or group alert, which is what makes a disabled action recoverable by keyboard. The count is folded into the accessible name. Its other half is `Button`'s `badge` — a count pill for the pending changes the action will commit. — `components/settingrow.html`

**`FormRow`** — an equal-width column grid for laying paired fields side by side in a dialog, the component form of the kit's `.aam-row2`: two columns by default, the standard 14px gutter, top-aligned cells (so a field with a helper line doesn't drag its neighbour down). `cols` for three-up rows; drop an empty `<div/>` to leave one field alone in a two-column row. Specimen: `components/formrow.html`.

**`NoteField`** — the canonical **multi-line note / description / comment** field, consolidating the textarea-with-counter that was hand-rolled in every create/edit dialog (the `.field` + `.atm-textarea` + `.trm-charcount` / `.est-charcount` pattern). One labelled control on the `.odc-field` shell: the head row carries the optional/required marker on the left and a live **`len/max`** counter on the right (it turns red once the value hits `maxLength`, which also sets the native `maxLength`). Same `error` / `help` / `required` / `optional` contract as `Field`; for a single-line value use `Field`, for a money value use `AmountField`. Now in use across `AddTermModal` (Note), `AddEstimateModal` (Note), `AddRenewalModal` (Notes), `AddInsurancePolicyModal` (Notes), `AddContractModal` (Description) and the New-transaction / Transactions **Extra data** fields. Specimen: `components/notefield.html`.

**Loading is a first-class state.** The product fetches a lot, so a screen should render its *shape* immediately and shimmer until data lands — never a blank panel or a centered spinner over empty space. **`Skeleton`** is the placeholder primitive (`variant` text / circle / block; `lines` for a paragraph); **`SkeletonRow`** drops a placeholder `<tr>` into a loading `Table`'s `<tbody>` so the row layout (including right-aligned numeric cells) holds steady as real rows replace it. The shimmer is the *only* place we animate a loading state, and it falls static under `prefers-reduced-motion`. The button spinner (`loading` on `Button`) stays for in-place action busy-states; skeletons are for first paint.

**Toasts are terse and bottom-right.** **`Toast`** is the snackbar — a transient confirmation matching the system's quiet success voice ("Saved." · "Approved 3 transactions."), with an optional inline action (Undo) and auto-dismiss via `duration`. `severity` tints a leading icon; **default carries no icon at all** — most confirmations are just a word. Errors get `role="alert"`, everything else `role="status"`. **`ToastStack`** is the fixed bottom-right positioner (the system's toast corner, per *Fixed elements* above); render your live toasts as its children. Maps to `MudSnackbar` / `ISnackbar`. Keep success silent-or-terse — no celebrations, no emoji.

**Change reads one way — through `Delta`.** Every "this differs from that" in the product routes through one indicator so the encoding never drifts. Three modes: `variance` (a reconciliation result — `0` reads reconciled in mint with a ✓, non-zero a discrepancy in amber, `null` "unavailable" disabled — the Tax Statements reconciliation cells), `directional` (a period-over-period change — ↑/↓/– arrow + magnitude, with `neutral` to mute the tint when up isn't "good", e.g. a rate move), and `signed` (a `+/−` amount in mint/coral — the `LineChart` / `StatTile` head deltas). Pass a `format(n)` for the magnitude; the component owns the sign, glyph, and color. It introduces no new hues — same `--finance-income` / `--finance-expense` / `--finance-pending` stack, just centralized. Live card: `components/delta.html`.

**Problems surface in three coordinated places.** When a screen has a data condition the user should act on (an account missing its exchange rate, a tax statement with off-currency transactions excluded or balances not yet synced), it uses one **problem/signal** pattern rather than ad-hoc alerts: (1) the `PageHeader` `signal` region rolls every affected record into a severity-tinted toggle with a count and clickable jump rows; (2) the record's row carries a severity `Chip`; and (3) the expanded detail shows a **`ProblemAlert`** — a severity-tinted block (`warning` amber · `error` coral · `info` sea) with a title, a navigate-to-fix CTA (`actionLabel` + `onAction`, routing to where it's resolved), and a detail line. Accounts and Tax Statements both implement all three. Severity follows the house convention (info → warning → error); the rollup and chip take the highest severity present. Live card: `components/problemalert.html`.

> **Money & status encoding still holds.** These primitives don't introduce new hues — amounts in the `Table` use `--finance-income` / `--finance-expense` with the redundant `+/−` sign, status uses the existing chips, and brand tide never encodes money. The table is chrome; the semantics live in the cells you render.

**Charts are data, not decoration.** Three consumable data-viz primitives cover what the product actually draws — all pure dependency-free SVG computed from data, themed via the `--chart-1…6` / `--chart-grid` / `--chart-axis` tokens (which step darker in light mode for contrast), and dropping straight into the existing chart specimens (`preview/24–28`). **`Sparkline`** is the axis-less trend strip for stat tiles — pass a `data` number[], it auto-scales to its own min/max; area fill is the stroke color at low opacity. **`LineChart`** is the *axis'd* counterpart (the card with gridlines + value/category axis labels + a head figure & delta) behind the Dashboard net-worth chart and the Tax Statements overview: pass `series` `[{label,value}]`, a `format` (e.g. `H.money`) for the figure, an optional compact `axisFormat` for the y-ticks, `showDelta`+`deltaSuffix` for the latest-vs-first delta, and `cumulative` to plot a running total. **`Donut`** is the allocation **panel** ported from the kit's `DonutPanel` (Accounts asset/liability rings, Budgets planned-in/out): a ring whose hole holds only a **muted watermark icon** — never a number — over a legend ("ledger") of slice rows (swatch · name · amount · %), with the **total in its own row at the foot of the legend** so large sums never overflow the hole. `data` is `[{label,value,color?}]`, zero-values dropped, slices auto-colored from the categorical palette in order with a small gap so same-family hues stay distinct; pass `format` (e.g. `H.money`) and a `centerIcon`. **`DonutLegend`** is that same ledger standalone, for when the ring and ledger are laid out separately. The categorical palette deliberately runs tide → sea → mint → violet so allocation slices never read as income/expense; reach for `color` overrides only when a slice has a fixed brand meaning. These are the *only* place hand-built SVG is sanctioned — it's data viz, not illustration.

> **Density is tokenized.** Row + control heights live as tokens (`--row-h` 48px / `--row-h-dense` 36px; `--control-h` 40px / `--control-h-dense` 32px). The `Table` wires its rows to `--row-h` / `--row-h-dense` (the `dense` prop), so MudBlazor's `Dense` maps to the `-dense` step instead of bespoke per-table padding.

---

## Components — server pagination

The product is moving search, filtering, sorting, and paging from the browser to the **server**: each list page fetches **one page at a time** via a shared `PagedResult<T>` envelope (`items` + `page` + `pageSize` + `totalCount`) and renders the server's order verbatim, instead of pulling a large capped window and re-sorting it client-side. This supersedes the old "filtered list renders whole / no pager" note that lived on the ledger pages — the design system now ships the pager and the state contract that pattern needs.

> **Rollout reality.** This lands in iterations. **Iteration 1** keeps the UI unchanged: the client just requests page 1 with a large `pageSize`, gets the whole set as before, and keeps its in-browser sort/filter/search — **no GUI change, no pager visible.** The pieces below are the target state for when a page flips to true server paging; build new list work against them so it's ready.

**`Pager`** — **the shared list pager** below every server-paged **flat-table** page (Transactions, Files, Users, Contacts, Currencies, Exchange rates, Transaction tags, …). It is the **canonical, always-present home of the rows-per-page control** — rendered even on a table with no toolbar, so a page can never end up with no way to change the page size. Anatomy, left→right: a **rows-per-page selector** (presets **25 · 100 · 1000 · All**, default 25) + the **one canonical summary** `Showing X–Y of N` (or `0 results` when empty, always **as text**, never colour/icon alone); then the nav cluster — **first / previous / next / last**. **`All`** requests every matching row (the client virtualizes them) and the pager then reports a single page. Controlled — pass `page` (1-based) + `pageSize` (number | `'all'`) + `totalCount` and handle `onPageChange(nextPage)` + `onPageSizeChange(nextSize)`; `TotalPages` is derived (`ceil(totalCount / pageSize)`), never passed. `loading` turns the nav buttons into inert no-ops, disables the size selector, and shows a busy spinner in the summary while a fetch is in flight.

**`PageSizeSelect`** — **the toolbar mirror** of the footer Pager's rows-per-page control. Mount it in a list page's **search/toolbar** region (trailing edge, after the filters) and bind it to the **same `pageSize` state** the footer reads — the two stay in sync because they read/write one value. It is **additive**: the footer selector is the always-present home; the mirror appears **only where a search bar exists** to host it. Reads `Show 25 ▾` (change the verb with `prefix`, or `""` for a bare value), opens **downward**, and is 40 px tall to line up with `SearchField` / `MultiSelect`. Specimen: `preview/30`.

> **Accessibility contract (pinned).** The pager is a `<nav aria-label="Pagination">` landmark with real `<button>` first / prev / next / last (plain-text names). **At a bound the button stays focusable and enabled but `aria-disabled="true"` with a no-op activation** — it is *never* given the native `disabled` attribute, which would drop it from the tab order and lose focus (WCAG 2.4.3). When a prev/next press reaches a bound and that button goes `aria-disabled`, **focus moves to the opposite, still-active button** so focus is never stranded. Targets are ≥ 24 px with a visible `:focus-visible` ring. Specimen: `preview/30`, styled `.odc-pager-*` / `.odc-rpp-*`.

**The state contract around it.** A migrated page owns `{ page, pageSize, totalCount, sort, filters, search }`, resets `page` to 1 on any search / filter / sort / **page-size** change (page itself is **not** persisted — every load starts at page 1), and feeds the table **one page** of rows:

- **`RecordTable` / `TxnTable` render server order verbatim** — use the existing **controlled sort** (`sort` + `onSortChange`): a header click raises a complete `{key,dir}` to the parent, which refetches, rather than re-sorting in place. (The tables already support this; a `ServerSort` pass-through that skips the internal `OrderBy` entirely is the Blazor-side name for it.)
- **Search debounces (~300 ms)** before it refetches — the same debounce the ledger `SearchField` already uses — and the last request in flight wins, so fast typing never renders a stale page.
- **Three outcomes, never conflated:** **Loaded** (render `items`, update the pager), **Empty** (query matched nothing → the muted no-match `EmptyState` above a `0 results` pager), and **Error** (fetch failed → keep the last good page + an inline Retry, plus a `Toast`). Loading stays a first-class state — shimmer the rows in place, don't blank the panel.

Maps to the Blazor `OdsPager` (a `MudBlazor`-button pager mirroring the `/users` pager) over each list's `PagedResult<T>` GET.

---

## Iconography

**Primary set: Material Icons (filled).** Loaded from Google Fonts in `wwwroot/index.html` and consumed via MudBlazor's `@Icons.Material.Filled.*` constants. The codebase already uses these icons in `NavMenu.razor`, so they are canonical:

| Concept | Material Icon |
|---|---|
| Dashboard | `space_dashboard` |
| Accounts | `account_balance_wallet` |
| Budgets | `pie_chart` |
| Insurance | `shield` |
| Contracts | `handshake` |
| Subscriptions | `subscriptions` |
| Transactions | `receipt_long` |
| Tags | `local_offer` |
| Contacts | `store` |
| Currencies | `attach_money` |
| Preferences | `tune` |
| User Account | `account_circle` |
| Sign out | `logout` |
| About / external | GitHub brand icon (`@Icons.Custom.Brands.GitHub`) |

**Style.** Material Icons **Filled** weight by default at 24px. Use 20px in dense rows and 18px inside chips. Outlined / Rounded variants are reserved for hero illustrations (none today).

**Color.** Icons inherit `currentColor`. In nav, icons use `--mud-palette-text-secondary` for inactive and `--mud-palette-primary` for active. In tables, they use text-secondary unless they carry semantic meaning (income → mint, expense → coral, pending → amber).

**SVGs vs. font.** The Material Icons *font* is the production path (already cached, used by every MudBlazor `Icon=` prop). Standalone SVGs are used only for the **Odyssey logomark and wordmark** in `assets/`. Don't draw your own SVG icons — Material Icons covers every concept the product needs; if you reach for a custom SVG, look harder.

**Emoji.** Never. Not in nav, not in copy, not in empty states. The brand voice is numerate; emoji break that.

**Unicode characters.** Acceptable: `—` (em dash) for ranges, `→` for sequences in marketing/onboarding, `·` (middle dot) as a metadata separator (`USD · Updated 2 min ago`). Avoid arrows in product chrome (use Material Icons `arrow_forward` etc.).

**File assets.**
- `assets/odyssey-logomark.svg` — official compass-rose logomark, mark-only (200×210 viewBox). North needle in bright tide-glow on a deep teal frame, with gray secondary needles for the other three cardinals. Use on auth screens, splash, favicons, anywhere the brand stands alone.
- `assets/odyssey-wordmark.svg` — the full lockup with `ODYSSEY` underneath in tide-glow, 500 weight, 5px letter-spacing, all caps. Use at the top of the drawer and any horizontal brand placement.
- `assets/odyssey-logo-animated.svg` — the animated draw-on version (CSS-driven stroke + fade). Use on splash / loading states only; never inline in product chrome.
- `assets/odyssey-favicon-16/32/192/512.png` are the **Odyssey compass logomark** rasterized as the full favicon export set (use the 32px as the standard browser-tab favicon).

**Logomark colors — the brand's exact hex values.**

| Role | Hex | Token |
|---|---|---|
| Frame, rings, ticks | `#006B5A` | `--tide-deep` |
| North needle, pivot dot, wordmark | `#00F5D4` | `--tide-glow` |
| Secondary needle (tip) | `#707070` | (literal) |
| Secondary needle (tail) | `#404040` | (literal) |

---

## Components — Dialogs

Every modal flow shares one shell: the design system's **`Modal`** component (`components/Modal.jsx` — `.odc-scrim` / `.odc-modal` / `.odc-modal-head` (`.odc-modal-lead` + title column + close) / `.odc-modal-body` / `.odc-modal-foot`, `components.css`). It is a body-portaled scrim (click-out + Esc to close, with a subtle blur) under a 520px surface that rises in on open, hardened with a real a11y layer — focus trap (landing on the first body field), body scroll-lock, focus restore on close, `aria-labelledby` wiring. Per-dialog `className` widens it (`atm-` transaction 560px, `afm-` upload 560px, `fan-` analyze + `wide` 1240px). The **head is a tinted band** (the same subtle lift + hairline as the footer, so the body surface is bracketed top and bottom) carrying an **optional leading `icon` tile** (brand-tide by default, `iconTone="warning"`/`"error"` for destructive/confirm dialogs) and a title column that **fills the width up to the ×**, so the subtitle wraps only as it nears the close button (≤58ch measure cap). Every dialog sets a lead icon tied to its entity — New account `account_balance_wallet`, New transaction `receipt_long`, New budget `pie_chart`, Upload `cloud_upload`, New contact `store`, New currency `attach_money`, New tag `local_offer`, New exchange rate `currency_exchange`, Analyze file `document_scanner`, New term `percent`, edit dialogs `edit`. (The kit's legacy `.aam-*` classes remain in `kit.css`, mirrored 1:1, for the hand-rolled `FileViewerModal` surface and the static anatomy card.) Anatomy + the wording rules: `preview/37-dialog-anatomy.html`.

**Wording follows the create/new convention** (see Content fundamentals): a creation **trigger** reads *New X*, the **dialog title** reads *New X*, and the **primary button** reads *Create X*. Edit dialogs are titled *Edit X* with a *Save changes* primary (one component often does both, keyed on whether a record was passed). Upload keeps upload verbs (*Upload file* / *Upload files* / *Upload*). One-off process dialogs share a verb across title and primary (*Analyze file*, *Set rate*, *Reconnect*).

Each dialog has a live specimen card in the **Dialogs** group:

| Dialog | Component | Kind | Card |
|---|---|---|---|
| New / Edit account | `AddAccountModal` | create + edit | `preview/38` |
| New / Edit transaction | `AddTransactionModal` | create + edit | `preview/39` |
| New / Edit budget | `AddBudgetModal` | create + edit | `preview/40` |
| New / Edit budget item | `AddBudgetItemModal` | create + edit | `preview/41` |
| Upload files | `AddFileModal` | upload | `preview/42` |
| New contact | `AddContactModal` | create | `preview/43` |
| New / Edit currency | `AddCurrencyModal` | create + edit | `preview/44` |
| New / Edit tag | `AddTagModal` | create + edit | `preview/45` |
| New / Edit exchange rate | `RecordRateModal` | create + edit | `preview/46` |
| New / Edit term | `AddTermModal` | create + edit | `preview/48` |
| File viewer | `FileViewerModal` | viewer | `preview/47` |
| Analyze file | `AnalyzeFileModal` | process (consent-gated · resumable · **AI-matched**, with a Matching step + match-degraded fallback) | `preview/28` |

> **Convergence note — resolved.** Every create/edit/process dialog in the table above is now built ON the consumable `Modal` (it owns the scrim, head, scrollable body, footer, Esc/click-out, focus trap and restore); each dialog supplies only its body fields, footer verbs, and lead icon. The one deliberate exception is `FileViewerModal` — a full document-viewer chrome (header · toolbar · stage · footer) that composes only the scrim/surface primitives.

---

## Reference data — Contact types

The **ContactType** enum (`Odyssey.Finance.Dtos/ContactType.cs`) has exactly six members — **Merchant · Person · Organization · Company · Institution · Other** — and `Other` is the `NewContact` default. The enum carries *only* a name; the **icon** and **color** for each type are a design-system decision, so they live in one canonical registry, `OdysseyData.contactTypes` (`ui_kits/web/data.js`), the sister of `accountTypes`. Every surface that renders a contact — the table's leading avatar, the type chip, the detail tile, and the inline/create pickers — reads that registry, so a type looks identical everywhere and a recolor is a one-line edit. `Contacts.jsx` no longer hard-codes the list; it sources `CP_TYPES` from the registry. Specimen: `preview/33-data-contact-types.html`.

| Type | Material icon | Color (oklch) | Meaning |
|---|---|---|---|
| **Merchant** | `storefront` | `0.79 0.115 188` (teal) | A shop, store, or service you pay — the everyday spending contact. |
| **Person** | `person` | `0.80 0.15 150` (green) | An individual — a friend, landlord, or contractor. |
| **Organization** | `corporate_fare` | `0.72 0.16 295` (violet) | A non-commercial body — charity, club, HOA, association. |
| **Company** | `business` | `0.76 0.13 225` (blue) | A registered business — typically an employer or vendor. |
| **Institution** | `account_balance` | `0.75 0.16 330` (magenta) | A bank, lender, government, or utility. |
| **Other** *(default)* | `category` | `0.74 0.02 250` (neutral) | The DTO default — anything outside the categories above. |

> **Encoding rule.** These hues identify a *category*; they share the categorical chroma/lightness band with `accountTypes` (L ~0.72–0.80, C ~0.12–0.16) so the two registries read as one family. They never encode income / expense / status, and brand **tide** / **sea** stay out of the scale. The avatar fills the soft (16%) tint behind the `color` glyph; the type chip is an outline carrying the same icon — never a filled swatch. Keep the registry keys in lockstep with the C# enum.

**Typed pickers.** Two consumable components turn the registry into ready-made controls, so a feature never re-wires the option list: **`ContactTypeSelect`** (single — the Type field on the create / edit contact forms) and **`ContactTypeMultiSelect`** (the ledger-header Type filter). Both are thin wrappers over the base `Select` / `MultiSelect`, pre-fed `CONTACT_TYPES` so every option renders its Material icon in its category color — value is the enum key, and everything the base control takes (label, help, error, `align`, …) passes through. Pass `types` to subset or reorder (e.g. drop `Other`). `CONTACT_TYPES` is exported on the DS namespace as the consumable layer's source of truth (mirrors the kit's `OdysseyData.contactTypes`). To support them, the base **`Select`** and **`MultiSelect`** gained an optional per-option **`icon`** + **`iconColor`** — additive and backward-compatible (options without an icon render exactly as before), so any select can carry a leading glyph. Specimen: `preview/25-components-type-pickers.html` (live), registry card `preview/33-data-contact-types.html`.

**Fields.** Beyond Type, a contact carries **Name** (≤ 128 chars, required), a server-derived **NormalizedName**, an optional **Description** (≤ 1024 chars, edited as a multi-line `Field`), and an optional **OrganizationNumber** — a free-text registration/tax identifier (string, ≤ 64 chars). It surfaces as a `mono` detail tile and a single-column field on both the create and inline-edit forms; null/empty renders as `—`. The synthetic record ID is **not** shown as a detail tile (it lives only in the *Copy contact ID* menu item).

---

## Reference data — File types

Files attach in **three contexts, each with its own enum** — a distinction worth getting right:

- **`AccountFileType`** (`Odyssey.Finance.Dtos/AccountFileType.cs`, field `FileType` on `ExistingAccountFile`) — documents filed against an **account**: **Other(0) · Message(1) · Statement(2) · Contract(3) · Tax(4) · Documentation(5) · InsurancePolicy(6) · LoanAgreement(7) · RepaymentSchedule(8) · PurchaseAgreement(9) · Valuation(10) · Warranty(11) · Registration(12) · Prospectus(13)**.
- **`TransactionFileType`** (`TransactionFileType.cs`, field `Type` on `ExistingTransactionFile`) — proof attached to a single **transaction**: **Receipt(0) · Invoice(1) · Other(2) · CreditNote(3) · Quote(4) · PaymentConfirmation(5) · Documentation(6)**.
- **`TaxStatementFileType`** (`TaxStatementFileType.cs`, field `FileType` on `TaxStatementFile` — newly added) — documents on a **tax statement**: **TaxReturn(0) · TaxAssessment(1) · SupportingDocument(2) · Other(3)**.

So `Receipt` is a real `TransactionFileType` member, not a missing `AccountFileType` value. As with contacts, each enum carries only a name; the **icon** and **color** live in canonical registries — `OdysseyData.accountFileTypes`, `transactionFileTypes`, and `taxStatementFileTypes` (`ui_kits/web/data.js`), siblings of `accountTypes` and `contactTypes`. Every file surface reads them, so a kind looks identical everywhere; a merged `OdysseyData.fileTypeByKey` lookup renders any kind from any of the three enums (the Files-table avatar, kind chip, the upload picker, the edit-file picker, the file viewer, the account-detail list). Specimen: `preview/34-data-file-types.html`.

**AccountFileType** — files on an account:

| Type | Enum | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **Message** | 1 | `mail` | `0.76 0.13 225` (blue) | Saved correspondence — an emailed notice or letter. |
| **Statement** | 2 | `description` | `0.79 0.115 188` (teal) | A periodic account statement. **The only analyzable type.** |
| **Contract** | 3 | `history_edu` | `0.72 0.16 295` (violet) | A signed agreement — loan terms, an account-opening or deposit form. |
| **Tax** | 4 | `request_quote` | `0.75 0.16 330` (magenta) | A tax document — 1099, 1098, year-end summary. |
| **Documentation** | 5 | `menu_book` | `0.77 0.14 110` (lime) | Reference material — a manual, guide, policy booklet. |
| **InsurancePolicy** | 6 | `shield` | `0.74 0.15 30` (orange) | Insurance coverage (home / contents / auto). Carries a policy period. |
| **LoanAgreement** | 7 | `gavel` | `0.72 0.15 265` (indigo) | The original loan / credit agreement. |
| **RepaymentSchedule** | 8 | `event_repeat` | `0.78 0.14 160` (green) | An amortization plan / instalment schedule. |
| **PurchaseAgreement** | 9 | `sell` | `0.79 0.14 60` (amber) | The purchase & sale contract for an asset. |
| **Valuation** | 10 | `price_check` | `0.80 0.15 140` (green) | A professional valuation / appraisal report. |
| **Warranty** | 11 | `verified` | `0.77 0.13 205` (cyan) | Manufacturer / extended warranty (usually carries an expiry). |
| **Registration** | 12 | `app_registration` | `0.74 0.15 310` (purple) | A registration certificate — vehicle reg, deed, title. |
| **Prospectus** | 13 | `auto_stories` | `0.78 0.14 95` (yellow-green) | A fund prospectus / KID for an investment or pension. |
| **Other** *(default)* | 0 | `insert_drive_file` | `0.74 0.02 250` (neutral) | The enum default. |

**TransactionFileType** — files on a transaction:

| Type | Enum | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **Receipt** | 0 | `receipt_long` | `0.80 0.15 150` (green) | A purchase receipt — proof of payment. |
| **Invoice** | 1 | `receipt` | `0.80 0.13 85` (amber) | A bill or invoice the transaction settles. |
| **CreditNote** | 3 | `assignment_return` | `0.72 0.16 22` (red) | A refund or credit memo against an earlier charge. |
| **Quote** | 4 | `format_quote` | `0.72 0.16 295` (violet) | A pre-invoice quotation or estimate. |
| **PaymentConfirmation** | 5 | `price_check` | `0.76 0.13 225` (blue) | A bank-transfer / payment confirmation slip. |
| **Documentation** | 6 | `menu_book` | `0.77 0.14 110` (lime) | General supporting documentation. |
| **Other** *(default)* | 2 | `insert_drive_file` | `0.74 0.02 250` (neutral) | Any other supporting document. |

**TaxStatementFileType** — files on a tax statement *(new)*:

| Type | Enum | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **TaxReturn** | 0 | `assignment` | `0.75 0.16 330` (magenta) | The filed return for the fiscal year. |
| **TaxAssessment** | 1 | `fact_check` | `0.72 0.16 295` (violet) | The authority's assessment / settled figures. |
| **SupportingDocument** | 2 | `attach_file` | `0.77 0.14 110` (lime) | Backing material — receipts, deduction evidence. |
| **Other** *(default)* | 3 | `insert_drive_file` | `0.74 0.02 250` (neutral) | The enum default. |

> **Notes.** Each list is enum order with `Other` (the default in each) pulled last. The enums share only `Other` — different enum values, identical icon/color. Only account **`Statement`** is analyzable — the server rejects every other type, and `OdysseyHelpers.canAnalyze` keys off the kind, not the icon. Account types **6–13** and the new transaction / tax-statement types were added to cover what property, vehicle, loan, and investment accounts actually file; keep each registry's keys in lockstep with its C# enum. The upload picker is context-aware: `AddFileModal` shows account types, `AddTransactionModal` passes the transaction vocabulary, and the tax-statement upload passes the tax vocabulary.

### Document validity metadata (`AccountFile`)

An `AccountFile` row now carries four **optional, nullable** join-entity fields — they describe the *document's* validity, not the raw bytes (which live on `FileMetadata`):

| Field | Type | Purpose |
|---|---|---|
| `ValidFrom` | `DateTime?` | When the document takes effect — e.g. an insurance policy start. |
| `ValidTo` | `DateTime?` | When it expires — policy end, warranty expiry. |
| `IssuedAt` | `DateTime?` | When the document was issued / signed. |
| `IssuedBy` | `Guid?` | Issuing institution — an FK to **Contacts**. |

Because they're nullable, existing attachments are unaffected. In the UI they surface in two places: the **FilesTable** detail well shows a *Valid from / Valid to / Issued / Issued by* row when any are present (`issuerFor` resolves the contact id to a name), and the inline **edit panel** + the **upload modal** (`AddFileModal`, account context only) expose date pickers and an *Issued by* contact select. The `ValidFrom`/`ValidTo` pair is what future expiry-alert features will key off. These fields live on `AccountFile` only — transaction and tax-statement attachments don't carry them.

**Typed pickers.** One pair per enum, all thin wrappers over `Select` / `MultiSelect` with each option's icon in its category color: **`AccountFileTypeSelect`** / **`AccountFileTypeMultiSelect`** (the Files-page filter is wired to the latter), **`TransactionFileTypeSelect`** / **`TransactionFileTypeMultiSelect`**, and **`TaxStatementFileTypeSelect`** / **`TaxStatementFileTypeMultiSelect`**. Value is the enum key; pass `types` to subset. `ACCOUNT_FILE_TYPES`, `TRANSACTION_FILE_TYPES`, and `TAX_STATEMENT_FILE_TYPES` are exported on the DS namespace (mirroring the `OdysseyData` registries). Specimen: `preview/27-components-file-type-pickers.html` (live).

---

## Components — Budgets page

The **Budgets page** is the planning screen at `/budgets`, the sister of the Accounts page. Specimen: `templates/budgets` (the whole page, the first budget expanded, static); reference build: `ui_kits/web/Budgets.jsx`. Like Accounts it composes the **Page header** + a list of expandable **records** (one per budget) — but the record holds a *plan*, not a ledger: a period (`StartDate`–`EndDate`, base currency) with **income and expense items**, each item a planned amount whose **actual is derived** from the transactions its tag matched in range.

**It owns no new chrome — except the item table.** The page is a **`RecordCard`** rollout (as Accounts, Subscriptions, Tax statements are): each budget is one card whose body follows the fixed order — *details* (the full field set as `InfoTile`s) → *content* (Description) → *sections* (Allocation · Budget items · Transactions, each introduced by a `SectionDivider`). The list owns one `openId`, so opening a budget closes its siblings. The one Budgets-specific surface is the **planned-vs-actual item table** (with its `.bgt-*` rows, fill bars, and the *Edit multiple* batch grid). Unlike Accounts, the header has **no Overview or Problems region** — allocation lives inside each record and budgets carry no rate problems; its sub reads `N active · planned balance $…` (net planned, archived excluded).

**Anatomy of an expanded budget** (`BudgetRecordCard` → `BudgetTiles` + `BudgetDetail`):

| # | Slot | What it shows · Maps to |
|---|---|---|
| 1 | **Header** | Type mark + name + status chip; meta line `start → end · currency`; counts (items · matched transactions); figure **Expected balance** (planned in − planned out), income/expense-toned. |
| 2 | **Details tiles** | The full record: Name / Start / End / Base currency / Status, then the four roll-ups — **Planned income · Planned expenses · Actual income · Actual expenses** — and the two balances, **Expected** and **Actual** (foot reads *ahead of / behind plan*). Derived tiles never replace the planned tiles they come from. |
| 3 | **Content** | Description, in one wide tile. Absent when the budget has none. |
| 4 | **Allocation** | Two rings — **Planned income** and **Planned expenses** — each sliced by *this* budget's planned lines, largest-first, zero-planned lines dropped. Planned, not actual. |
| 5 | **Budget items** | The planned-vs-actual table — income group then expense group, color-coded by kind. **New item** and **Edit multiple** live in the record menu. |
| 6 | **Transactions** | The budget's matched transactions (`BudgetReport.Transactions`), paged in place. |

**The item row is the page.** Items group by kind with no header band — the row's left rule + tint mark **income** (mint) vs **expense** (coral). The bar fills `min(actual / planned, 100%)`; an expense past its plan turns the bar **solid coral** and prints an **over by $X** overflow. **Untagged** items are plan-only — their actual reads a neutral `—`. Per-item `actual = Σ|amount|` of transactions whose tag matches the item, inside the budget's date range — exactly the server's `BudgetReport` per-tag sum.

**Edit multiple** swaps the read-only *item* table for an inline batch grid — name, category (picked via **`BudgetCategoryTypeSelect`** — Expense / Income with category glyphs, on the shared `TypeSelect`), transaction tag, planned amount per row, plus an always-visible delete column. Every keystroke commits to the draft; **Done** returns to the planned-vs-actual view. (This same grid, widened, is the Analyze-file review step.) Editing the **budget record itself** (name, dates, currency, description) is a different action: the row menu's **Edit budget** opens `AddBudgetModal` in edit mode, not an inline panel.

**Menus & lifecycle.** The **budget menu** (`more_vert` per row): Edit budget · New item · Edit multiple · Copy ID · — · Archive/Unarchive · Delete. The **item menu** (revealed on row hover): Edit · Copy ID · — · Delete. Archiving dims the row and drops the budget from the header's active count and net planned balance. Edit budget expands in place (Name / dates / currency / Description) — it never navigates.

> **Stack reality check.** The list is `BudgetsCard.razor` (`/budgets`), the detail `BudgetCard.razor` (`/budgets/{id}`), driven by `Odyssey.Finance` budget DTOs (`Budget`, `BudgetItem` with `CategoryType` + `TransactionTagId` + `Planned`). The derived actuals and the two balances are the client-side stand-in for the server's `GET /api/budgets/{id}/transactions` → `BudgetReport`, which groups matched transactions by tag and sums them; the prototype computes the same in `data.js` (`budgetItemActual` / `budgetTotals` / `budgetMatchedTxns`). The donuts reuse the allocation-donut primitive (charts, p.11). The `.bgt-*` item table is the only net-new CSS.

---

## Reference data — Term kinds

The **TermKind** enum classifies each entry in an account's rate & fee history (the **AccountTerm** feature — see *Components — Account rate & fee history*). Two **rates** lead — **InterestRate** and the optional **ExpectedReturn** — then four **fees**: **ManagementFee · ServiceFee · TransactionFee · OtherFee**. As with every Odyssey registry, the enum carries only a name; the **group** (rate / fee), **Material icon**, **color**, and **default unit** are a design-system decision, defined once in `OdysseyData.termKinds` (`ui_kits/web/data.js`) so a kind reads identically in the step chart, the current-terms summary, the history table/timeline, the New term picker, and the account-row subtitle. Specimen: `preview/36-data-term-kinds.html`.

| Kind | Enum | Group | Icon | Color (oklch) | Default unit | Meaning |
|---|---|---|---|---|---|---|
| **Interest rate** | 1 | Rate | `percent` | `0.78 0.13 200` (cyan) | Percentage | Contractual interest the account earns or is charged. |
| **Expected return** | 2 | Rate | `trending_up` | `0.72 0.16 295` (violet) | Percentage | Optional target / expected annual return for a variable-return holding. |
| **Management fee** | 10 | Fee | `pie_chart` | `0.77 0.14 55` (amber) | Percentage | Fund / platform / management fee — usually a % of assets. |
| **Service fee** | 11 | Fee | `event_repeat` | `0.76 0.13 225` (blue) | Amount | Periodic account / service fee — usually a flat amount. |
| **Transaction fee** | 12 | Fee | `swap_horiz` | `0.75 0.16 330` (magenta) | Amount | Per-transaction fee — an amount or a percentage. |
| **Other fee** | 99 | Fee | `receipt_long` | `0.74 0.02 250` (neutral) | Amount | Any other fee outside the categories above. |

**Value unit (`TermValueUnit`).** `Percentage` — a fraction in `[-1, 1]` (3.40% stored as `0.0340`; negatives allowed); `Amount` — a money value (currency required). Rates are always Percentage; `TransactionFee` can be either; the other fees default as above.

**Eligibility matrix (`termKindEligibility`).** Which account types a kind can apply to — in code, not the DB, so it changes without a migration. The New term dialog hides ineligible kinds.

| Kind | Eligible account types |
|---|---|
| `InterestRate` | Checking · Savings · Pension · Credit card · Mortgage · Student loan · Personal loan · Car loan · Tax debt |
| `ExpectedReturn` | Investment · Pension |
| `ManagementFee` · `ServiceFee` · `TransactionFee` · `OtherFee` | All account types |

**Billing period (`BillingPeriod`).** Optional context for fees, null for rates: **OneTime(0) · PerTransaction(1) · Daily(2) · Monthly(3) · Quarterly(4) · Annually(5)** — rendered with a compact suffix (`/txn · /day · /mo · /qtr · /yr`).

> **Encoding rule.** Like `accountTypes` / `contactTypes` / file-type registries, these hues identify a *kind* and share the categorical chroma/lightness band (L ~0.74–0.80, C ~0.13–0.16) so all the registries read as one family. They never encode income / expense / status, and brand **tide** stays out of the scale. Keep the registry keys in lockstep with the C# `TermKind` enum values.

---

## Components — Account rate & fee history

The **Terms** section (`AccountTerms.jsx`) is a new zone inside the expanded account record (Accounts → account detail), beside **Files** and **Transactions** — a quiet one-word sibling (the **§** section glyph) that leads, sitting first under the metadata grid. (It's titled **Terms**, not "Rate & fees", because it holds fees and other terms too, not only a rate.) It backs the **AccountTerm** feature: a time-versioned history of an account's **terms** — its interest rate, an optional expected return, and the prices of its bank services (fees). The latest entry on or before a date is the value **in force** (implicit supersession — there is no `EffectiveTo`). Specimen: `preview/31-account-rate-fees.html` (two account contexts + the empty state, with the summary / history tweaks live); reference build: `ui_kits/web/AccountTerms.jsx` + the `AddTermModal.jsx` dialog. Styled by `account-terms.css` (the only net-new sheet, `.trm-*`).

**It owns one new chart and reuses the rest of the kit.** The section is three stacked zones, leading with the interest rate:

| # | Zone | What it shows · Maps to |
|---|---|---|
| 1 | **Rate hero** | A **step-line chart** of the interest rate over time (falls back to expected return; hidden when the account has no chartable rate). Effective-dated rates **hold flat and jump** on each change — the line steps, never interpolates — and extends the current value to a dashed **Today** marker. The header carries the current value + a neutral, directional **delta** vs the prior entry. |
| 2 | **Current terms** | One value **in force** per kind — the `GET …/terms/current` projection. Three summary styles, tweakable: **tiles** (default) · compact **row** · **chips**. |
| 3 | **History** | The full `GET …/terms` list, grouped **Rate history** then **Fees**, newest first. Two views, tweakable: a **table** or a vertical **timeline**. Each entry is editable / deletable in place; the in-force row is marked, future-dated rows read **Scheduled**, older ones **Superseded**. |

**The delta is neutral by design.** A rate change shows a direction arrow + magnitude (e.g. *↓ 0.20% vs Jul ’25*) in a muted chip — **never** green-up / red-down, because the same direction means opposite things on a savings rate vs a loan APR. The brand voice states what's *in* the account, it doesn't judge it.

**A loan's interest rate reads as a cost.** Interest *charged* on a liability (loan, credit card, …) is money out, so its rate is shown **negative and in the expense color** — `−6.49%`, coral — mirroring how the account's own balance is shown. Interest *earned* on an asset (a savings rate) and an **expected return** on an investment stay positive. The whole rate panel follows the sign: a liability's step chart plots in the negative region with a coral line, so a *rising* APR trends **downward** (a growing cost), consistent with Odyssey's negative-is-coral language. The kind glyph keeps its categorical color (the `percent` tile stays cyan — it identifies *interest rate*); only the value, chart, and subtitle carry the cost sign. Fees keep their own price framing (a positive amount you pay). The sign is presentation-only — the stored `Value` is the positive magnitude the spec expects, flipped at display time by the account's asset/liability group, so the New term dialog still takes a plain `6.49`.

**The rate reaches the collapsed row.** The account record's subtitle carries the in-force **rate** after the account number — interest rate, or `~`-prefixed expected return for investment / pension — never a fee, and only when the account has one (a Checking account shows nothing). It's the one term worth seeing without expanding. The account's overflow menu also gains **New term** (beside New transaction), which expands the record and opens the create dialog; it shares the section's term state, so a term added from either place updates the chart, the summary, the history, and the header rate at once.

**New / Edit term** (`AddTermModal.jsx`) is built on the shared DS **`Modal`**, like every other dialog. Its field set mirrors the `NewAccountTerm` DTO and enforces the spec's rules:

| Field | Maps to | Control · rule |
|---|---|---|
| Term | `TermKind` | **Eligibility-gated** kind grid — interest rate only on interest-bearing accounts, expected return only on investment / pension, fees broadly. Ineligible kinds are hidden. Locked on edit. |
| Value | `Value` · `ValueUnit` | Hero input. **Percentage** typed as a percent, stored as a fraction in `[-1, 1]` (3.40 → 0.0340; negatives allowed); **Amount** ≥ 0. Unit locked to Percentage for rate kinds. |
| Currency | `CurrencyCode` | Required for an Amount (defaults to the account currency); disabled / null for a Percentage. |
| Effective from | `EffectiveFrom` | DateField, required. Past or future allowed (future = Scheduled). An exact `(TermKind, EffectiveFrom)` duplicate is rejected — the server's `409`. |
| Billing period | `BillingPeriod` | Fees only (`/mo · /yr · /txn`); hidden and null for rate kinds. |
| Note | `Note` | Optional, ≤ 512 chars. |

The term-kind registry — label / group / icon / color / default unit, plus the eligibility matrix and the `BillingPeriod` set — lives in `data.js` (`OdysseyData.termKinds` / `termKindEligibility` / `billingPeriods`), the canonical sibling of `accountTypes` / `contactTypes` / file-type registries, so a kind reads identically in the chart, summary, history and picker. Its hues sit in the shared categorical band (L ~0.74–0.80, C ~0.13–0.16); **brand tide stays out of it**, and the kinds never encode income / expense / status.

> **Stack reality check.** The section stands in for the account-nested `AccountController` term routes — `GET …/terms` (history), `GET …/terms/current` (the summary), `POST …/terms` (New term), `PUT`/`DELETE …/terms/{id}` (inline edit / delete), gated by the new **`accounts.terms.read`** / **`accounts.terms.write`** claims. Backed by `ExistingAccountTerm` / `NewAccountTerm` / `CurrentAccountTerm`, the `TermKind` · `TermValueUnit` · `BillingPeriod` enums, and the in-code eligibility matrix. The prototype keeps the history in `data.js` (`accountTerms`, keyed by account) and resolves current / series / supersession client-side (`OdysseyHelpers.currentTerms` / `termSeries` / `termsForAccount`); wire it to the real endpoints in Blazor. The step chart is dependency-free SVG, themed via the `--chart-*` tokens — sanctioned data viz, not illustration.

## Components — Account custodian

The **custodian** is an account's optional link to the **contact that holds it** — the bank for a bank account, the broker for a brokerage, the card issuer for a card, the provider for a pension. It records *where* an account is held, not just what it is. It reuses the existing **Contact** entity (a nullable `Account.CustodianId` FK) rather than introducing a separate institution concept, so a bank already created for transactions/issuers is reused, not duplicated. It is **fully optional** — cash and property accounts simply have none. Two consumable components back it; specimen: `components/custodian.html`.

**`CustodianChip`** — the read-only display of the link on the account card (the collapsed row, beside the status chip, and the detail metadata grid). It **composes the chip visual language with the canonical `CONTACT_TYPES` registry** — the leading glyph and its color are the contact's *type* icon (read off the registry, never re-hardcoded; FE-4) and are **decorative** (`aria-hidden`). It is **informational, not a link** in v1 (there is no per-contact detail route to navigate to), so it renders as a plain non-focusable `<span>`. Four states: **selected** (name + type label in text), **archived** (muted tone **plus** a visible "(archived)" cue — never tone alone), **no custodian** ("No custodian" in text, a dashed empty chip), and **missing** (a deleted contact reads back as no custodian via the DB's `ON DELETE SET NULL`). All meaning rides in text — the type and archived state are spoken via an `sr-only` accessible name (e.g. *"Custodian: Ally Bank (Institution), archived"*); muted variants hold AA contrast (`text-secondary`, not the disabled alpha). `size` sm (row) / md (detail).

**`CustodianSelect`** — the optional picker on the two account-editing surfaces the product actually has: the **New / Edit account dialog** (create and edit share one `AddAccountModal`). Per spec it is **not a new widget** — it reuses/extends the DS **`Combobox`** (the searchable single-select; the DS equivalent of `OdsCombobox`/`MudAutocomplete`), wrapped in the standard `Field` label / help / error chrome. It lists **active contacts only** (archived are filtered out client-side, so an archived target can't be picked), is **clearable** (clearing removes the link) and **optional**, and deliberately has **no inline create** (the custodian must already exist) and **no `ContactType` restriction** (any contact is eligible). Each option carries its type icon. This is the picker's reuse path — do **not** copy the inaccessible `MudMenu`-hosted contact picker from the transaction dialog.

> **The Combobox gained three additive props for this.** `clearable` (a real keyboard-operable 24px clear button, not a pointer-only ×), per-option `icon`/`iconColor` (a leading type glyph in each row + beside the value), and `loading` (an announced loading row). They're backward-compatible — existing contact/tag comboboxes pass none and are unchanged.

**Accessibility (WCAG 2.2 AA — the feature ships meeting it, not deferring it).** The chip conveys **type and archived state in text** (A11Y-5/6/11), icon decorative. The picker has a persistent associated **"Custodian"** label with the optional state in text (A11Y-1), combobox/listbox/option semantics + full keyboarding inherited from `Combobox` (A11Y-2/3) with a keyboard-operable clear, announced loading/empty/error states (`aria-live` / `role="alert"`) and the empty-list "create a contact first" hint wired to the field (A11Y-4), a visible `:focus-visible` ring (A11Y-8), and inline validation linked via `aria-describedby` + `aria-invalid` on a rejected save (A11Y-9). The create-dialog instance keeps the modal focus-trap + two-level Escape; the inline-edit-grid instance is non-modal (A11Y-10 / A11Y-10b).

> **Stack reality check.** The custodian travels through the **existing** account contract — `CustodianId` (scalar) in the `NewAccount` body on `POST`/`PUT /api/accounts`, and a nested, response-only **`Custodian`** projection on `ExistingAccount` (read). That projection is a *slim* subset of a contact — identifying/display fields only (`ContactId · Name · NormalizedName · Type · OrganizationNumber · Archived`), deliberately **without** the free-text `Description` (data-minimisation: the account read path is gated by `accounts.read`, the description stays reachable only through `contacts.read`). The write DTO accepts **only** the scalar id — the nested object is never model-bound (no over-posting). No new endpoints, no new claims; the picker additionally needs `contacts.read` to list. The prototype models all of this in `data.js`: bank/broker contacts (incl. an archived **Wells Fargo** to exercise the archived chip), a `custodianId` on each account, and `OdysseyData.custodianForAccount(a)` resolving the slim projection (a dangling id reads back as `null`). Reference build: `CustodianChip` + `CustodianSelect` in `ui_kits/web/Accounts.jsx` (row chip · detail tile · inline-edit picker) and `AddAccountModal.jsx` (create-dialog picker).

## Components — Account detail chips & menu conventions

The expanded account record's metadata grid reads as **one coherent chip family** — type, custodian, and status all render as the same pill shell (a leading accent · a bold name · a muted third segment), drawn from the canonical registries. Three consumable components, all siblings:

- **`AccountTypeChip`** — the account **type** as a chip: the type's colored Material glyph + label + a muted **Asset / Liability** group segment (e.g. *Checking · Asset*). Driven by the `ACCOUNT_TYPES` registry, the same source the type picker and row avatar use. `showGroup={false}` drops the group; `size` sm (row) / md (detail).
- **`AccountStatusChip`** — the account **status** as a chip: a tone-colored **dot** (status is a state, not an icon-category) + label + an optional muted **date** segment (e.g. *Open · since Mar 14, 2021*). `tone` maps to the dot color — income (open) · pending (closed) · outline (archived). The sibling structure to the type chip, with a dot where the type has a glyph.
- **`CustodianChip`** — the **custodian** as a chip (documented above): type glyph + name + muted ContactType segment (e.g. *JPMorgan Chase · Institution*).

> **Status reads "Open", and the lifecycle lives in one tile.** An account's non-closed status is labelled **Open** (not "Active") across the row chip, the detail tile, the *Any status* filter, and the page subtitle ("6 open · …"). The detail grid **consolidates** the former separate *Opened* / *Closed* tiles into the single **Status** tile: the status chip plus a lifecycle line that adapts to the state — `Opened {date}` when open, `Opened {date} · Closed {date}` when closed, `Opened {date} · Archived {date}` when archived. The collapsed row keeps the compact chip with the single most-relevant date. (Tags / contacts / currencies / budgets keep their own *Active / Archived* vocabulary — the rename is account-specific.)

**Menu conventions — the `ActionMenu` overflow (`more_vert`).** Two capabilities were added to the shared `ActionMenu`, used by every record table and list row:

- **`trailingIcon`** — a right-aligned Material icon on a menu item, revealed on hover/focus. The house use is the **`content_copy`** affordance on a Copy-ID item.
- **Glyph icons** — an item's `icon` (and the `Modal` lead-tile `icon`) may be a Material Icons ligature **or** any non-ligature character rendered as a typographic glyph. This is how the **Terms** action and dialog carry the **§** section glyph (matching the Terms section's identity), the same technique the `Collapsible` lead uses.

> **One "Copy ID" everywhere.** Every record's overflow menu carries a single standardized copy action: a **`fingerprint`** leading icon, the label **"Copy ID"** verbatim (never "Copy *X* ID"), and a hover-revealed **`content_copy`** trailing icon. This holds across Accounts, Transactions (`TxnTable`), Files (`FilesTable`), Contacts, Currencies, Tags, Exchange rates, Users, Tax statements, and Budgets — the identifier copied differs (id / currency code), the affordance does not.

## Components — Account value estimates

The **Estimates** section (`AccountEstimates.jsx`) is a new zone inside the expanded account record (Accounts → account detail), positioned **above** the Terms section — the **§**-style one-word sibling (the `monitor` glyph) that leads the record. It backs the **AccountEstimate** feature: a time-versioned history of an account's **estimated value** — a single user-supplied money amount, in the account's own currency, effective from a date — for assets the transaction ledger can't represent (a house, a car, a valuable). The latest entry on or before a date is the value **in force** (implicit supersession — no `EffectiveTo`, step function, **identical resolution to `AccountTerm`**). It is deliberately the sibling of Terms so the two read and behave consistently; where it differs, it's because an estimate has no kind / unit / billing dimension — it is always one amount. Specimen: `preview/32-account-estimates.html` (Property with and without transactions + the empty state, chart / history / empty-state tweaks live); reference build: `ui_kits/web/AccountEstimates.jsx` + the `AddEstimateModal.jsx` dialog. Styled by `account-estimates.css` (the only net-new sheet, `.est-*`).

**It reuses the Terms anatomy, simplified to a single value.** Three stacked zones:

| # | Zone | What it shows · Maps to |
|---|---|---|
| 1 | **Value hero** | The current estimated value + a directional **change** chip vs the prior estimate, over a **value chart** of the estimate over time. Estimates hold flat between appraisals and extend to a dashed **Today** marker. The chart reads as a **step** line (discrete appraisals held flat) or a **smooth** value trend — tweakable. |
| 2 | **Current value** | The in-force estimate as the **headline**, with the **transaction balance** kept as a quiet secondary — the one place the "estimate replaces balance in net worth" decision reads. The `GET …/estimates/current` projection. |
| 3 | **History** | The full `GET …/estimates` list, newest first, as a **table** or a vertical **timeline** (tweakable). Each row shows the value, the **change** vs the prior estimate, and a status (**In force** · future-dated **Scheduled** · **Superseded**); editable / deletable in place. |

**The estimate is the headline; the balance stays quiet.** Today an account's `Balance` is the sum of signed transaction amounts — `0` for a Property with no transactions. When a current estimate exists it **replaces** the transaction balance in net worth (the spec's §9 *replace* policy): the section surfaces the estimate as the big mint figure and the transaction balance as a muted secondary, and the collapsed account row shows the estimate as the account's value (labelled **Est. value**), exactly as Terms surfaces the in-force rate. There is no extra "this counts toward net worth" prose — hierarchy carries it, with one quiet **In net worth** chip on the value tile.

**Value is asset worth, so it reads in the income color.** The hero figure, chart line/area, and the current-value tile use **`--finance-income`** (mint) — an estimate is a positive amount of worth, the same encoding the account balance uses when positive. The hero **glyph tile** carries the **account-type** color/icon (a Property estimate reads in the property hue), tying the section to its account. The change indicator is **directional** — mint ↑ for a gain, coral ↓ for a loss — since an asset's value moving up or down is unambiguous (unlike a rate's neutral delta in Terms). Brand **tide** stays out of it.

**Every account type is eligible.** Unlike Terms, estimates are **not** gated by account type — any account may carry one, so there's no eligibility kind-grid. A recommended practical subset (`Property` · `Vehicle` · `OtherAsset` · `InvestmentAccount` · `PensionAccount` — asset accounts whose worth isn't transaction-derived) is highlighted in the dialog and the guided empty state, but never enforced. The **empty state** has two tweakable treatments: a **standard** `EmptyState`, or a **guided** prompt that frames the value proposition and shows the transaction balance the estimate would stand beside.

**New / Edit estimate** (`AddEstimateModal.jsx`) is built on the shared DS **`Modal`**, like every dialog. Its field set mirrors the `NewAccountEstimate` DTO and enforces the spec's rules:

| Field | Maps to | Control · rule |
|---|---|---|
| Estimated value | `Value` | Hero money input. Required, **≥ 0**. |
| Currency | `CurrencyCode` | **Locked to the account currency** (shown read-only); the server rejects a differing currency (`400`). |
| Effective from | `EffectiveFrom` | DateField, required. Past or future allowed (future = Scheduled). An exact `(AccountId, EffectiveFrom)` duplicate is rejected — the server's `409`. |
| Note | `Note` | Optional, ≤ 512 chars. |

> **Stack reality check.** The section stands in for the account-nested `AccountController` estimate routes — `GET …/estimates` (history), `GET …/estimates/current` (the headline), `POST …/estimates` (New estimate), `PUT`/`DELETE …/estimates/{id}` (inline edit / delete), gated by the new **`accounts.estimates.read`** / **`accounts.estimates.write`** claims. Backed by `ExistingAccountEstimate` / `NewAccountEstimate` / `CurrentAccountEstimate` and the `AccountEstimate` entity (a sibling of `AccountTerm`, minus the kind/unit/billing columns). `ExistingAccount` gains a computed `CurrentEstimatedValue` (+ `CurrentEstimatedValueCurrencyCode`), populated server-side like `CurrentInterestRate`, and `AccountTotalsService` folds it into net worth per the replace policy. The prototype keeps the history in `data.js` (`accountEstimates`, keyed by account) and resolves current / series / supersession client-side (`OdysseyHelpers.currentEstimate` / `estimateSeries` / `estimatesForAccount`); wire it to the real endpoints in Blazor. The value chart is dependency-free SVG themed via the `--chart-*` tokens — sanctioned data viz, not illustration.

## Components — Tax Statements page

The **Tax Statements page** (`TaxStatements.jsx`) is the yearly-tax record screen at `/tax-statements` — one expandable record per fiscal year, the sister of the Accounts and Budgets pages. Reference build: `ui_kits/web/TaxStatements.jsx`; template: `templates/tax-statements/`.

**It reuses the record scaffold; its net-new piece is reconciliation.** Header, expandable `.acct-item` rows, metadata wells, and collapsibles are the same atoms documented for Accounts. The defining view is the **reconciliation report** — a table (or tiles, via a tweak) contrasting three columns: **Declared** (figures from the official statement), **Odyssey-derived** (net worth from accounts, advance tax + actual income summed from tagged transactions), and **Variance**. The variance cell reads mint ✓ when reconciled, amber when it differs, disabled when a derived figure is unavailable. Rows group under **Net worth · Income · Tax**, where the Tax group shows assessed tax, advance paid (statement-implied `assessed − settlement` vs. tag-derived), and the settlement.

**The cross-year settlement is the modelling principle.** Advance tax is withheld *within* the income year; the post-assessment settlement (additional tax / refund) is **declared, not derived**, and paid the following year — so it stays out of that year's derived advance-tax figure. This is surfaced in the Tax section of the table, not a separate band.

**It leans on existing systems, not new ones.** The header **Overview** holds three `LineChart`s (net worth · assessed tax · accumulated tax, year over year). Data conditions (account balances not yet synced; off-currency transactions excluded) flow through the **problem/signal system** documented for Accounts — header rollup, row severity chip, and the `acct-problem` fix-it alert. File attach reuses the `AfmUpload` dropzone; tag selection is two `MultiSelect`s edited inline in the record's edit form.

> **Stack reality check.** Mirrors the *Yearly Tax Statement* backend: `TaxStatement` declared figures + `TaxStatementTag` (tax-payment / income roles) + `TaxStatementFile`, with the reconciliation `TaxStatementReport` computed on read. Derived net worth degrades gracefully (`derived.available=false`) when account balances aren't computed.

---

## Reference data — Insurance policy & document types

The **Insurance Policies** feature adds two enums, each with a canonical registry in `OdysseyData` (icon + color + label, same categorical band as the others) and a typed picker in `/components`.

**`InsurancePolicyType`** (`OdysseyData.insurancePolicyTypes`; picker `InsurancePolicyTypeSelect`, registry export `INSURANCE_POLICY_TYPES`) — twelve members, `Other` last: **Home** (`house`) · **Contents** (`chair`) · **Building** (`apartment`) · **Vehicle** (`directions_car`) · **Travel** (`flight`) · **Life** (`favorite`) · **Health** (`health_and_safety`) · **Accident** (`personal_injury`) · **Liability** (`gavel`) · **Pet** (`pets`) · **Property** (`home_work`) · **Other** (`shield`). It drives the policy's leading avatar and the *Any type* filter.

**`PolicyFileType`** (`OdysseyData.policyFileTypes`; pickers `PolicyFileTypeSelect` / `PolicyFileTypeMultiSelect`, registry export `POLICY_FILE_TYPES`) — the documents that attach to a policy **and** to a renewal: **Contract** (`history_edu`) · **Invoice** (`receipt`) · **Terms & conditions** (`menu_book`) · **Policy document** (`shield`, the indigo headline doc) · **Claim document** (`assignment_late`) · **Other** (`insert_drive_file`).

**`CoverageStatusChip`** (registry export `COVERAGE_STATUSES`) renders the **derived** coverage status — the Insurance sibling of `AccountStatusChip`. The status meaning lives in the **visible text label** (the leading dot/icon is `aria-hidden`), on existing finance accents: **Active** = income mint · **Expiring soon** = pending amber · **Lapsed** = expense coral · **Upcoming** = info sea · **No coverage** = neutral outline. An optional muted `detail` segment carries the day count ("12 days left"). Brand tide never encodes status.

---

## Components — Insurance Policies page

The **Insurance page** (`Insurance.jsx`) is the insurance-portfolio screen at `/insurance` — one expandable record per policy, the sister of Accounts / Budgets / Tax Statements. Reference build: `ui_kits/web/Insurance.jsx` (seed + helpers in `ui_kits/web/insurance-data.js`, styles in `ui_kits/web/insurance.css`); template: `templates/insurance/`.

**It reuses the record scaffold; its net-new pieces are coverage status and the renewal history.** Header, expandable `.acct-item` rows, collapsibles, the `AfmUpload` dropzone and the problem/signal system are all atoms documented for Accounts. A policy carries an ordered history of **renewal periods** (premium · coverage · validity window), and its **coverage status** + **current renewal** are *derived, never stored* — computed by the ordered rule in `insurance-data.js` (`insCoverageStatus` / `insCurrentRenewal`) against one request "today", with the latest-`FromDate` / latest-`CreatedAtUtc` overlap tie-break. The collapsed row's headline figure is the **coverage end date + a days-remaining word** (expires in N days / lapsed N days ago / starts in N days).

**The renewal history follows the Terms pattern** (Accounts → account row → Terms): a **Current period** summary (premium · coverage · renews · documents) over a status'd **history table** — period · premium · coverage · status · **docs** · inline edit/delete — with **In force / Upcoming / Past** pills, the direct analog of the AccountTerms section. Each history row's **Docs** chip expands an inline panel showing that renewal's attached files (a `FilesTable`) with its own **Attach** action, so renewal-level documents are visible and managed in place — not just counted. The section leads with a **Premium** trend chart. The feature accent is **indigo** (oklch hue 282) — distinct from brand tide and from Tax's magenta.

**Policy facts read as chip tiles.** Below the row, the scalar policy fields (policy number, type, insurer, insured account, total premium, notes) render as the DS **`InfoTile`** — an icon-chip + label + value + foot tile — with the **Type** tile carrying its own policy-type icon and color, and the grid re-tinting the other chips to the feature's indigo via `--odc-infotile-accent`. Directly under them sits the policy-level **Current period** snapshot (Premium · Coverage · Renews), also `InfoTile`s, lifted out of the history section so current state reads as a policy fact, not history.

**The portfolio summary** rides in the header **Overview**: total policies, **counts-by-status pills**, and **current premium / coverage rolled up per currency** (the current renewal only). When a base currency is chosen (the `summaryBaseCurrency` tweak, sourced from the display-currency preference) it shows a converted total and lists any currency lacking a direct rate under an *excluded — no rate* note (never silently zeroed). The header **signal** rolls up policies that are *Expiring soon* (warning) or *Lapsed* (error) so the renewal cliff is never missed; clicking a signal row jumps to the policy.

**Pickers reuse the accessible `Combobox`.** The insurer (required) and insured-account (optional, clearable) selectors are the DS `Combobox` — never a bespoke popover — fed scalar-id options with the contact/account type glyph. Create/edit pass **scalar ids only** (no nested entities), matching the spec's mass-assignment invariant. **A document's only home is a renewal period** — the upload dialog is always scoped to one. Opened from a period's own document panel the target is **fixed** and reads as an *Attaching to* line; opened from the row menu it is a **period picker defaulted to the resolved target** (the current period, else the latest-ending one), so a late-arriving document can still be filed against an earlier period. With no period there is nowhere to file a document, so the row menu's **Attach document** is `aria-disabled` with the reason *Add a renewal period first.* as its `aria-describedby` note — reachable by keyboard, never the dimmed state alone — and the renewal-history empty state carries the **New renewal period** button that unblocks it. Attach counts and the enable transition are announced through a polite live region.

> **Stack reality check.** Mirrors the *Insurance Policies* backend: `InsurancePolicy` + ordered `PolicyRenewal`s + `PolicyRenewalFile` (the sole insurance-document join — policy-level attachments are gone), with `CoverageStatus`, `currentRenewal` and the portfolio summary computed on read against a single request clock. Read responses expose **minimal `{id,name,type}` projections** for the insurer and insured account (not the full contact/account record). Dialogs map to `NewInsurancePolicy` / `NewPolicyRenewal` / `AttachInsurancePolicyFileRequest` (the surviving per-period endpoint's body); the prototype computes status, current-renewal and the multi-currency summary client-side in `insurance-data.js`. A policy's document count is the sum across its periods.

---

## Reference data — Contract & document types

The **Contracts** feature adds two enums, each with a canonical registry in `OdysseyData` (icon + color + label, same categorical band as the others).

**`ContractType`** (`OdysseyData.contractTypes`, helper `contractTypeInfo`; DS picker **`ContractTypeSelect`**, registry export `CONTRACT_TYPES`) — four members, `Other` last: **Employment** (`work`, blue) · **Service** (`home_repair_service`, teal-green) · **Rental** (`cottage`, amber) · **Other** (`description`, neutral). It drives the contract's leading avatar and the *Any type* filter; the create + inline-edit forms pick it via `ContractTypeSelect` (delegates to the shared `TypeSelect`).

**`ContractFileType`** (`OdysseyData.contractFileTypes`, helper `contractFileTypeInfo`) — the documents that **upload** to a contract: **Signed** (`history_edu`, violet — the executed agreement of record) · **Amendment** (`edit_document`, amber) · **Correspondence** (`forum`, blue) · **Other** (`insert_drive_file`, neutral). Documents are uploaded straight from the user's machine through the shared `AfmUpload` dropzone; each becomes a `ContractFile` carrying its own name + size.

**`ContractStatusChip`** (in `Contracts.jsx`, a `Chip` preset) renders the **derived** status — the Contracts sibling of `CoverageStatusChip`. The status meaning lives in the **visible text label** (the leading dot/icon is `aria-hidden`), on existing finance accents: **Active** = income mint · **Upcoming** = info sea · **Expired** = expense coral · **Archived** = neutral outline. Brand tide never encodes status.

---

## Components — Contracts page

The **Contracts page** (`Contracts.jsx`) is the household-agreements screen at `/contracts` — one expandable record per contract, the sister of Accounts / Insurance / Tax Statements. Reference build: `ui_kits/web/Contracts.jsx` (seed + helpers in `ui_kits/web/contracts-data.js`, styles in `ui_kits/web/contracts.css`); page specimen: `templates/contracts`.

**It reuses the record scaffold; its net-new pieces are derived status, the one-of-two parties list, and the document references.** Header, expandable `.acct-item` rows, collapsibles, `InfoTile`s and the problem/signal system are all atoms documented for Accounts / Insurance. A contract is a **single active period** (start + optional end) — there is no renewal sub-history (v1 non-goal). Its **status** is *derived, never stored* — computed by the ordered rule in `contracts-data.js` (`conStatus`): **Archived → Upcoming** (start in future) **→ Expired** (end in past) **→ Active**, against one request "today". The collapsed row's headline is the period anchor + a relative-days word (ends in N days / starts in N days / expired N days ago / open-ended). The feature accent is a warm **clay/bronze** (oklch hue 42) — distinct from brand tide, Insurance's indigo, and Tax's magenta; it evokes a signed agreement and never encodes money.

**Parties are a one-of-two polymorphic link.** Each party row links exactly one of an **Account**, an **Institution** (a `Contact` — the user-facing label is "Institution", per the spec's §3 decision), or an **Insurance policy**. Rows render the **minimal projection** only — kind icon + display name + a kind pill + the target's own type — never the fuller cross-claim record. `conResolveParty` resolves a party's scalar id to that projection.

**The party picker reuses the accessible `Combobox`; documents are uploaded.** The add-party dialog is a two-step flow: a **party-kind** segmented selector (Account / Institution) then a type-to-filter `Combobox` whose options are **pre-loaded** for the chosen kind (no in-widget async fetch — the candidate lists are handed in up front, keeping it inside `Combobox`'s accessible contract). The inaccessible `CreateTransactionDialog` popover is deliberately not used. Parties are created from **scalar ids only** (no nested entities — the §6/§10 mass-assignment invariant), and already-linked targets are filtered out so a duplicate can't be submitted. **Documents are uploaded**, not picked from a library — the attach dialog reuses the shared `AfmUpload` dropzone (the same drag-drop/browse control as the Files page and account uploads) with the `ContractFileType` vocabulary, so every upload surface in Odyssey behaves identically; each uploaded file becomes a `ContractFile` tagged Signed / Amendment / Correspondence / Other.

**Archive is a reversible action, distinct from delete.** The row's **action menu** carries **Archive / Restore** — archiving stamps `Archived`, restoring clears it; either way parties and documents are kept. Archived contracts are **hidden from the default list** and reachable only by adding *Archived* to the status filter; the hard **Delete** is a separate, irreversible menu row. Both meanings are always conveyed as text.

**The page summary** rides in the header **Overview**: active vs. total + archived counts, currently-active vs. upcoming/expired, a **by-type** breakdown, and **counts-by-status pills**. The header **signal** rolls up Active contracts whose term ends within the *ending-soon* window (the `endingWindowDays` tweak) so a renewal cliff is never missed; clicking a signal row jumps to the contract.

> **Stack reality check.** Mirrors the *Contracts* backend (Draft v4): `Contract` + `ContractParty` (XOR one-of-two) + `ContractFile` (a `FileMetadata` reference), with `Status` derived on read against a single request clock. Read responses expose **minimal `{id,name,type}` projections** for each party target (not the full account/contact/policy record). Dialogs map to `NewContract` / the add-party + attach-file requests; archive is the `IsArchived` flag on the update DTO. The prototype computes status, the parties projection and the summary client-side in `contracts-data.js`.

---

## Reference data — Billing interval

The **Subscriptions** feature adds one enum with a canonical registry in `OdysseyData` (icon + color + label, same categorical band as the others) and typed pickers in `/components`.

**`BillingInterval`** (`OdysseyData.billingIntervals`, helper `subIntervalInfo`; pickers `BillingIntervalSelect` / `BillingIntervalMultiSelect`, registry export `BILLING_INTERVALS`) — four members in the enum's numeric order (which is also how the list sorts by "Frequency"): **Daily** (`today`, cyan) · **Weekly** (`view_week`, teal) · **Monthly** (`calendar_month`, blue — the DTO default) · **Yearly** (`event_repeat`, violet). Brand tide stays out of the ramp.

**`BillingIntervalChip`** renders a subscription's cadence — the interval glyph + label + the **derived** per-cycle billing anchor as a muted trailing segment: "Monthly · day 15", "Yearly · 15 Jan", "Weekly · Wed", "Daily" (no anchor). It also honours the **`intervalCount`** multiplier (the `count` prop): count 1 shows the plain label, count > 1 shows "Every N months / years / weeks / days" (helper `billingIntervalLabel`, mirrored by `subIntervalLabel`). The anchor is computed from `firstBillingDate` + `interval` at render time, **never stored** (helper `subBillingAnchor`).

**`SubscriptionStatusChip`** (registry export `SUBSCRIPTION_STATES`) renders a subscription's lifecycle states — **Paused** (pending/amber, stored flag) · **Ended** (expense/coral, **derived** from `endDate`) · **Archived** (neutral outline, stored flag) — one chip per active state, with an optional Active chip when none is set. Ended **supersedes** Paused (a pause is moot once the term is over); Archived stacks after either. The state meaning lives in the **visible text label**, never colour alone — the Subscriptions sibling of `CoverageStatusChip`.

---

## Components — Journal module (Journal + Tasks)

The **Journal module** adds two shared surfaces reachable from a new **Journal** nav module (icon `menu_book`): a **Journal** (`/journal`) of dated narrative entries and a **Tasks** (`/tasks`) to-do list on a kanban board. Both are shared across all users (no per-user private journal); every entry/task records its **author** for display only (not an access boundary). Reference builds: `ui_kits/web/Journal.jsx` · `Tasks.jsx` (+ `journal-data.js`, `journal.css`); the module is wired into `AppShell.jsx`, whose switcher now **drops any module with zero viewable pages** (a Guest holding none of the Journal claims sees no Journal module at all). The **Contacts** page (`/contacts`) now also lives **under the Journal nav module** — relocated from its former standalone Contacts module (its route key and page build are unchanged) — so the module's page rail reads Journal · Calendar · Photos · Albums · Tasks · **Contacts**, plus the journal / task / photo tag pages.

**Journal page** — the sister of Subscriptions/Contracts on the expandable **record-card** scaffold (`.acct-list` / `.acct-item`), reverse-chron by entry date. Each card shows a `menu_book` `Avatar`, the title (with an Archived chip when archived), a tag line of entry date · author · location, a two-line content snippet, tag chips, and **text-labelled** photo / file / contact **count indicators**. Expanding reveals full plain-text content, a `MetaTile` grid (entry date, location, written-by, last-edited, tags, linked contacts), a **`JournalPhotoGallery`**, and an attachment list; **Edit entry** now opens the **New / Edit entry** dialog (`AddJournalEntryModal`) reused in edit mode (`Save changes`), rather than an inline panel. The create and edit dialog both carry Title, `NoteField` content, entry date, location, a `TagMultiSelect` for tags, a `TagMultiSelect`-based multi-**contact** picker, and `FileUpload` for photos + attachments. Entries link contacts/files by **id only**; the client hydrates names, and a since-deleted / no-access contact renders a muted, text-labelled **“Unavailable”** chip (never errors the entry). All free text renders **escaped as plain text** — no Markdown/HTML.

**Tasks page** — a **Board** (default) / **List** view toggle (`SegmentedControl`). The board is **`TaskBoard`**: three landmark columns (Backlog · Doing · Done) with **dual-path** moves — drag a card, or use its keyboard move buttons (up / down / previous-column / next-column) — every move announced via a polite live region; moving to Done stamps `CompletedAt`, moving out clears it. Archived tasks are **off-board**, shown in a muted section only when the status filter includes Archived. The list view is a flat, status-`Select`-per-row rendering. Both views carry search (Title + Content), tag and status `MultiSelect` filters, and the **New task** dialog (`Create task`: Title, `NoteField` content, optional deadline, status, `TagMultiSelect` tags, `FileUpload` attachments). Deadlines render as a chip toned by urgency (overdue = expense, ≤3 days = pending) with the meaning in text.

The three composites — **`TodoStatusChip`** (registry export `TODO_STATUSES`), **`JournalPhotoGallery`**, and **`TaskBoard`** — ship as typed `/components` (specimen `components/journal-module.html`). Tags on both surfaces reuse the accessible **`TagMultiSelect`**; the multi-contact picker reuses it too (no new bespoke combobox). Photos are limited to browser-renderable image types (JPEG/PNG/GIF/WebP; HEIC/HEIF excluded in v1).

**Task status is derived, not stored.** A task carries no status enum — it has three nullable datetimes: **`StartedAt`**, **`CompletedAt`**, and **`Archived`** (the last mirrors how every other entity archives). `OdysseyHelpers.taskStatus(t)` maps them to a kanban status with precedence **Archived → Done → Doing → Backlog** (Backlog = all null, the starting state); `position` is the per-column display order. The **search API filters by (derived) status**, but **create / edit / update take the new datamodel** — the client keeps a semantic Status control (dropdown, board columns) and translates the chosen value to a datetime patch at the write boundary via `OdysseyHelpers.taskStatusPatch` (Doing stamps `StartedAt`; Done stamps `CompletedAt`; Backlog clears the progress stamps; Archived sets `Archived` but preserves the others, so **unarchiving restores the prior status** — a done task returns to Done, not Backlog). `TodoStatusChip` and `TaskBoard` stay presentational — the kit passes them the derived status.

---

## Components — Calendar module (Calendar)

The **Calendar module** adds a shared household **Calendar** (`/calendar`) as a new page under the existing **Journal** nav module (icon `calendar_month`, next to Journal and Tasks), gated by `calendar.read`. Like every other module it is **shared, not per-user**. Reference build: `ui_kits/web/Calendar.jsx` (+ `calendar-data.js`, `calendar.css`, and the `AddCalendarEventModal` / `ManageCalendarsModal` dialogs), wired into `AppShell.jsx`. Specimens: `templates/calendar` (the page) and `preview/51`–`74` (the dialogs).

**Calendars & colour.** Events live on one or more named, colour-coded **calendars**. `Calendar.Color` is chosen from a **curated, contrast-vetted swatch palette** via **`ColorSwatchSelect`**, never a free hex/HSV picker — which removes the need for a heavy colour-picker component *and* guarantees every chip's title text clears WCAG 1.4.3, because each swatch ships a **baked foreground**. The palette (registry export `CALENDAR_SWATCHES`, lookup `swatchFor(hex)`) is re-mapped onto the Odyssey ramps — **sea · tide · mint · coral · violet · amber · ink** — rather than the spec's generic Material hues, so a calendar sits in the same colour world as the rest of the product; brand tide is used only in its deep stop, well clear of the bright chrome primary. **Deviation note:** the spec's eight Material swatches (`#1976D2`, …) are intentionally *not* used verbatim — the stored value stays a hex string, but the chooser constrains it to the Odyssey-derived set.

**Views.** A `SegmentedControl` switches **Month · Week · Day · Agenda**. **Month** is the DS **`CalendarGrid`**: a `role="grid"` of day cells with colour-coded chips — all-day events render above timed ones and, when multi-day, repeat a square-edged strip across the days they cover so they read as one continuous bar (exclusive-end: the end-midnight day is unpainted); timed chips carry a tabular time; dense days collapse the overflow into a **“+N more”** popover listing the full day. **Week/Day** are a time-grid (`CalTimeGrid`): an all-day lane over an hourly body with events positioned by start/end and split into columns when they overlap. **Agenda** is a chronological list grouped by day. Like the other list pages, the **page header carries a Search region** — an event title/location `SearchField` plus a **calendar filter** `MultiSelect` (all calendars shown by default); **Manage calendars** is a header action.

**Drag-and-drop reschedule.** Events can be dragged in place: in the **month** grid, drag a chip onto another day to move it (whole-day shift, duration preserved — `CalendarGrid`'s `onEventDrop(id, toDate, fromDate)`); in the **week/day** time-grid, drag an event block to another time/day, or drag its bottom edge to change the end time (15-minute snapping, a live ghost preview). A plain click still opens the edit dialog; keyboard users reschedule via that dialog.

**Time entry.** Timed events use **`TimeField`** (24-hour `HH:mm`, tabular monospace) for start/end — the net-new timed sibling of `DateField`, since `DatePicker`/`DateField` only bind date granularity. All-day events swap the two time inputs for two `DateField` day pickers (start day + **inclusive** end day; the client converts the inclusive UI end to the exclusive midnight boundary the API stores).

**Events & recurrence.** **`AddCalendarEventModal`** is one shell with three modes: **create** (the *Does not repeat / Repeats* toggle is editable; “Repeats” reveals the full rule builder — frequency, interval, weekly day-chips, monthly/yearly day-of-month, and the **required** *Ends* choice: On date / After N — there is no “never” option, mirroring the bounded-only rule), **edit** (a single occurrence; the repeat toggle is **read-only** — the Repeats choice is fixed at creation), and **series** (the pattern's template/rule, reached via the **“Edit series…”** link an occurrence shows). The toggle + rule builder are one **`RevealPanel`** — the toggle is the header of a bordered surface and the rule fields attach beneath a divider, so the choice and the fields it controls read as a single connected control. Recurring events are **eagerly materialized** into individually-editable occurrence rows (each carrying its `patternId`), exactly as the API does; the kit's `calGenerateOccurrences` reproduces the Daily/Weekly/Monthly/Yearly generation (with end-of-month clamping). Editing/deleting an occurrence touches that one row; editing a series regenerates **future** occurrences only; deleting a series removes future rows and keeps past ones. The footer names the route each submit maps to (`POST /api/calendar-events` vs `POST /api/recurrence-patterns`, etc.).

**Manage calendars.** **`ManageCalendarsModal`** is CRUD over the calendars (name, description, `ColorSwatchSelect`), with per-row inline edit and an add row. Deleting a calendar that still holds events is **blocked** (the service returns `409`) — the delete control is disabled with the reason, mirroring Journal's restrict-while-in-use philosophy. A duplicate name (case-insensitive) is rejected in-dialog as a `409`.

**Keyboard & ARIA.** The month grid is a roving-tabindex grid — arrow keys move day focus, `Home`/`End` jump to week ends, `Enter`/`Space` opens a busy day's popover or starts a new event on an empty day; today carries `aria-current="date"`. Each event chip is an independently focusable button whose accessible name is the full sentence (“Design review, 13:00, Work calendar”, or “Public holiday, all day, Work calendar”), not the truncated visual title. **`DatePicker` / `DateField` and `TimeField` are keyboard-navigable inside dialogs.** Both are body-portaled, so React's *delegated* `onKeyDown` never fires when they open inside a Modal (the create/edit event dialog is one); each therefore binds a **native `keydown` listener** — the DatePicker drives grid navigation (arrows / `Home`/`End` / `PageUp`/`PageDown` / `Enter`·`Space` / `Esc`) from a document-level capture listener while open, with the roving highlight shown via a `.kbd` class on the focused day (not `:focus`, which the portal won't hold); the TimeField opens on click/type/ArrowDown (never bare focus), navigates its list with ↑/↓ · Home/End · Enter, and `Space` opens/selects rather than typing a space that would wipe a Tab-selected value. Both mirror their focus/highlight state through refs so a fast Arrow→Enter commits the moved-to value, not the pre-move one.

---

## Components — Import & export (vCard / iCalendar)

Four **productivity surfaces** — **Contacts**, **Tasks**, **Journal**, and **Calendar** — carry a shared **import / export** family, all following one interaction pattern so the product feels consistent across them. Reference builds: the new `ContactImportModal.jsx`, `ImportTasksModal.jsx`, `ImportJournalEntriesModal.jsx`, and `ExportCalendarEventsModal.jsx` (+ the export/import helpers folded into `Contacts.jsx`, `Tasks.jsx`, `Journal.jsx`, and `calendar-data.js` / `Calendar.jsx`). Templates load the matching modal alongside their page (`templates/kit-app.js` for Contacts; the inline loaders in the Tasks / Journal / Calendar templates).

**Entry points — one pattern.** Every page exposes bulk import/export from the **page-header overflow (`⋯`) menu**, never as standalone header buttons: **Export all**, **Export filtered (N)** (always visible — with no filters active it exports the same set as *all*), and **Import from …** (shown only when the caller holds the create **and** update claims for that entity). Per-record export lives in each row / card **action menu** (`Export vCard` / `Export as iCalendar` / `Export VJOURNAL`), and the Calendar event dialog carries an **Export** text action. On Calendar, **Manage calendars** and **Import from file…** also live in the `⋯` menu, leaving only **New event** as the visible primary.

**Formats.** Each surface serializes to the standard its records map onto — **Contacts → vCard 4.0 (`.vcf`, RFC 6350)**, **Tasks → iCalendar VTODO (`.ics`)**, **Journal → iCalendar VJOURNAL (`.ics`)**, **Calendar → iCalendar VEVENT (`.ics`)** — with correct line folding (75-octet), text escaping, and stable per-record `UID`s. Odyssey-specific fields ride as `X-ODYSSEY-*` extensions (e.g. `X-ODYSSEY-CONTACT` on a journal entry, emitted only with `contacts.read`); attachments/photos reference `odyssey-file:` / `odyssey-photo:` URIs.

**Import — UID-matched update-in-place.** Import is a **file → parse → apply → summarise** flow in a four-state dialog: **compose** (a single-file picker, the DS `FileUpload` with `multiple=false`), **in-flight** (spinner), **rejected** (an envelope-level failure — wrong extension/type, over the size or count cap, unparseable — shown inline as an `Alert` before any record is touched), and **result**. Entries whose `UID` matches an existing record are **updated in place** (re-importing a file you exported from Odyssey is idempotent, not duplicative); the rest are created; invalid entries are **skipped with a reason** while the rest of the file still imports. The result summary shows **created / updated / skipped** as plain counts, with the skipped set grouped by reason and expandable to a capped list (100) of sample names/titles. Surfaces with linked sub-objects add **link-level skip tallies** — Tasks reports unresolved tag / attachment links; Journal reports the four-way tag / contact / attachment / photo link skips (an unreadable-contact reference is skipped when the caller lacks `contacts.read`). Journal's import dialog also carries a persistent **destructive-replace warning**: a UID match **replaces** an entry's tags, linked contacts, and attachments/photos rather than merging them.

**Export — scope choices.** Bulk export takes a **scope**: *all* (every record the caller can read, bounded by the per-format entry cap) or *filtered* (the current search / type / status / date set — Calendar's filtered export prefills From/To from the on-screen period and a ≤ 92-day span). Exporting a **single recurring calendar event** first asks **occurrence vs. series** via a compact radio-card dialog (`ExportEventScopeModal`) with one **Export** button — a standalone event downloads immediately. A whole recurring series collapses to one `RRULE` VEVENT only when the entire materialized series is in the set; a filtered subset exports per-occurrence. Success and cap/again failures surface as **toasts**.

**Tweaks.** Contacts, Tasks, and Journal each expose demo tweaks for the edge states — the import claim on/off, an export-cap-exceeded simulation, and the import outcome (all-clean / with-skips / file-rejected); Journal adds a `contacts.read` toggle that drives the contact-link skip count.

---

The **Subscriptions page** (`Subscriptions.jsx`) is a manual list of recurring subscriptions at `/subscriptions` — the sister of Contracts: the same PageHeader + expandable **record-card** scaffold (`.acct-list` / `.acct-item`), not the flat table. It is a pure record-keeping list: subscriptions **do not** generate transactions, post to accounts, or schedule anything. Reference build: `ui_kits/web/Subscriptions.jsx` (+ `AddSubscriptionModal.jsx`, `subscriptions-data.js`, `subscriptions.css`).

**Each card** shows the interval-colored `Avatar`, the name with a **`SubscriptionStatusChip`** (Paused / Ended / Archived), and a tag line of external id · company (the data-minimised `{id,name,type}` contact projection, or “No company”) · cadence (the "every N" interval label + derived anchor, e.g. "Every 2 months · day 1"). The right-hand figure is the price with a *per month* / *every N months* caption; the row's `ActionMenu` carries Edit / Pause·Resume / **End subscription** / Copy ID / Archive·Restore / Delete (Pause and End are hidden once the subscription has ended). Expanding reveals a `MetaTile` grid over every field — including a derived **Next billing** date (the next on/after today, stepping by the `intervalCount`, with a relative word; "Paused" / "Ended" / "Archived" / "No further billing" when there is nothing to bill); inline edit reuses the create form's controls (Paused / Archived / end date via **End** are managed from the action menu, not the form — the end date is also editable directly). Archived rows dim (via the shared `.acct-item.dimmed`), with the Archived chip as the primary text cue.

**Lifecycle states.** **Paused** = still tracked and visible in the default list, flagged as temporarily not billing; **Ended** = a **derived** terminal state (its `endDate` is set and on/before today — `endDate ≤ today`), no longer billing; **Archived** = hidden from the default (Active) list, reachable via the status filter. Paused and Archived are orthogonal stored flags set by a boolean toggle (the service owns the timestamp); Ended is never stored — it falls out of the `endDate`. The **End subscription** action sets `endDate` to today so the row reads Ended immediately and drops out of all billing derivations (next-billing, run-rate); the end date can equally be set/cleared from the edit form. Row actions offer Pause / Resume, End, and Archive / Unarchive directly.

**Filters + sort.** Search spans name / external id / company name; the **interval** filter is a `BillingIntervalMultiSelect` and the **status** filter is a `MultiSelect` (Active / Paused / Archived — same treatment as the Contracts page; archived rows stay hidden until "Archived" is picked), with the curated `SortSelect` sorting Name / Price / Start date / Frequency (the interval's numeric enum order) via the shared `SortHelpers.sortRows`.

**Overview + upcoming renewals.** The header **Overview** leads with two **run-rate stat tiles** — Monthly and Yearly — in the same elevated `InfoTile` treatment as the Insurance detail's "Total premium" tile; each shows the blended **base-currency** total (converted via the stored exchange rates, hopping through USD), in the finance-expense hue, with the Monthly tile captioned by the **largest single cost driver**. A **By currency** caption keeps the un-converted per-currency amounts visible (cadence normalized daily/weekly/yearly → monthly & yearly; paused / archived / ended subs excluded), and the **by-interval** / **by-status** breakdowns follow. The header **signal** panel lists the soonest **upcoming renewals** (each subscription's *derived* next-billing date within a 45-day window); clicking a row jumps to and opens that card. Next-billing is derived from `firstBillingDate` + `interval` (month/year steps clamp to month length), never stored.

> **Stack reality check.** Mirrors the *Subscriptions* backend: a single `Subscription` entity (amount + currency + interval directly on the row — no child collection, no derived-status engine), with `BillingInterval` (Daily/Weekly/Monthly/Yearly) plus an integer **`IntervalCount`** multiplier ("every N", default 1), an optional `ExternalId`, an optional scalar `ContactId` (minimal read projection), a required `FirstBillingDate` anchor, and independent nullable `Paused` / `Archived` stamps. Dialogs map to `NewSubscription` / `UpdateSubscription`; the derived billing anchor and the summary are computed client-side in `subscriptions-data.js`.

---

## Components — Transactions page

The **Transactions page** (`Transactions.jsx`) is the ledger screen at `/transactions` — every transaction across every account in one searchable, sortable table. Sister to the Accounts and Budgets pages, it invents almost no new chrome: the **Page header** over a data table whose rows follow the same **expand → detail → edit** lifecycle as every other record. Specimen: `templates/transactions` (the whole page, one row expanded, static), with `components/txntable.html` for the row anatomy and states; reference build: `ui_kits/web/Transactions.jsx`.

**One table, four homes.** The page's defining fact is that its table is the consumable DS **`TxnTable`** (`components/TxnTable.jsx` — see *Components — data table* above). The Accounts page embeds the very same table inside a single account's detail (passing `hideAccount` to drop the Account column), Budgets renders it for a budget's matched transactions, and the Dashboard for the recent list; the Transactions page renders it unfiltered by account and in full — **no pagination in the MVP, the filtered list renders whole**. Fix the table once and every surface updates.

**The header.** A Page header whose **Search** region holds everything: a debounced (~300ms) query over description · contact · amount, plus four `MultiSelect` filters — account, status, tag, direction. The sub-line is a running tally of the filtered set (`248 transactions · in $24,310.00 · out $9,884.20`). There is **no Overview or Problems region** — a transaction is a leaf record, with nothing to roll up. The primary is **New transaction**, which opens `AddTransactionModal` (pre-filled with the account when a single one is filtered).

**The table.** Eight columns, all sortable — Description (the title), Contact, Account, Tags, Status, Amount, Date (the default sort, newest first) — plus a row-actions cell (overflow menu + disclosure). A direction-tinted **type avatar** leads each row: coral `shopping_cart` for money out, mint `arrow_downward` for money in. The amount is monospace, right-aligned and signed in the direction colour. The **Tags** cell renders the transaction's tag set via `TagChips` (capped at two chips + a `+N`). A row expands in place into a read-only **detail** — a metadata grid (ID · account · contact · tags · status · direction · amount · date · currency, plus optional status comment and external / internal IDs) over a **Files** collapsible — exactly like a budget. The **Tag** filter matches a transaction carrying **any** of the selected tags.

**Status & direction.** Two vocabularies, both on existing finance accents so no new hue enters. **Status** (`TransactionStatus`): **New** (informational sea) · **Approved** (income mint) · **Flagged** (expense coral). **Direction**: **Money in** (mint) vs **Money out** (coral), reused on the avatar and the amount. Brand tide never encodes money.

**Edit opens the New / Edit transaction dialog.** Selecting **Edit** opens `AddTransactionModal` in edit mode (the row never navigates and no longer swaps to an inline panel) — pre-filled with the transaction's values and its existing attachments. Every field maps to a `NewTransaction` field, and Save commits a patch via the table's `onSave(id, patch)`. Because the table owns the dialog, all four `TxnTable` homes (Transactions page, Accounts detail, Budgets, Dashboard) get the same edit surface:

| Field | Maps to | Control |
|---|---|---|
| Date | `TimeStamp` | Date picker — the default sort key, newest first. |
| Description | `Description` | Text; its leading segment is read as the contact label. |
| Contact | `ContactId` | Combobox — search an existing contact or create one inline. |
| Account | `AccountId` | Select across the user's accounts. |
| Tags | `TransactionTagIds` | `TagMultiSelect` — zero, one, or many tags (search / check / create inline). |
| Status | `Status` (`TransactionStatus`) | New / Approved / Flagged. |
| Direction | sign of `Amount` | Money in / Money out — re-signs the amount. |
| Amount | `Amount` (signed) | Number, coloured by sign; stored signed. |
| Currency | `CurrencyCode` | ISO-4217, `"USD"` default. |
| Files | attached `AccountFile`s | Existing attachments (deletable) + the upload dropzone. |
| Status comment · External / Internal ID · Extra data | `StatusComment` · `ExternalId` · `InternalId` · `ExtraData` | Optional — behind the advanced disclosure. |

**Lifecycle.** Accordion-style: opening a row collapses the others — except a row mid-edit, which stays open until Save or Cancel. The row menu carries View details · Edit · Copy transaction ID · — · Delete. New rows always post as `Status = New`.

> **Stack reality check.** The list is `Transactions.razor` (`/transactions`), standing in for MudBlazor's `MudTable` + `MudTableSortLabel` (+ the shared **`Pager`** / `MudBlazor` button pager once the page moves to the server-paged contract — see *Components — server pagination* below; the interim build renders the filtered window whole), driven by the `Odyssey.Finance` transaction DTOs (`Transaction`, `NewTransaction`, `TransactionStatus`). `Amount` is stored signed and `CurrencyCode` as ISO-4217 (`"USD"` default). The contact combobox and tag picker read `GET /api/contacts` and `/api/transaction-tags`; the list is `GET /api/transactions` (searched/filtered/sorted), create `POST /api/transactions`, edit `PATCH /api/transactions/{id}`, delete `DELETE /api/transactions/{id}`. The prototype computes the running in / out tally and the shared-table view state client-side in `Transactions.jsx`.

> **Multi-tag (many-to-many).** A transaction carries a **set** of `TransactionTag`s, not one — `NewTransaction.TransactionTagIds` (a `Guid[]`) on the way in, `ExistingTransaction.TransactionTags` (a list) on the way out, backed by a `TransactionTagLink` join table. The whole frontend is wired for it: data rows use `tags: string[]` (the legacy single `tag` still reads through `OdysseyData.txnTagIds` / `txnTags` for back-compat); the ledger Tag column + detail tile render the set via `TagChips`; the create dialog and inline edit use `TagMultiSelect`; the header Tag filter is *any-of*; search matches across all of a transaction's tag names; and the Analyze-file import carries `TransactionTagIds`. Budget matching reads the set too — a budget claims a transaction if it carries **any** of the budget's item tags (de-duplicated to one row), and a transaction tagged with two of a budget's item tags counts under **each** item, so per-tag buckets can sum past the de-duplicated transaction total (no amount splitting in v1). **Budget items themselves stay single-tag** (`BudgetItem.TransactionTagId` is unchanged) — only transactions became multi-tag.

## Components — File viewer

The **File viewer** (`FileViewerModal`) opens from the **Preview** action in the file row action menu — on the per-account Files list (Accounts → account detail) and the flat Files page. It previews a stored `AccountFile` in place without leaving the app. Specimen: `preview/29-components-file-viewer.html`; reference build: `ui_kits/web/FileViewerModal.jsx`.

**Anatomy.** A 12px-radius dialog (the modal radius) on the standard `rgba(8, 12, 24, 0.6)` scrim, in four bands:

1. *Header* — file-type icon chip, filename, and a context meta row (type chip · account ·last4 · size · upload date). Close affordance top-right.
2. *Toolbar* — controls adapt to the file type (see below). Page nav left, zoom centered, secondary actions (rotate, open-in-new) right.
3. *Stage* — a recessed neutral surface (`--ink-950` on dark, `--ink-200` on light) that holds the document. The file content itself is a **white page in both modes** — a receipt or statement is a document, not app chrome, so it never inverts.
4. *Footer* — a "Read-only preview" lock note, with Close (text) and Download (filled primary).

**Branches**, keyed off the file extension:

| Type | Match | Controls | Renders |
|---|---|---|---|
| Image | `jpg png gif webp svg heic tiff bmp avif…` | zoom, **rotate** | the raster, centered in the stage |
| PDF | `pdf` | **page nav**, zoom | the document, one page at a time |
| Other | everything else | — | a "Preview not available" empty state with a Download CTA |

Zoom runs 50–300% in 25% steps; the percent label resets zoom + rotation to fit. `←/→` page a PDF, `Esc` closes.

> **Stack reality check.** The shell (`MudDialog`), header, toolbar (`MudIconButton` / `MudButtonGroup`), and the **image** branch (`MudImage`) are all native MudBlazor. MudBlazor has **no dedicated PDF component** — render the PDF branch with the browser's built-in viewer embedded via `<object data="blob:…" type="application/pdf">` (or an `<iframe>`), and keep the toolbar/page chrome above as the Odyssey wrapper so it matches the rest of the app. The statement/receipt drawn in the specimen are stand-ins for real file bytes; the design work is the chrome around the embed.

## Components — Analyze file

The **Analyze file dialog** (`AnalyzeFileModal`) opens from the **Analyze** action in the file row action menu — on the per-account Files list (Accounts → account detail) and the flat Files page. It runs the file-analysis feature end to end: it **gates on consent before any bytes leave Odyssey**, kicks off an analysis job, shows the extracted **candidate transactions**, **matches each one's merchant and category to the user's existing records with a second AI step**, lets the user edit and select them, and imports the chosen rows as real transactions. Specimen: `preview/28-components-file-analysis.html` (a phase switcher cycles every state, plus toggles for match outcome and the `contacts.create` claim); reference build: `ui_kits/web/AnalyzeFileModal.jsx`.

**Consent gate — the first phase (`consent`).** File analysis transfers the document to a **third-party processor** (Anthropic's Claude API, US), so the dialog opens on an informed, per-document consent step *before* anything is sent — the fix for the privacy issue this feature raised. It carries: a **transfer route** (Odyssey → Anthropic · Claude, with model + region); a quiet **document preview** so the user sees what leaves; a four-item **disclosure** — (1) the whole file is uploaded, (2) Odyssey hasn't inspected it so *whatever* it contains is sent as-is, (3) the user's **contact and tag names** are sent too — **names only, for matching** (no notes, organization numbers, or other fields), with the count shown inline, (4) the data isn't used to train models and is retained for a limited period under Anthropic's [privacy policy](https://www.anthropic.com/legal/privacy); a required **consent checkbox** that gates the **Send & analyze** button; and the **lawful basis** recorded (Consent · GDPR Art. 6(1)(a)). The disclosure copy is sourced from `OdysseyData.analysisTransfer` so the wording shown is exactly what gets logged. Confirming calls `OdysseyData.recordAnalysisConsent(...)` (writes the audit row — see **Analysis log** below), then proceeds to `analyzing`. The consent line itself is the **corrected, versioned** wording (`analysisTransfer.consentVersion`): it now states the contact/tag names ride along for matching, so the recorded `ConsentText` is factually accurate, and the version distinguishes jobs consented under the old vs. corrected disclosure (GDPR Art. 5(2) accountability — old records are never back-dated).

**AI matching — merchant & category (the `matching` phase).** After extraction completes, a **second LLM step** runs automatically before Review: Odyssey sends Claude the extracted merchant/category strings **together with the user's contact and tag names** (names only — an opaque reference token per name, never ids or other fields) and Claude returns the best-matching existing record per field with a confidence. The step is announced via the dialog's `aria-live` region (“Matching merchants and categories…”) and **never re-sends the document**. Returns are applied against `FileAnalysis:Match:AutoLinkThreshold` (default `0.60`):

- **Merchant cell** is now the consumable **`Combobox`** (`OdsCombobox`) — typeahead, keyboard, accessible name, a focusable ≥24px clear, and an inline **Create “‹name›”** row. The create row is **rendered only when the reviewer holds `contacts.create`** (Admin/Owner, **not** the `User` role), so a User-role reviewer never meets a 403 on a happy-path control; server-side `[Authorize]` stays the real gate. Category keeps `TagMultiSelect` (no inline tag-create in v1).
- **Match indicator** — the new **`MatchIndicator`** atom — sits under each merchant/category cell and states, **as text**, where the value came from + its confidence: `Suggested by AI · 91%` / `Created here` / `You chose` / `No match`. Never colour or a meter alone, and distinct from the grid's **Confidence** column, which keeps showing the **extraction** confidence. When the value is **`No match`** but extraction returned a merchant string that matched nothing existing, the indicator turns the dead-end into an action — a **Create “‹name›”** affordance (e.g. `Create "Nopa"`) on its own line that creates *and* links the contact in one click, gated on `contacts.create` exactly like the Combobox's create row.
- **Sub-threshold suggestion.** A match returned **below** the threshold is **not** auto-filled; the cell shows a dismissible suggestion, split into a **status row** (“Suggested by AI · 46%”) over an **action row** — a keyboard-operable **Use ‹name›** (links it, sets `MatchMethod = Manual`) and an ignore — the same status-over-action shape as **No match → Create ‹name›**, so a narrow cell shows the full suggested name in the action instead of one massive line, and the reviewer always sees what the model thought without it riding silently into an import.
- **Match-degraded (non-blocking).** If matching **fails** or is **skipped** (a vocabulary over `MaxVocabulary`, default 500/list), Review still opens with the **raw candidates and no suggestions**, plus an inline `role="alert"` notice and a **Re-match** action (distinct from **Re-analyze**, which re-extracts). The extraction `Status` still governs importability and the orthogonal `MatchStatus` only governs whether suggestions exist — so a match failure **never blocks the import**. A normal (matched) Review also carries a quiet **Re-match** in the toolbar; a re-run refreshes `Llm`/`None` rows but **preserves** rows the reviewer set to Manual/created.

**Resume an open review — durable recovery (no second transfer).** A persisted analysis job whose extraction *completed* but whose candidates are still *pending* is **resumable**: the review can be reopened **straight into the candidate list** from the saved job — surviving a closed dialog, a page reload, or a different device — without re-sending the document or re-billing Claude. The **host** (the files surface) loads the account-scoped resumable map once and **decides the dialog's initial phase**, so the dialog never re-discovers resumability:

- **Resume review** (menu action, shown only when a resumable job exists) → opens on **`resumeLoading`** → **`review`**. ResumeLoading *awaits the reference-data loads (contacts / tags / currencies) before seeding rows*, the same gating `OnInitializedAsync` uses — seeding before reference data drops merchant/tag prefills. Nothing is sent to Claude.
- **Analyze** *with* a resumable job present → opens on **`reanalyzeConfirm`** ("You've already analyzed this file"): **Resume review** (primary → resumeLoading) vs. **Analyze again** (secondary → the normal `consent` gate, a new transfer). This stops an accidental duplicate — opening Analyze no longer silently creates a second candidate set.
- **Analyze** with *no* resumable job → opens on `consent` as today.

The file also carries a **"Review pending · N" chip** (the additive `FilesTableRow.statusBadge` slot, rendered as an `OdsChip`) so the resumable state is discoverable from the row, not just the menu. Meaning is carried **as text** with a full accessible name (file + count); the chip is amber (`pending`). On an import/Done close the host **refreshes the resumable map** so the now-finished file's hint clears (no stale indicator). If the saved job can't be loaded or is no longer resumable (imported elsewhere, deleted) the dialog shows **`noLongerAvailable`** — a curated "This review is no longer available" message (announced via `role="alert"`, **never** the raw `FailureMessage`) with an **Analyze** action. The dialog also gains a **dialog-scoped `aria-live="polite"` region** (it had none) that announces ResumeLoading.

**Anatomy.** The wide variant of the `Modal` shell (`wide` — `max-width: 1240px`, `width: 96vw`). The hero **Review** phase is the budget *Edit multiple* batch grid, widened — there is **no separate read-only view**; you land directly in the editable table.

1. *Header* — title + sub, close affordance top-right (same as every modal).
2. *Toolbar* — file-type chip + filename + account context, the **analyzer provider chip** (`Claude · claude-opus-4-7`), and an `N found` pill.
3. *Candidate table* — one row per extracted transaction. A select checkbox, then the columns below, then a remove button. Sticky header; horizontal scroll below ~1000px.
4. *Footer* — `X of N selected` + signed **net total** on the left; Cancel + `Import N transactions` (primary, disabled at zero) on the right.

**Editable vs. read-only.** Every field except confidence is editable, and each maps onto a real `NewTransaction` field. Confidence is analysis output with no transaction field, so it stays read-only:

| Column | Maps to | Control |
|---|---|---|
| Date | `TimeStamp` | date picker |
| Description | `Description` | text |
| Merchant | `ContactId` | **`Combobox`** (`OdsCombobox`) — search existing, or **Create “‹name›”** inline (create row gated on `contacts.create`) |
| Category | `TransactionTagIds` | `TagMultiSelect` — zero, one, or many tags (no inline create in v1) |
| Amount | `Amount` (signed) | number, colored by sign |
| Currency | `CurrencyCode` | select |
| Confidence | `LlmConfidence` (0–1) — **extraction** | **read-only** meter |
| Reference | `ExternalId` / `InternalId` | text |

The **match step** seeds each merchant/category from the user's existing records, with the provenance shown beneath the cell by the `MatchIndicator`; the free-text `Merchant`/`CategoryHint` are **kept** for audit/display and used as the combobox placeholder when nothing is linked. The import path accepts a *list* of tags (`ImportRequest.TransactionTagIds`). Editing a cell, applying a sub-threshold suggestion, or creating a contact inline marks that field `MatchMethod = Manual`/`Created`.

**Confidence is subtle and never editable** — a 34px meter + percentage, toned by band (sea ≥ 85% · amber ≥ 60% · coral below). This is the **extraction** confidence (is this a real transaction) — a distinct signal from the per-cell **match** confidence (how sure the AI is of the contact/tag), and the two never share a column. Low-confidence rows (< 60% extraction) carry an amber left accent and **start unticked** so the user opts them in deliberately; that import row-selection is independent of whether a merchant or tag is linked.

**Phases** (all driven by the real lifecycle; `FileAnalysisJobStatus`):

| Phase | Trigger | What it shows |
|---|---|---|
| `consent` | dialog opened on an eligible statement (no resumable job) | The privacy/consent gate (above) — disclosure of the third-party transfer + required consent. **Precedes any upload.** Warning-toned `shield` lead icon. |
| `reanalyzeConfirm` | **Analyze** opened on a file that *has* a resumable job | Resume-vs-reanalyze fork — **Resume review** (primary) or **Analyze again** (→ `consent`). Prevents an accidental duplicate transfer. Brand `history` lead icon. |
| `resumeLoading` | **Resume review** chosen | Brief loading state while the saved job + reference data load; announced via the dialog's `aria-live` region, then → `review`. No new AI call. |
| `noLongerAvailable` | saved job can't be loaded / no longer resumable | Curated "This review is no longer available" (`role="alert"`; never the raw `FailureMessage`) + an **Analyze** action. Warning-toned `unpublished` lead icon. |
| `blocked` | file type ≠ `Statement` | Guard — only statements can be analyzed; points the user to Edit → document type. Mirrors the server's `InvalidOperationException`. |
| `analyzing` | job `Running` | Tide spinner, file + provider chip, a three-step checklist. Hands off to `matching` when extraction completes. |
| `matching` | extraction `Completed`, candidates > 0 | The **second AI step** — sends the contact/tag **names** (not the document) and maps the returns back to records. Spinner + the names count; announced via the `aria-live` region. → `review`. |
| `review` | match resolved (fresh, resumed, **or** degraded) | The candidate table with per-cell **match indicators** + sub-threshold **suggestion chips** and a toolbar **Re-match**; when `MatchStatus` is `Failed`/`Skipped`, a non-blocking `role="alert"` **match-degraded** notice sits over the raw candidates with its own **Re-match**. |
| `empty` | `Completed`, 0 candidates | "No transactions found" + Re-analyze. |
| `failed` | job `Failed` | `FailureMessage` + Try again. |
| `done` | after import | `ImportResponse` summary — `Imported N`, any per-row failures, and a link to Transactions. Imported rows post as `Status = New`. On close the host refreshes the resumable map. |

> **Stack reality check.** This is `MudDialog` + a `MudTable`/grid in batch-edit mode. The flow maps onto the existing endpoints: `POST /api/file-analysis` (implicit, via the file's Analyze) → `GET /api/file-analysis/{jobId}` for candidates → `POST /api/file-analysis/{jobId}/import` with the selected, edited rows. The today's `ImportCandidateRequest` carries date/description/amount/currency; to persist the edited **contact, tag and reference** shown here, extend it with `ContactId`, `TransactionTagId` and `ExternalId` (all already on `NewTransaction`). Provider/model come from `FileAnalysisOptions` (Claude · `claude-opus-4-7`). The prototype fakes the running delay with a timer; wire it to real job polling in Blazor.
>
> **Resume (durable recovery).** Resumability is discovered by **one account-scoped read** — `GET /api/accounts/{accountId}/files/analysis/resumable` — returning the *latest resumable job per file* as a `fileId → minimal-summary` list (job id, status, startedAt, candidate/pending **counts only** — no candidate free-text). A file with no resumable job (never-analysed, failed/running only, all-reviewed) is **uniformly absent** from the list so it can't act as an existence oracle. The dialog's resume mode reuses the existing `GET /api/file-analysis/{jobId}` to load the saved candidates (no new write path, no second AI transfer, no new audit entry). In the prototype the map is `OdysseyData.resumableJobs` (read via `resumableSummaryForFile` / `resumableJobsForAccount`, cleared via `clearResumableJob` on a finished review).
>
> **Consent + transfer (privacy issue).** `POST /api/file-analysis` must be gated server-side on a recorded consent — persist a `FileAnalysisConsent` (user, file, timestamp, consent text shown, lawful basis) and reject the job without it. The request additionally sends the user's account / contact / tag **names** as matching context; document this in the transfer record. The processor (Anthropic) is a sub-processor under a DPA — surface it in the privacy notice, not just the dialog. Every transfer writes an audit entry consumed by the **Analysis log** page.
>
> **AI matching (this feature).** A new **`POST /api/file-analysis/{jobId}/match`** runs the second LLM step (synchronous `200` with the updated job; `409` if extraction isn't `Completed` or a match is already `Running`; `503` when the feature is off) — gated on `file-analysis.create` (same external-transfer boundary as `analyze`, **no new claim**). It builds a **vocabulary** of contact + tag **names → opaque reference tokens** (`c0..cN` / `t0..tN`), sends names only (archived excluded, capped per `Match:MaxVocabulary`), and maps the model's returned tokens back to ids by **set-membership** — any token not in the sent list is dropped, so an injected match can't produce an out-of-tenant id (candidate strings are framed as *data, not instructions*; OWASP LLM01). New persisted fields: `MatchedContactId` (FK → `Contact`, **declare `DeleteBehavior.SetNull`** — EF won't emit it for an optional FK; the recurring Odyssey trap), `Merchant`/`Category` match confidences, a `FileAnalysisCandidateTag` join (`ON DELETE CASCADE`), `MatchMethod` (`None`/`Llm`/`Manual`), and job-level `MatchStatus` + curated `MatchFailureMessage` (**never** the raw provider body) + `VocabularyCount`. The extended `GET /api/file-analysis/{jobId}` returns `matchedContactName` via an **explicit slim id→name projection** (no Mapster full-DTO mapping, no N+1) so the read can't leak notes/org-numbers. Config: `FileAnalysis:Match:AutoLinkThreshold` (0.60), `:MaxVocabulary` (500), `:TimeoutSeconds` (60); reuses the existing provider/model/key. Prototype mirrors: `OdysseyData.matchConfig`, `permissions` + `can(claim)`, `analysisVocabulary()`, `matchBand()`, and the candidates' `match*` fields in `data.js`.

**Runtime settings — the kill switch, the model and the destination.** Three values an administrator can change without a redeploy live in the settings store rather than `appsettings.json` (issue #439): `FileAnalysisEnabled`, `FileAnalysisModel` and `FileAnalysisBaseUrl`. All three sit at the **top** of Settings → **File analysis**, above the processor-disclosure rows — the switch and the destination frame everything below them — all three take `system-settings.security.update`, and all three are therefore audited. `FileAnalysis:ApiKey` does **not** move; it stays a deploy-time secret, which is exactly why the base-URL row says out loud that the configured key travels to whatever host is set. Specimen: `components/file-analysis-runtime.html`.

- **The switch is read live, never from the settings cache.** "I turned it off" has to mean the next request is refused, not the next request after a 30-second TTL — so the kill switch is a single-row live read and deliberately **not** a member of the cached settings snapshot. In the kit it is `OdysseyData.fileAnalysisRuntime.enabled`, published by a settings save (standing in for evicting the settings cache **and** invalidating the client's disclosure cache).
- **The affordance follows the switch.** `GET /api/file-analysis/disclosure` carries `enabled`; when it is `false` the file row's **Analyze** action renders **disabled with a visible reason** ("AI document analysis is turned off for this instance.") through the `Menu` item's `note`, and the consent gate is unreachable — a user is never allowed to pick a document and affirm consent only to be answered `503`. The dialog keeps a `featureDisabled` phase for the race the live read makes possible (the switch flips between the row rendering and the dialog opening): "Nothing was sent", no consent recorded.
- **A degraded model or base URL refuses; it never substitutes.** Substituting the shipped default model would stamp a job with a model that did not run; substituting the default destination would transfer a document to `api.anthropic.com` when the deployment was deliberately pointed at a gateway. Both are published as `null` when unusable, so the target cannot be constructed and the analysis answers `503 configuration_unavailable` — its own dialog state ("Nothing was sent"), distinct from the kill switch's, with static text that never names the stored value, the host or the parse error. A degradation in any of the *other* file-analysis settings leaves analysis working; the refusal is scoped to these two.
- **The base URL is shape-validated as strictly as the request builder.** Absolute `https://`, no `userinfo`, no query, no fragment, and **no path** — the provider resolves a root-absolute `/v1/messages` against the value, so a path would be silently discarded and the value saved would differ from the value used. Private, loopback and link-local hosts are **allowed**: an internal gateway is the main reason the setting is editable. The stored value is canonicalised (trimmed, trailing slash removed), so `https://host/` and `https://host` are one value and produce no audit line. Only the **host** is ever echoed — in the advisory, the job stamp and the log — because a gateway URL can carry a credential.
- **Advisories, non-blocking as always.** The switch **on** names the processor and region documents are transferred to (and that per-document consent is still required); a **model** off its shipped default says recorded analyses keep what they ran under; a **destination** off its shipped default names the host and asks the reader to confirm the disclosed processor and region still describe it. The existing processor-correspondence heuristic now reads the host from the **setting** rather than from configuration.

**Consent is bound to the disclosure it was given against.** With the processor, region, model and destination all runtime-editable, a user's cached disclosure and the disclosure in force at transfer time can differ — so the disclosure response carries a **`disclosureVersion`**: a short hash over `processor ␟ processorRegion ␟ lawfulBasis ␟ privacyNoticeUrl ␟ model ␟ host(baseUrl)`. Never the whole URL (the version moves when the destination moves without the input carrying a path or a credential), and **`enabled` is deliberately excluded** — it is not a disclosure fact, and including it would invalidate every open gate on an unrelated toggle. The gate echoes the version on analyze; the server recomputes it from the same per-run snapshot the transfer uses and, on a mismatch, answers **`409 disclosure_changed`** — **no job row, no provider request**. The gate then stays open and usable: the cache is re-fetched, the text re-renders, the affirmation checkbox is **reset to unchecked** (it was given for different facts), a non-dismissable explanation is announced through the dialog's live region, and focus moves to it. Evaluation order is `503 feature_disabled` → `503 configuration_unavailable` → `409`, so a disabled or misconfigured instance leaks no disclosure state. All three states are in the specimen switcher (`preview/28`): **Analysis off**, **Config broken** and **Disclosure changed**.

## Components — Mail transport settings

Four values that used to live in `appsettings.json` — **SMTP host**, **SMTP port**, **Use STARTTLS** and **Client base URL** — are now rows in the settings store, so an administrator configures transactional mail at Settings → **Email** without a redeploy. They open the group, above the sender identity and the throttle they frame, and all four take `system-settings.security.update`. There is no configuration fallback and no adoption step: an empty host is not a missing value, it means *this deployment has no mail configured*. Reference build: `ui_kits/web/SystemSettings.jsx` + `system-settings-data.js`; specimen: `templates/settings`.

**No new widget.** The four rows are declarations rendered by the existing `SettingField` machinery — two `TextInputField`s spanning both columns, a `NumberField`, and the group's first `Switch` tile. What is new is a text row whose **empty value is legal and means something** (`allowEmpty`): without it, configuring mail would be a one-way door, because a host could never be cleared back to unconfigured. Empty short-circuits before the row's own rule, exactly as the server's `StringSetting.AllowEmpty` does.

**Two blocking shape rules, both canonicalising.** The host is a DNS name or IP literal and nothing else — no scheme, port, path or userinfo, and CR / LF / NUL rejected outright, not because the SMTP client would compose a command from them but because the value is written to log lines and audit entries where a newline forges a record. It is lowercased with a single trailing dot stripped, so `SMTP.Example.Net.` and `smtp.example.net` are one stored value and produce no spurious audit line. The client base URL is absolute `https://` **except for loopback**, where `http` keeps the dev stack working — a loopback link resolves on the recipient's own machine, so the exemption cannot be used to intercept anything. No query, no fragment, no userinfo; a path is allowed (a deployment may sit under a subpath) and normalised without its trailing slash.

**Changing the host, or turning STARTTLS off, destroys a stored credential.** The client connects before it authenticates, so whatever host is set receives the stored SMTP credential; and a credential entered for an encrypted transport must not be replayed over a cleartext one, where passive network position alone is enough to read it. Both changes therefore clear `Email:Username` and `Email:Password` in the same commit. The page says this **three times, at descending distance**: a non-blocking row advisory while the value is being edited, naming the old and new host; the **`SecretClearOnSaveDialog`** gate in front of Save; and a warning toast afterwards naming where to re-enter them.

- **The gate is on the page's batch Save, not on a field.** Settings has one whole-page Save and no per-field submit, so Confirm submits the entire batch exactly as an unguarded Save would — splitting it would invent partial saves the page has never had. **Cancel discards nothing**: the page stays dirty with every edit intact, so the host can be reverted by hand and the rest saved. The dialog says so, because a Cancel that silently dropped every pending edit would be the worse surprise.
- **The trigger is direction-sensitive**, and computed from the saved snapshot rather than a dirty flag: a host that moved to a *different non-empty* value, and STARTTLS moving *true → false* only. Saving the same host again, or turning STARTTLS on, clears nothing.
- Closing by Confirm, Cancel or Escape **returns focus to the control that opened it** — neither `Modal` nor this page's other dialogs restore it on their own.

**The origin-mismatch hint is computed in the browser.** Anyone who can change the client base URL receives password-reset tokens for any address they know, so a value pointing somewhere other than the origin the administrator is actually browsing from is worth a second look. It is a hint, never an error and never `aria-invalid`: an operator may legitimately set a public URL from an internal hostname, or set it ahead of a DNS cutover. It lives client-side because the server has no view of the caller's origin on the read path, and an advisory composed there would re-fire on every page load rather than on the value that differs.

**Unconfigured mail is a page-header signal, not a Save problem.** With the host empty the page contributes one entry to the same rollup the undecryptable-credential rows use — at **information** severity, because a deployment that has not configured mail yet is incomplete, not broken: *"Transactional mail is not configured. Confirmation and password-reset messages are logged and skipped, so no account can be confirmed or recovered until an SMTP host is set." In Email.* with a **Fix →** jump to the host row. It is kept out of the `problems` list beside Save deliberately — neither the cause nor the fix is a Save, and merging them would make Save look blocked by something Save cannot fix. The rollup is already gated on the security claim, so only an administrator who can fix the state sees it.

> **Stack reality check.** The four keys join `SystemSettingsKeys` / `SystemSettingsDefaults` / `SystemSettingsRegistry` with no new endpoint — `GET`/`PUT /api/system-settings` gain four fields — and are read **uncached, per send**, so a change binds on the next message rather than after a cache TTL. The credential clear and the settings write share one transaction, and the send path reads these four through a dedicated fail-closed reader rather than `SystemSettingsReader`'s defaulting overloads: substituting 587 or `true` for an unparseable stored value would put a value the administrator never chose on the wire.

## Components — Analysis log

The **Analysis log** (`FileAnalysisLog.jsx`) is the admin-only screen at `/analysis-log` — the audit trail of every external AI file-analysis transfer. It exists for the accountability half of the privacy issue: because analysis sends a complete document to a third party, the workspace needs a who · which file · when · result record for ISO-27001 traceability and breach response. It sits in the admin nav between **Roles** and **Settings**. Specimen: `templates/analysis-log`; reference build: `ui_kits/web/FileAnalysisLog.jsx`.

**It owns no new visual language.** The page is the **Page header** pattern (with a default-open Search region — free-text over file / user / account / request ID, plus an outcome `Select`) over a warning-toned transfer notice, then a list of records built on the shared **`.acct-item`** record-row scaffold (the same expand → detail surface as Roles and the Accounts list). Newest first.

**The row.** A leading **AI-sparkle** avatar (`auto_awesome`), tinted by outcome — **green** completed · **amber** in-progress · **red** failed; the file name + a status pill; a sub-line of *user · account · provider/model*; and right-aligned *UTC timestamp* + *imported / candidate* result. Expanding reveals the full audit grid — when (UTC), initiated by (name + email), account (name + masked number), processor + model + prompt version, what was sent (pages · size), the **names sent** (`VocabularyCount` — contact + tag names), result, the **matching** outcome (`MatchStatus`: Completed / Skipped-over-cap / Failed, with the curated failure reason — never the raw provider body), duration, **lawful basis**, **request ID**, and a **consent** confirmation — closing with the verbatim consent text the user affirmed.

**The conditions the transfer ran under.** Now that the destination, the processor and the region are all runtime settings, none of them can be reconstructed after the fact — so each job stamps them once, from the same snapshot, and the detail grid states them: **Processor in force**, **Region in force** (the fact that decides whether a transfer was a third-country transfer — GDPR Art. 44–49) and **Destination host** (Art. 30(1)(e): who actually received the data). Host only — never the path, query or `userinfo`, which a gateway URL can carry. A job recorded before those columns existed reads a dimmed, italic **Not recorded** rather than today's values: back-filling would put a value into an audit record that was never observed, and a fabricated region would be a fabricated answer to "was this a third-country transfer?". Because redirects are not followed on the outbound client, the recorded host cannot diverge from the host the document reached.

**Data.** `OdysseyData.analysisAuditLog` (newest-first records) seeds the page; `OdysseyData.recordAnalysisConsent(entry)` — called from the Analyze dialog's consent gate — prepends a live row, so consenting to a transfer makes it appear here immediately. Both live in `ui_kits/web/data.js` beside `OdysseyData.analysisTransfer` (the shared disclosure/lawful-basis constants), `OdysseyData.fileAnalysisRuntime` (the live switch + model + base URL) and `OdysseyData.analysisDisclosure()` (the resolved disclosure and its `disclosureVersion`).

> **Stack reality check.** `AnalysisLog.razor` (`/analysis-log`, gated on an admin claim — e.g. `files.analyze.audit`), backed by a `GET /api/file-analysis/audit` over a `FileAnalysisAuditEntry` tab

## Components — License / ToS acceptance

The **License / Terms-of-Service acceptance** feature is the frontend for the acceptance spec: every user must explicitly accept the repository **License** and the admin-managed **Terms of Service** at first login, and re-accept within a bounded window whenever either changes. Three surfaces, plus two auth touch-ups; all seed from `ui_kits/web/legal-data.js` (`window.OdysseyLegal` — `licenseText` (BSD 2-Clause), `licenseSha`, `tosVersions`, `currentTos`). In production these read `GET /api/legal/*`.

**Accept terms** (`AcceptTerms.jsx`, `/accept-terms`, `templates/accept-terms`, `templates/accept-terms`) — the blocking interstitial a signed-in but non-compliant user is routed to at login or mid-session, under its **own dedicated layout** (no drawer / module rail, mirroring `OnboardingLayout`). A focused **two-step wizard** (License → Terms of Service) driven by a centered stepper: only the current document's panel renders at a time. Each step is a keyboard-reachable scrollable read-only block with the themed scrollbar; **Accept is gated until the reader scrolls to the end**, and each document has an independent **Decline** (confirm → signed out → `/login?reason=legal-declined`, no lockout). A document with nothing to accept — ToS not yet published, or a License that failed to load (a fallback) — becomes an acknowledge-and-**Continue** step instead of an Accept step. Load / error / declined states plus a dashed *Preview state* review bar (a design aid, not shipped).

**Legal documents** (`LegalDocuments.jsx`, admin panel inside `/settings`, `templates/legal-documents`, `templates/legal-documents`) — the bespoke admin surface for authoring versioned ToS, gated by the **`UsersManage`** claim (explicitly its own carded `PageHeader` panel, **not** a `SettingItem` row). A 50,000-char plain-text editor (`NoteField` + counter, themed scrollbar), a dedicated **Publish new version** action with a confirmation dialog (warns everyone — including the publishing admin — must re-accept), a retained read-only **version-history table** (published date, publisher `publishedByDisplayName`; `null` → "deleted user") with an on-demand full-text viewer `Modal`, an empty state, loading / load-error states, and an **own-compliance precheck** that routes a non-compliant admin through `/accept-terms` first.

**Register review + Login** (`Login.jsx`) — `/register` gains the informational **License / ToS review** (`RegisterLegalReview`): two "I have read and agree to the …" checkboxes whose links open each full document in a `Modal`. Create account is disabled until email + password + matching confirm are filled and both boxes checked; submit also validates email format and a 6-char minimum. Login accepts `reason="legal-declined"` to surface the signed-out-after-declining notice. Registration display is informational — the authoritative acceptance is recorded at first login.

> **Stack reality check.** License compliance is tied to a **SHA-256 of the repo `LICENSE`** (a text change is detected with no migration); ToS content is admin-versioned and insert-only. Enforced server-side via a claim recomputed by a custom `UserClaimsPrincipalFactory` (at login and on `SecurityStampValidator`'s existing revalidation) + a middleware gate returning `451 LEGAL_ACCEPTANCE_REQUIRED`, surfaced client-side by a shared `LegalComplianceHandler`. `/accept-terms` renders under its own layout, sequenced before the onboarding gate.

le the analysis endpoint appends to on every transfer. Entries are immutable and retained per the data-retention policy; the consent text is stored on the row so the record reflects exactly what the user agreed to at the time, even if the wording later changes.


## Components — Files page

The **Files page** (`Files.jsx`) is the flat screen at `/files` — every stored file across every account in one searchable, sortable table. It mirrors the **Transactions page** layout (page header + filter card + table) but it *manages* files rather than creating them. Specimen: `templates/files` (the whole page, static), with `preview/29-components-file-viewer.html` for the View modal a row opens; reference build: `ui_kits/web/Files.jsx`.

**A join, not a collection.** Files belong to accounts — there is no flat files endpoint. `data.js` keys `accountFiles` by `accountId`; the page flattens them into one list and joins each file back to its owning account for filter context. The header sub counts the set (`5 files across 2 accounts`), there is **no header primary** (files are added per-account via Add file on Accounts or the upload field), and Search holds a name query plus a document-type `MultiSelect` — **no account filter by design**; search + type cover the MVP, and a file's owning account shows in its View modal.

**The table.** The consumable DS **`FilesTable`** (`components/FilesTable.jsx` — the same surface as the per-account Files list and the Transactions panels, now a **preset of `RecordTable`**). Four sortable columns — Name, Type, Size, Uploaded (the default sort, newest first) — plus the actions cell. File rows follow the standard **expand → detail → inline edit** lifecycle: click a row (or **View details**) for the read-only MetaTile detail, **Edit** swaps in the inline panel (name + document type, the only mutable fields), Save flashes the row's **Saved** chip. There is **no Account column**. Each row leads with a **kind avatar** — a document-type icon tile whose glyph + oklch hue match the upload modal's kind picker (resolved via `typeFor`).

**Document types.** Four kinds, each one icon + categorical hue: **Statement** (teal `description`) · **Document** (slate `insert_drive_file`) · **Receipt** (green `receipt_long`) · **Tax** (magenta `request_quote`). Unknown extensions fall back to the slate document glyph. Only **Statements** can be analyzed.

**The overflow menu** is the file action menu, identical on every files surface and following the record-table menu convention (View details · Edit · file-specific items · — · Delete):

| Action | Opens / does |
|---|---|
| View details | Expands the row into its read-only detail — the record, not the document. In-place, no modal. |
| Edit | The inline edit panel in the expanded row — rename + change document type (its only mutable fields). |
| Preview | `FileViewerModal` — the document itself: PDFs paged, images zoomable, else a download prompt. |
| Download | Native browser save with the stored filename — no preview, no navigation. |
| Resume review | `AnalyzeFileModal` opened on the saved job — **only when the file has an open, resumable analysis** (extraction done, candidates still pending). Reopens straight into the candidate list; no new transfer. The row also shows a **“Review pending · N”** chip. |
| Analyze | `AnalyzeFileModal` — the candidate-review flow. **Statements only**; other kinds are blocked up-front. With a resumable job present it opens on the **reanalyze-confirm** fork (resume vs. analyze again) rather than silently creating a duplicate. |
| Delete | Divided off, coral, through the confirm dialog. |

> **Stack reality check.** The flat page is `Files.razor` (`/files`) standing in for a MudBlazor `MudTable`. An `AccountFile` carries name, document type (`AccountFileType` — `Message` / `Statement` / `Contract` / `Tax` / `Other`), size and upload date, and belongs to an account — so the list is assembled from each account's `GET /api/accounts/{id}/files`, not a flat endpoint. Edit is `PATCH /api/files/{id}` (name + type only; the bytes are immutable), Delete `DELETE /api/files/{id}`, Analyze `POST /api/file-analysis`. The prototype flattens `data.js` `accountFiles` in `Files.jsx`; new files are uploaded per-account, never from this view.

## Components — Authentication

The **auth screens** live on `AuthLayout` — a centered 420px card, no drawer or app bar — and now span the full sign-in / sign-up / confirm / **recover** loop. Specimens: `templates/login` (Login + Register, every state), `templates/confirm-email` (the confirmation landing page), `templates/forgot-password` and `templates/reset-password` (the forgotten-password recovery pair), and `templates/account-2fa` (the enrollment + management surface that backs the login second factor). Reference builds: `ui_kits/web/Login.jsx` · `ConfirmEmail.jsx` · `ForgotPassword.jsx` · `ResetPassword.jsx`.

**Login is two-phase** (`LoginPhase` in `Login.razor`):
1. **Password** — Username/Email + Password → **Sign in**. The credential error is verbatim: *"Unable to sign in. Please check your username/email and password."* A `LockedOut` result is deliberately ambiguous — one combined message covers awaiting-approval, admin-disabled, and too-many-attempts. A **Forgot your password?** link sits directly under the password field (Password phase only, not during 2FA) and opens `/forgot-password`.
2. **Two-step verification** — shown only when Identity returns `RequiresTwoFactor` after the password is accepted. Enter a 6-digit **authenticator code** or switch to a **recovery code**; the authenticator path offers an opt-in *"Remember this device"* (recovery-code logins are never remembered). You can toggle between code types and step *"Back to sign in"*.

**Register** ends at the inbox, not the app: on success it reads *"Account created. Check your email for a confirmation link — you'll need to confirm before signing in."* The primary is **Create account** (per the New/Create convention) — not the old "Sign up", and the old "Registration successful. You can now log in." copy is gone. The password field now renders the shared **`OdsPasswordRules`** checklist live (a visible improvement — it previously surfaced one error string only on submit).

**Confirm email** (`/confirm-email`, `ConfirmEmail.razor`) is where that link lands. It verifies the `userId` + `code` and resolves to **verifying** (spinner), **confirmed** (success → *Go to sign in*), or **failed** (invalid/expired → enter an email to resend, with a deliberately non-committal response). An email **change** carries a `changedEmail` param, and the confirmed copy reflects the new address.

**Forgot / reset password** is the self-service recovery pair, both anonymous on `AuthLayout`:

- **Forgot password** (`/forgot-password`, `ForgotPassword.jsx`, `templates/forgot-password`, `templates/forgot-password`) — one **Email** field → **Send reset link**. On every `200` it shows the same neutral *"If an account exists for that address, we've sent a link to reset your password. The link expires in 1 hour."* — byte-identical for known, unknown, unconfirmed and throttled addresses, so **nothing is disclosed** (no user enumeration). A **Send again** action issues a second, independent link. A `429` is the one exception: it keeps the user on the form with a *"Too many attempts"* line.
- **Reset password** (`/reset-password?code=…`, `ResetPassword.jsx`, `templates/reset-password`, `templates/reset-password`) — where the emailed link lands. Four phases: **invalidLink** (no `code` — no request made), **form** (Email retyped, **never carried in the URL**; new password with the shared `OdsPasswordRules` checklist; confirm; submit disabled until all five rules pass and the entries match), **done** (*"…Any other devices you were signed in on will be signed out shortly."* — worded for the 30-minute security-stamp interval, not an instant guarantee), and **failed** (token invalid / expired / used — a single copy that never distinguishes which, to avoid leaking account state). A password-policy rejection instead keeps the user on **form** with the message inline. Each outcome heading is a focusable `tabindex="-1"` target so focus lands on the new panel.

**Shared password rules & the change-password form — one source each.** The five rules and the 16-character minimum are declared once in the DS `PASSWORD_POLICY` (`components/PasswordRules.jsx`, specimen `preview/21`). The whole current→new→confirm triad — those rules, the three fields, the failure banner, and the submit — is itself one shared component, **`PasswordChangeForm`** (`components/PasswordChangeForm.jsx`), consumed by BOTH `/account`'s change-password surface AND the new **`/change-password-required`** admin forced-reset gate, so the two can never drift and the `autocomplete="current-password"/"new-password"` + `role="alert"` accessibility fixes land once. Register and `/reset-password` render just the checklist (no current-password field). All of it mirrors the server's `IdentityOptions.Password` gate. This corrected a live defect — the `/account` checklist previously hardcoded "At least 6 characters", the framework default rather than this app's policy.

**Admin-triggered reset & the block (`/users` → gate).** An operator with `users.update` can **Send password reset** for any user from the `/users` row menu (Edit → **Send password reset** → Copy ID — divider — Delete): a warning-toned `lock_reset` confirmation, then one of four honest toasts — link sent, applied-but-email-failed, no confirmed email, or throttled. A **Password reset pending** `OdsChip` (warning tone, icon + text) then shows in the row's Account-status cell and the detail panel. The target is signed out of all devices and, if they sign in with the old password, meets the `/change-password-required` gate above until they set a new one.

> **Stack reality check.** All stock **ASP.NET Core Identity**: `SignInManager.PasswordSignInAsync` (which raises `RequiresTwoFactor` / `IsLockedOut`), `TwoFactorAuthenticatorSignInAsync`, `TwoFactorRecoveryCodeSignInAsync`, and the confirm-email / resend-confirmation endpoints. The "remember this device" toggle maps to Identity's `rememberClient`; a recovery-code sign-in clears it via `ForgetTwoFactorMachineAsync`. The recovery pair consumes the already-mapped `POST /forgotPassword` and `POST /resetPassword` (`MapIdentityApi`); the emailed link is composed server-side to `{ClientBaseUrl}/reset-password?code=…` and the reset token is single-use (security-stamp bound) with a **1-hour** lifespan.

## Components — Users

The **Users page** (`Users.jsx`, with the `Roles.jsx` reference as its sister destination) is the admin-only screen at `/users` — where an operator with `users.update` administers *other* people: their **role**, **account flags**, and — new this pass — a **Send password reset** action for a compromised or locked-out account. It's the counterpart to the self-service **User Account** page (below): that page is about *you*, this one is about *everyone else*. Specimen: `templates/users` (five frozen states — search/filters open, a row expanded into its read-only detail, the Edit panel mid-confirmation, the **row menu open with Edit + Delete**, and the **delete-confirmation dialog** incl. the last-Admin block), with `templates/roles` for its sister Roles & access reference; reference build: `ui_kits/web/Users.jsx`.

**It owns one new surface — the data table.** Everything else is the kit: the **Page header** (p.10) over a searched/filtered `<table>` whose rows follow the Accounts/Budgets **expand → detail → edit** lifecycle (p.14), applied to a table row instead of a card. The header subject is a *population*, not a person — a generic `manage_accounts` badge, a sub that counts the set (`44 users · 38 enabled`), and a **Refresh** primary (operators administer existing users, they don't create them here). The Search region carries a debounced query plus **two** `Select` filters: **role** and **account status**. (Earlier drafts also showed email-confirmation and 2FA filters; both were removed — the `GET /api/users` endpoint filters by role + enabled only, and `ExistingUser` exposes no two-factor field, so the real page can't offer them.)

**The table.** Six sortable columns — User (avatar + name + `@username`, the default sort), Email, Role (the role pill from p.24), Email status, Account, Created — plus a row-actions cell (overflow menu + disclosure). Roles sort by **privilege rank**, not alphabetically; booleans sort false→true; null created-dates sink to the bottom. A row expands in place into a read-only **detail** (an identity meta-grid + a collapsed *Role permissions* count), exactly like a budget. The full matching set renders in one list — no pager.

**Status vocabulary.** The page's net-new atoms are the row badges, each borrowing an existing accent so no new hue enters: **Enabled** (account-active green) · **Disabled** (closed slate) · **Confirmed** (informational sea) · **Unconfirmed** (archived amber). (Two-factor state is **not** shown on this page — the user-list contract doesn't carry it.) The role pill itself is shared with — and documented on — **Roles & access** (p.24).

**Edit is access, not identity.** Selecting Edit swaps the read-only detail for a form in place (the row never navigates). Username and email are shown **locked**; only three things change, and they commit as a unit:

| Field | Maps to | Notes |
|---|---|---|
| Email confirmed | `PATCH /api/users/{id}` | Boolean flag. Marking unconfirmed is disruptive → confirmation. |
| Account enabled | `PATCH /api/users/{id}` | Disabling applies a backend lockout and signs the user out → confirmation. |
| Role | `PUT /api/users/{id}/role` | Admin / Owner / User / Guest. Granting or removing Admin is disruptive → confirmation. |
| Username · Email · Created | — | Read-only here. These belong to the person (User Account, p.25). |

**The row menu carries two permission-gated actions.** **Edit** (`users.update`) opens the in-place form above; **Delete** (`users.delete`, Admin-only) is divided off and coral, and opens an **error-tone confirm dialog** — *"Permanently delete {name} ({email})? This removes the account and cannot be undone."* The API blocks two deletes with a `409 Conflict` — your own account, and the **last enabled Admin** — and the dialog surfaces the last-Admin case inline (an error alert + a disabled Delete), the same self-protection the Edit panel applies.

**Disruptive changes confirm inline**, and the system protects itself. Disabling an account, marking an email unconfirmed, or touching Admin inserts a one-step confirmation in the panel (the same in-place pattern the 2FA danger zone uses — not a modal). And the **last enabled Admin** can't be disabled or demoted: the panel raises a blocking guard and disables Save, mirroring the backend's `409 Conflict`.

> **Stack reality check.** The list is `Users.razor` (`/users`), driven by the admin API: `GET /api/users` (searched/filtered → `UsersPage.items`), `GET /api/users/roles` (the four seeded roles + claim sets from `RoleDefinitions.cs`), `PATCH /api/users/{id}` (the two flags), `PUT /api/users/{id}/role` (role assignment, 409 if it would orphan the last enabled Admin), and `DELETE /api/users/{id}` (permanent delete — `users.delete`; 409 on self-delete or the last enabled Admin). User administration is now four claims — **`users.manage` · `users.read` · `users.update` · `users.delete`** (all in `PermissionClaims.AllClaims`, so **Admin-only**; `OwnerClaims` excludes every `users.*`). The table is a CSS-styled `<table>` standing in for MudBlazor's `MudTable` + `MudTableSortLabel`; the role pill, claim chips, and permission catalog are shared with the Roles page. The prototype computes the last-Admin guard client-side in `Users.jsx`; the server is the source of truth.

## Components — User Account

The **User Account** area (`Account.jsx` + `AccountTwoFactor.jsx`) is the self-service page at `/account`, reached from the drawer footer (`account_circle`, below Preferences). It's where a signed-in person manages their own **identity**, **security**, and reviews what their role lets them do — distinct from the admin **Users** page (p.23), which manages *other* people. Specimen: `templates/user-account` (the whole page, static); reference build: `ui_kits/web/Account.jsx`. The signed-in user is the seed Owner, **Jane Sato**.

**It owns no new visual language.** The page is an instance of the **Page header** pattern wrapped around four stacked setting sections (the **Overview** is the fifth, but lives in the header's Overview region — not the scrolling list), every one assembled from kit atoms (`Card` / `Field` / `Alert` / `Chip` / `Button` / `MIcon`) + the admin permission catalog. The person *is* the page subject, so the header's leading slot is their avatar and the title is their name; status chips carry role · email · 2FA · tenure; the **Problems** signal rolls up account-level recommendations (today: a single info nudge to enable 2FA); **Overview** opens an identity / security / access summary. All of that is the page-header's own machinery.

**Anatomy.** Header (whose Overview region is section 1 below), then one scrolling list of the other four sections (the former tabs, stacked like Preferences and filtered live by the header Search):

| # | Section | What the user does | Maps to · ASP.NET Core Identity |
|---|---|---|---|
| 1 | **Overview** | Reads identity / security / access at a glance | Composed client-side from the loaded user + 2FA state — no endpoint of its own. Lives in the header's Overview region. |
| 2 | **Email address** | Change the sign-in email; resend confirmation | `GenerateChangeEmailTokenAsync` → `ChangeEmailAsync` (emailed link, landing on `/confirm-email`); `GenerateEmailConfirmationTokenAsync` to resend. The `EmailConfirmed` flag drives the chip. |
| 3 | **Password** | Change password against a live checklist | `ChangePasswordAsync(user, current, new)`. The five rules come from the shared `PASSWORD_POLICY` (16 chars + four classes), mirroring `IdentityOptions.Password`. |
| 4 | **Two-factor** | Enroll an authenticator; manage recovery codes | `GetAuthenticatorKeyAsync` (QR + manual key) · `VerifyTwoFactorTokenAsync` (6-digit verify) · `SetTwoFactorEnabledAsync` · `GenerateNewTwoFactorRecoveryCodesAsync` · `ResetAuthenticatorKeyAsync` (danger zone). |
| 5 | **Permissions** | Reviews what the role can do | `GetClaimsAsync` + role claims (`RoleManager`). Catalog is `PermissionClaims.cs`; grants come from the role in `RoleDefinitions.cs`. **Read-only** — no write path. |

**The password checklist is the policy, made visible.** Its five rows are exactly the `IdentityOptions.Password` defaults — `RequiredLength = 6`, `RequireUppercase`, `RequireLowercase`, `RequireDigit`, `RequireNonAlphanumeric` — greening one by one as the new password satisfies each. Never show a rule the backend doesn't enforce; if the options change, change the checklist with them.

**The two-factor flow** is a fully-clickable wizard: `OFF → Set up → [1] scan QR / copy key → [2] enter 6-digit code → [3] save recovery codes → ON`. Verify-&-enable always regenerates a fresh recovery-code set, so re-enrolling never finishes without showing fallback codes. Once on, it surfaces status, one-time recovery-code regeneration, and a coral **danger zone** — each gated by an inline "type to confirm" step, the same pattern the Users page uses, not a separate dialog. Two danger actions: **Reset authenticator key** and **Turn off 2FA** (type `DISABLE`). **Reset key changed:** resetting now **turns 2FA off and walks the user straight back through setup** (it disconnects the old app, regenerates the key, and re-runs scan → verify → save codes) rather than swapping the key in place — the copy reads *"Disconnects your current app and walks you through setup again."* Dedicated specimen: `templates/account-2fa`.

> **Stack reality check.** Everything here is stock **ASP.NET Core Identity** behind `MudCard` forms — no custom backend. The page is `UserManager`/`SignInManager` calls plus the `IdentityOptions` the app already configures; the 2FA QR is the standard `otpauth://totp/` URI rendered to an image. The Permissions section is read-only by design — access is granted by **role** on the Users page, never hand-edited here. The faux-QR, deterministic recovery codes, and the timer-free verify in the prototype are stand-ins; wire them to the real `UserManager` token calls in Blazor.

## Substitutions to flag

- **Users list — 2FA & email-status filters removed.** Earlier drafts of the Users page showed a 2FA badge/filter and an email-confirmation filter; the `GET /api/users` contract filters by role + enabled only and `ExistingUser` carries no two-factor field, so both were dropped to match reality. If the contract later exposes two-factor, re-add them together (column + filter).
- **Recovery-code Download is a kit extra.** The live `Account.razor` recovery-code panel only offers **Copy**; the kit's `AccRecoveryCodes` adds a **Download** button. Keep it as a proposed enhancement, or drop it to match the page exactly.
- **Account email-change is a guided preview.** `Account.razor`'s email-change section has no backend yet (`UpdateEmail` previews the success state without calling the API); it renders the design but doesn't send. The confirmation link it describes lands on `/confirm-email`.

- **Logomark is final.** `assets/odyssey-logomark.svg` is the user-approved Odyssey mark; the animated version lives at `assets/odyssey-logo-animated.svg`. The earlier 4-variant exploration (Compass / Horizon / Terminal / Tide wave) has been deleted.
- **The `MudTheme` is live.** The palette in `colors_and_type.css` is wired into `Odyssey.Client/Theme/OdysseyTheme.cs` (both `PaletteDark` and `PaletteLight`, plus typography, layout, and the 26-step elevation stack) and consumed by `Odyssey.Client/Layout/OdysseyThemeProvider.razor`. Token edits here must be mirrored there — the CSS and the C# are two copies of the same values.
- **Roboto** is the declared product font and is on Google Fonts; no substitution needed.
- **Material Icons font** is canonical and already loaded — no substitution.

---

## Reading further

If you have access to `centralcmd/odyssey`, the following will sharpen designs further:

1. `Odyssey.Finance.Dtos/` — the full domain model (account/budget/transaction/file-analysis enums).
2. `Odyssey.Api/Controllers/` — endpoint shapes that drive screen states (loading, error, paging).
3. `Odyssey.Client/Pages/Finance/` — the live finance screens. These are the ground truth; sync this design system to them.
4. The MudBlazor docs at https://mudblazor.com/components — every component referenced in this design system is straight from there.
