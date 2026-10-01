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
- [Content fundamentals](#content-fundamentals)
- [Visual foundations](#visual-foundations)
- [Accessibility](#accessibility)
- [Iconography](#iconography)
- [Substitutions to flag](#substitutions-to-flag)
- [Reading further](#reading-further)
- [Feature & reference docs](#feature--reference-docs) — per-feature detail in `docs/`

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
| `colors_and_type.css` | All design tokens as CSS custom properties. Names mirror MudBlazor's `--mud-palette-*` semantics so they wire straight into `MudTheme.PaletteDark` / `PaletteLight`. **Also ships a framework-neutral `--color-*` alias layer** (`--color-surface`, `--color-text`, `--color-primary`, `--color-danger`, …) mapping 1:1 onto the Mud tokens, so a consumer who isn't on MudBlazor can theme against intent names instead of Mud vocabulary — both resolve to the same per-theme value. `@import`s `components.css`. |
| `icons.css` | The inlined Material Icons `@font-face` + `.material-icons` base class (~170 KB), split from the token layer so token-only consumers can skip it. `styles.css` imports it; cards and thumbnails that must render glyphs in captures also `<link>` it directly. |
| `components.css` | Portable, token-driven styles for the typed components, prefixed `.odc-*` so they never collide with the reference kit. `@imported` by `colors_and_type.css` so they ship with the tokens. |
| `components/` | Typed, **consumable** components — each a `.jsx` + `.d.ts`. **Atoms:** `Button`, `IconButton`, `FieldShell`, `Field`, `SearchField`, `AmountField`, `MoneyField`, `CurrencySelect`, `NoteField`, `NumberField`, `TextInputField`, `ErrorSummary`, `FormRow`, `Select`, `Chip`, `Badge`, `Card` (+ `CardHeader` / `CardBody` composition slots), `StatTile`, `InfoTile`, `Alert`, `EmptyState`, `Avatar`, `MIcon`, `SeverityIcon`. **Brand:** `BrandMark` (the compass-rose logomark as inline SVG). **Scaffolds:** `PageHeader` (title + sub + chips, composed action cluster, toggleable Signal / Overview / Search / Reference regions — every screen mounts it first), `SettingRow` (icon + label + description | one control — the Preferences card row), `SettingField` (label notched into the field's outline, control inside, description + last-changed stamp on one helper line — the System settings grid block), `AddRow` (the dashed list-closing create affordance). **Navigation:** `Drawer` (+ `NavItem`). **Overlays:** `Modal`, `Tabs`, `Tooltip`, `Menu`. **Disclosure:** `Collapsible` (header row + count pill + optional leading `icon` and a right-aligned `action` slot — the single component behind the Files / Transactions / Terms record sections, the Budgets item list, and the Users role-permissions reveal; the reference kit consumes it through the `Components.jsx` bridge, no second copy). **Form controls:** `Switch`, `Checkbox`, `RadioGroup`, `SegmentedControl`, `CardSelect` (icon-over-label card picker for the "what kind is this?" question at the top of a create dialog — contract party kind, policy party role, term kind; group `accent`/`accentSoft` or per-option `color`/`soft`), `Combobox`, `MultiSelect`, `TagMultiSelect` (the multi-tag picker for transactions), `TransactionTagPicker` (the required single-tag picker where the tag is the record's identity — the budget item), `DatePicker` (+ `DateField`, its labelled form-field form), `FileUpload`, and the typed registry pickers `AccountTypeSelect`, `ContactTypeSelect` / `ContactTypeMultiSelect`, `FileTypeSelect kind="account"` / `-MultiSelect`, `FileTypeSelect kind="transaction"` / `-MultiSelect` — the file-type pickers delegate to the shared `RegistrySelect` / `RegistryMultiSelect` engines (registry in, themed control out). **Account custodian:** `CustodianChip` (the read-only "held at" chip on the account card) + `CustodianSelect` (the optional custodian picker on the create dialog + inline edit grid, a reuse/extension of `Combobox`). `AccountTypeChip` renders the account type as a chip (the sibling of `CustodianChip`) in the detail metadata grid; `AccountStatusChip` does the same for the account status (a tone-colored dot + label + muted date, e.g. "Open · since Mar 14, 2021"). **Data:** `Table`, `RecordTable` (+ its atoms `SortHeader`, `ActionMenu`, `MetaTile`), `TxnTable` (the transactions ledger), `FilesTable` (the attachments table), `TagChips` (the read display of a transaction's tag set). **Feedback:** `Skeleton` (+ `SkeletonRow`), `Toast` (+ `ToastStack`), `Spinner`, `ProgressBar`, `ProblemAlert` (the fix-it block of the problem/signal pattern). **Charts:** `Sparkline` (axis-less trend strip), `LineChart` (the axis'd trend card), `Donut` (+ `DonutLegend`). **Indicators:** `Delta` (one component for variance · directional · signed change). A consuming project reads them off `window.OdysseyDesignSystem_d5aa51` after loading the compiled `_ds_bundle.js`. The reference kit in `ui_kits/web/` consumes the same components through thin prop-name bridges in `Components.jsx` — there is no second implementation. `PageHeader` specimens: `components/pageheader.html` (live) and `preview/20-components-page-header.html`. |
| `assets/` | Brand marks (logomark, wordmark) and the rasterized Odyssey favicons (16 / 32 / 192 / 512). |
| `docs/` | One Markdown file per feature page, record section and reference-data vocabulary — the per-feature detail moved out of this README. See [Feature & reference docs](#feature--reference-docs). |
| `styles.css` | The consumer entry point — `@import`s the token layer plus every shared kit stylesheet. |
| `preview/` | Bite-sized HTML cards used by the Design System tab — type specimens, color swatches, component states. Edit these to iterate on tokens. |
| `ui_kits/web/` | Hi-fi recreations of Odyssey's product screens (dashboard, accounts list, transactions, budgets, receipt review) as React + JSX components. Open `ui_kits/web/index.html` for a click-thru. |
| `SKILL.md` | Cross-compatible Agent Skill manifest — drop this folder into Claude Code and invoke as `odyssey-design`. |


### Naming & prefixes

- `--*` tokens are declared only at `:root` / `[data-theme]`. Component-level custom properties that act as API (`--rec`, `--rec-soft`, `--odc-infotile-accent`) are never declared in CSS; every use site carries a token fallback, so they stay out of the token set.
- `.ods-*` — foundations and utilities (type styles, skip link, money).
- `.odc-*` — the consumable components in `components/` (styled by `components.css`).
- Kit-local feature prefixes (`acct-`, `con-`, `je-`, `tk-` / `tk-list-`, `prop-`, `tx-` …) belong to `ui_kits/web/` only. Never use them in `components/`; promote a pattern to `.odc-*` when a second feature needs it.
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
| `RowActions` | End-of-row icon cluster (edit/delete/…), revealed on row hover or focus, always visible on touch | `components/rowactions.html` |
| `Field` · `SearchField` | Labelled text input (`multiline` for long values) · the canonical search/filter input | `preview/17` · `components/searchfield.html` |
| `AmountField` | Money / numeric input with a currency-or-unit adornment (`prefix`/`suffix`), `md` + `lg` sizes — now for non-money numerics (rates, percentages, units) | `components/amountfield.
| `MoneyField` | The canonical **money editor** — amount plus its ISO currency code as one control; the code sits right, either a searchable picker or locked to static text (`currencyEditable={false}`). Optional leading `sign` + `tone` for signed amounts | `components/moneyfield.html` |
| `CurrencySelect` | Currency-**only** picker — the same ISO list, search box and keyboard behaviour as `MoneyField`'s segment, in standard Select chrome (account currency, base / reporting currency) | `components/currencyselect.html` |html` |
| `NoteField` | Multi-line note / description input with a live `len/max` character counter | `components/notefield.html` |
| `ReferenceNumberField` | Single-line **mono** input for a counterparty identifier (a contract's `referenceNumber`): `0/64` counter, live length + hidden-character (Cc/Cf/Co/Cn) errors that never echo the value, one-click removal of hidden characters, trim on blur, **no native `maxLength`** so a paste is never silently truncated. Ships `REFERENCE_NUMBER_RULES` (`maxLength`, `normalize`, `validate`, `stripHidden`) — the client half of the DTO's rules | `components/referencenumber.html` |
| `ReferenceNumber` | Read-only display of that identifier — `tag` glyph + mono value; **renders nothing when null**; `highlight` marks the search match; `copyable`; `size="sm"` (list meta line, ellipsized) / `"md"` (detail tile, wraps) | `components/referencenumber.html` |
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
| `BreakdownTile` | Labelled icon·label·count distribution tile (By type / By status / By currency), closing with a ruled total row — `total` is **on by default** and sums the rows' counts; pass a number/node for a total the rows' arithmetic can't give (a net, node counts), or `false` for buckets that don't sum | `components/breakdown.html` |
| `Alert` · `EmptyState` | Inline severity message · one-sentence absence + CTA (`variant="line"` for the thin muted sentence) | `preview/22` |
| `EmptyLine` · `RecordSection` | The thin empty sentence · record-body band (divider + refused-write notice + built-in empty line, custom view) | `components/empty-and-section.html` |
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
| `RecordCard` · `RecordBody` · `InfoTileGrid` · `SectionDivider` | **The expandable record card** every record list is built from — dense identity header + a body whose order is fixed by the component (alert → details → content → sections) · `RecordBody` is that body, extracted so a table-based record list (`RecordTable`'s expanded detail row, `inTable`) renders the identical panel · the auto-fitting `InfoTile` grid the `details` slot is made of (the record's full field set; `dense` for many-short-fact record types) · the uppercase-label + rule + mono-meta divider that introduces each band/section | `components/recordcard.html` · `components/record-card-rules.html` |

**Form controls**

| Component | Purpose | Specimen |
|---|---|---|
| `Switch` · `Checkbox` · `RadioGroup` | On/off · multi-select (+ indeterminate) · single choice — native inputs under styled chrome | `components/controls.html` |
| `SegmentedControl` | 2–3 inline options (e.g. party-kind selector) | `components/controls.html` |
| `Combobox` | Searchable single-select, optional inline create (`clearable`, `loading`, per-option icon) | `components/controls.html` |
| `MultiSelect` | Checkbox-list filter with count badge — every ledger header (any-of match) | `components/controls.html` |
| `TagMultiSelect` · `TagChips` | Multi-tag picker for forms · read-only tag-set display (caps + `+N`) | `components/tags.html` |
| `TransactionTagPicker` | Required single-tag picker for a record the tag **names** — Combobox in a FieldShell, each option led by the tag's own icon (`TransactionTagIcons.glyph`), selected tag's description as the help line, tags already planned for marked `in use`, archived marked in words, gated inline create, and the two no-control states (nothing left to plan for · tag list unavailable) | `components/transaction-tag-picker.html` |
| `TagIconPicker` (+ `TransactionTagIcons`) | The transaction-tag icon chooser — a closed grid of the API's icon catalogue with a leading **Default** cell (`null` → `local_offer`), 2-D arrow keys, and a caption naming the selection in words. `TransactionTagIcons` ships the catalogue (`All`, `Default`, `isKnown`, `glyph`, `order`, `resolve`) — `resolve` is a client preview of the server's `displayIcon` rule; render the server value | `components/tagiconpicker.html` · [Transaction tag icons](docs/components-transaction-tag-icons.md) |
| `MatchIndicator` | Per-cell AI-match annotation — source + confidence **as text** (`Suggested by AI` / `Created here` / `You chose` / `No match`), plus the sub-threshold **Use ‹name›** / dismiss action and the No-match **Create ‹name›** action (Analyze dialog) | `components/matchindicator.html` |
| `DatePicker` · `FileUpload` | Bare calendar popover (ISO value, keyboard grid) · drag-and-drop upload (rename/retype/remove rows) | `components/upload.html` · `components/uploadcap.html` |
| Consent-gate disclosure states | The analyze-file gate once its four processor-disclosure values are served rather than compiled — skeleton / resolved / degraded, with the affirmation disabled until it resolves | `components/consentgate.html` |
| `DateRangePicker` | Inline filter-bar range pill — two `DatePicker`s joined by a dash (icon + caption, ordered `{from,to}`, clear) | `components/daterangepicker.html` |
| `DateField` | The labelled form-field form of `DatePicker` (`DatePicker` in `FieldShell`) | `components/upload.html` |
| `ImageCropDialog` | The product's **one** client-side crop + downscale dialog — mounted by the contact image (picture / logo) and the user profile picture. Three labelled ranges + pointer drag, per-instance control ids, `min(instance cap, surface cap)` resolved inside, `onUpload` delegate | `components/image-crop-dialog.html` |
| `ProfilePictureField` | The signed-in user's own profile-picture control, in two variants — labelled `row` (text buttons) and `overlay` (the mark is the control: actions on hover/focus + a persistent badge). Driven by the `ImageVersion` token, with the pre-claim disabled window | `components/profile-picture.html` |

**Typed registry pickers** — the single-selects all delegate to one shared engine, **`TypeSelect`** (`components/typeselect.html`): the base Select's themed trigger + popover, each row a colored category glyph + label with the **selected check pinned far right**, and optional `groups` for sectioned lists (Assets / Liabilities). Don't use `TypeSelect` directly — reach for the domain wrapper below; each feeds its canonical registry in (value = enum key, pass `types` to subset). The `-MultiSelect` siblings remain thin wrappers over `MultiSelect`.

| Component(s) | Registry constant | Specimen |
|---|---|---|
| `TypeSelect` | _(shared engine — wrappers feed it)_ | `components/typeselect.html` |
| `AccountTypeSelect` | `ACCOUNT_TYPES` (+ `ACCOUNT_TYPE_GROUPS`) | `preview/25` |
| `ContactTypeSelect` / `-MultiSelect` | `CONTACT_TYPES` | `preview/25`, `preview/33` |
| `ContactMethodLabelSelect` | `ADDRESS_LABELS` · `EMAIL_LABELS` · `PHONE_LABELS`, scoped by `ContactLabelScope` — the one picker whose options depend on a **sibling field** (the parent contact's type) | `components/contact-method-label-select.html`, `preview/36b` |
| `FileTypeSelect kind="account"` / `-MultiSelect` | `ACCOUNT_FILE_TYPES` | `preview/27` |
| `FileTypeSelect kind="transaction"` / `-MultiSelect` | `TRANSACTION_FILE_TYPES` | `preview/27` |
| `FileTypeSelect kind="taxStatement"` / `-MultiSelect` | `TAX_STATEMENT_FILE_TYPES` | `preview/27` |
| `FileTypeSelect kind="property"` / `-MultiSelect` | `PROPERTY_FILE_TYPES` | `preview/27` |
| `PropertyEventTypeSelect` | `PROPERTY_EVENT_TYPES`, `PROPERTY_EVENT_TYPE_MATRIX`, `PROPERTY_EVENT_SYSTEM_ONLY` | `components/property-event-type-select.html` |
| `ContractTypeSelect` | `CONTRACT_TYPES` | `components/typeselect.html` |
| `ContractPartyRoleSelect` | `CONTRACT_PARTY_ROLES`, `CONTRACT_PARTY_ROLE_MATRIX` | `preview/58c` |
| `BudgetCategoryTypeSelect` | `BUDGET_CATEGORY_TYPES`, `BUDGET_CATEGORY_DIRECTION_OPTIONS` | `components/typeselect.html` |

**Data tables**

| Component | Purpose | Specimen |
|---|---|---|
| `Table` (+ `SkeletonRow`) | Read-only sortable data table (controlled sort) | `components/data.html` |
| `SortSelect` (+ `SortHelpers`) | The filter-bar **"Sort by"** control — curated field select + typed direction toggle bound to one `{key,dir}`; `SortHelpers` owns default directions, typed labels, and the stable null-last/id-tiebreak `sortRows` for hand-rolled lists | `components/sortselect.html` |
| `RecordTable` (+ `SortHeader` · `ActionMenu` · `MetaTile`) | The admin/ledger table — sort (uncontrolled or `sort`/`onSortChange`-controlled) + expand-to-detail + inline edit + row menu | `components/record.html` |
| `TxnTable` | THE transactions ledger (Transactions · Accounts · Budgets · Dashboard); `hideAccount` to drop a column. Leading avatar: direction tone + the row's `displayIcon` glyph | `components/txntable.html` |
| `FilesTable` | THE files surface — a `RecordTable` preset (Accounts · Transactions · Files) | `components/filestable.html` |
| `Pager` | The shared list pager for **server-paged** pages — Prev/Next + the canonical `Showing X–Y of N` (`0 results` when empty); `aria-disabled` no-op at bounds, focus never lost | `preview/30` |

**Account-record pieces**

| Component | Purpose | Specimen |
|---|---|---|
| `AccountTypeChip` · `AccountStatusChip` | Type chip (glyph + Asset/Liability) · status chip (dot + label + date) | `components/custodian.html` |
| `CustodianChip` · `CustodianSelect` | "Held at" link display · optional custodian picker (extends `Combobox`) | `components/custodian.html` |
| `ContactChip` | Read display of a linked/tagged contact (type glyph + name; archived + Unavailable states). How tagged People — Contacts of type Person — Journal links, and merchants read | `components/contactchip.html` |
| `ContactAliases` | The **Aliases** section of a contact record — the alternative-name tiles + their ⋯ menus, the add / edit dialog, the duplicate / cap rejections, one polite live region | `components/contact-aliases.html`, `preview/62` |
| `ContractStatusChip` | A contract's DERIVED status (Draft / Ready / Upcoming / Active / Paused / Expired / Archived) as a chip | `templates/contracts` |
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
| `Sparkline` · `LineChart` | Axis-less trend strip · axis'd chart with gridlines + delta, negatives, point kinds | `preview/24` · `preview/64` · `components/linechart.html` |
| `StepChart` | The dated-value card — LineChart's shell on a real time axis: staircase line, today marker (solid up to it, dashed past), hollow dot on a scheduled entry, in-force (never scheduled) figure + latest-change delta, `lines` to compare several histories, `scale` auto · indexed · absolute, `controls` / `controlsEnd` head slots, `showFigure` | `components/stepchart.html` |
| `TermHistoryChart` | `StepChart` whose head is its controls — quiet § Terms multi-select (what is plotted) left, Change / Value toggle (how it reads) right. Takes plain resolved series; owns one-unit-per-axis, stable leased colours, a four-line cap, never-empty. Backs the contract terms chart | `components/termhistorychart.html` |
| `Donut` · `DonutLegend` | Allocation ring (watermark hole) + slice ledger · the ledger standalone | `preview/26` |
| `AllocationDonuts` | Asset / liability pair — two recessed wells, each a stacked `Donut`. Plain one-currency slices in; the sub-line states what is counted and excluded. Accounts (accounts only) and Dashboard (accounts + owned property) | `components/allocationdonuts.html` |
| `Timeline` · `TimelineItem` | Vertical rail history list — the alternative rendering of an effective-dated record table | `components/timeline.html` |

### Token map — the ones you reach for most

Tokens live in `colors_and_type.css` (268 total). MudBlazor `--mud-palette-*` names are canonical; a `--color-*` alias layer mirrors them for non-Mud consumers.

| Intent | Token | Note |
|---|---|---|
| Page / surface / divider | `--mud-palette-background` · `--surface` · `--mud-palette-divider` | per-theme |
| Primary text / secondary | `--mud-palette-text-primary` · `--text-secondary` | secondary AA-safe |
| Brand fill / brand text | `--tide-400` (dark) / `--tide-600` (light) · `--brand-text` (`--tide-ink` on light) | tide-as-text steps to `--brand-text` for AA |
| Secondary accent | `--sea-400` | informational only |
| Money | `--finance-income` (mint) · `--finance-expense` (coral) · `--finance-pending` (amber) | never tide/sea |
| Categorical (tags/charts) | `--violet-500` · `--chart-1…6` · `--chart-grid` · `--chart-axis` · `--chart-axis-strong` | charts step darker in light; informational axis (zero baseline, "now" line) uses `-strong` for 3:1, drawn at `stroke-width: 2` + `vector-effect: non-scaling-stroke` so the ratio survives the scaled `viewBox` |
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
| Contracts | `Contracts.jsx` | `templates/contracts` |
| Properties | `Properties.jsx` (+ `PropertyEstimates` / `PropertyEvents`) | `templates/properties` |
| Journal | `Journal.jsx` (record cards + `JournalPhotoGallery`) | `templates/journal` |
| Tasks | `Tasks.jsx` (`TaskBoard` kanban + list view) | `templates/tasks` |
| Calendar | `Calendar.jsx` (`CalendarGrid` month + week/day/agenda + header calendar filter) | `templates/calendar` |
| Files | `Files.jsx` | `templates/files` |
| Transaction Tags (icon avatar + `TagIconPicker` in the dialog) · Contacts · Currencies · Exchange rates | `TransactionTags.jsx` · `Contacts.jsx` · `Currencies.jsx` · `ExchangeRates.jsx` | `templates/transaction-tags` · `contacts` · `currencies` · `exchange-rates` |
| Users · Roles · Settings | `Users.jsx` · `Roles.jsx` · `SystemSettings.jsx` | `templates/users` · `roles` · `settings` |
| Legal documents | `LegalDocuments.jsx` | `templates/legal-documents` |
| Analysis log | `FileAnalysisLog.jsx` | `templates/analysis-log` |
| User Account · Preferences | `Account.jsx` · `Preferences.jsx` | `templates/user-account` · `preferences` · `account-2fa` |
| Login · Confirm email | `Login.jsx` · `ConfirmEmail.jsx` | `templates/login` · `confirm-email` |
| Forgot · Reset password | `ForgotPassword.jsx` · `ResetPassword.jsx` | `templates/forgot-password` · `reset-password` |
| Register · Accept terms | `Login.jsx` (`Register`) · `AcceptTerms.jsx` | `templates/register` · `accept-terms` |


### Components — full index

Every typed component in `components/` (each a `.jsx` + `.d.ts`):

`AccountSmartTagsSection` · `AccountStatusChip` · `AccountTypeChip` · `AccountTypeSelect` · `ActionMenu` · `AddRow` · `Alert` · `AllocationDonuts` · `AmountField` · `Avatar` · `Badge` · `BrandMark` · `BreakdownTile` · `BudgetCategoryTypeSelect` · `Button` · `CalendarGrid` · `CapacityField` · `Card` · `CardBody` · `CardHeader` · `CardSelect` · `Checkbox` · `Chip` · `Collapsible` · `ColorSwatchSelect` · `Combobox` · `ContactAliases` · `ContactChip` · `ContactMethodLabelSelect` · `ContactSelect` · `ContactTypeMultiSelect` · `ContactTypeSelect` · `ContractPartyRoleSelect` · `ContractStatusChip` · `ContractTypeSelect` · `CoordinateField` · `CurrencySelect` · `CustodianChip` · `CustodianSelect` · `DateField` · `DatePicker` · `DateRangePicker` · `Delta` · `Donut` · `Drawer` · `EmptyLine` · `EmptyState` · `ErrorSummary` · `EventRail` · `Field` · `FieldShell` · `FileTypeMultiSelect` · `FileTypeSelect` · `FileUpload` · `FilesTable` · `FormRow` · `HomeownerAssociationSelect` · `IconButton` · `ImageCropDialog` · `InfoTile` · `InfoTileGrid` · `JournalPhotoGallery` · `LineChart` · `MIcon` · `MatchIndicator` · `Menu` · `MetaTile` · `Modal` · `MoneyField` · `MultiSelect` · `NoteField` · `NumberField` · `PageHeader` · `PageSizeSelect` · `Pager` · `PasswordChangeForm` · `PasswordRules` · `ProblemAlert` · `ProfilePictureField` · `PropertyEventTypeSelect` · `RadioGroup` · `RecordBody` · `RecordCard` · `RecordSection` · `RecordTable` · `ReferenceNumber` · `ReferenceNumberField` · `RegistryMultiSelect` · `RegistrySelect` · `RevealPanel` · `RowActions` · `SearchField` · `SecretClearDialog` · `SecretClearOnSaveDialog` · `SecretSettingField` · `SectionDivider` · `SegmentedControl` · `Select` · `SettingField` · `SettingRow` · `SeverityIcon` · `Skeleton` · `SortHeader` · `SortSelect` · `Sparkline` · `Spinner` · `StatTile` · `StepChart` · `StepperField` · `Switch` · `Table` · `Tabs` · `TagChips` · `TagIconPicker` · `TagMultiSelect` · `TaskBoard` · `TermHistoryChart` · `TextInputField` · `TimeField` · `Timeline` · `Toast` · `TodoStatusChip` · `Tooltip` · `TransactionTagPicker` · `TxnTable` · `TypeSelect`
---

## Content fundamentals

**Voice.** Plain English, second person ("you"), short sentences. The product never tells you what to do with your money — it tells you what's *in* it. No motivational copy ("Let's crush your goals!"), no emoji, no exclamation points outside error messages.

**Casing.** Title Case for navigation, page titles, primary buttons, and column headers. Sentence case for body copy, helper text, and toast messages.

**Numbers.** Always tabular figures (`font-variant-numeric: tabular-nums`). **Money is written as the amount followed by its ISO 4217 code — `1,234.56 USD`, never `$ 1,234.56`.** Symbols are not used for figures anywhere in the product: Odyssey is multi-currency and several shipped currencies share a glyph (`$` for USD and CAD, `kr` for NOK and SEK), so a symbol is ambiguous exactly where the figure matters — and the code matches the `MoneyField` the amount was typed into (which also carries its code on the right of the box) and the `CurrencyCode` the API stores. The currency's own `MinorUnits` sets the decimals (JPY renders none). Negative amounts use a minus sign and the expense color — not parentheses. **Glyph spacing.** The sign leads, then a **figure space** (U+2007 — digit-width, and not collapsible HTML whitespace), then the digits, then a single space before the code: `− 1,234.56 USD`, `+ 3,250.00 USD`. An **unsigned** amount carries no leading pad at all — `1,234.56 USD` — because on a headline tile a padded slot reads as stray indentation, and money columns align the way they always have: right-aligned, tabular figures, code trailing. One helper writes the lead (`moneySlot(n, signed)` in `data.js`), read by `money()` / `signedMoney()` / `moneyCompact()` and the per-feature `insMoney` / `subMoney` / `taxMoney`. Never glue a `+` onto a `money()` string to make a signed figure (it lands outside the slot and breaks the column) — call `signedMoney()`. The one place a **symbol** still appears is the Currency admin record, where `Symbol` is a stored field being edited.

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


### Scales added for consistency

- **Type** — besides display…overline, `--fs-micro` (10px, chart axes and mono stamps), `--fs-label` (13px, dense mono labels), `--fs-figure` (22px, record headline figure) and `--fs-amount` (30px, large amount inputs). `components.css` uses no raw px font sizes except two intentional glyph sizes (the 64px donut watermark, the 12px recurrence mark).
- **Icons** — `--icon-xs` 14 · `--icon-sm` 16 · `--icon-md` 18 · `--icon-lg` 20 · `--icon-xl` 24 · `--icon-2xl` 28. The `.material-icons` base size sits in `:where()`, so a component class sizes a glyph without `!important`.
- **Tints** — every `*-soft` / `*-border` token is derived with `color-mix()` from its base colour at the theme's `--tint-soft` (14% dark · 12% light) / `--tint-border` (30%). Change the base, and the tint follows. Don't hand-write `rgba()` tints.
- **Density** — `data-density="compact"` on any container switches `--row-h`, `--control-h`, `--control-pad-y`, `--iconbtn-size` and `--cell-pad-y` to the dense step (MudBlazor `Dense`). Buttons, inputs, select triggers, icon buttons and table cells all read these.
- **Starting a new screen** — use the **App shell (blank)** template (`templates/app-shell/`): nav, `PageHeader` and an empty state, with theme and density props.
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

## Iconography

**Primary set: Material Icons (filled).** Loaded from Google Fonts in `wwwroot/index.html` and consumed via MudBlazor's `@Icons.Material.Filled.*` constants. The codebase already uses these icons in `NavMenu.razor`, so they are canonical:

| Concept | Material Icon |
|---|---|
| Dashboard | `space_dashboard` |
| Accounts | `account_balance_wallet` |
| Budgets | `pie_chart` |
| Insurance | `shield` |
| Contracts | `handshake` |
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

## Feature & reference docs

Per-feature component detail and reference-data vocabularies live one file each in `docs/`, so this README stays at foundations + catalog. Read the relevant file before building or changing that feature.

**Components & features:** [data table, menu & form controls](docs/components-data-table-menu-form-controls.md) · [server pagination](docs/components-server-pagination.md) · [Dialogs](docs/components-dialogs.md) · [Contact image (profile picture / logo)](docs/components-contact-image-profile-picture-logo.md) · [User profile picture](docs/components-user-profile-picture.md) · [Budgets page](docs/components-budgets-page.md) · [Account custodian](docs/components-account-custodian.md) · [Account detail chips & menu conventions](docs/components-account-detail-chips-menu-conventions.md) · [Account value estimates](docs/components-account-value-estimates.md) · [Net-worth history chart](docs/components-net-worth-history-chart.md) · [Tax Statements page](docs/components-tax-statements-page.md) · [Contracts page](docs/components-contracts-page.md) · [Properties page](docs/components-properties-page.md) · [Contract terms](docs/components-contract-terms.md) · [Contract events](docs/components-contract-events.md) · [Property events](docs/components-property-events.md) · [Event rail](docs/components-event-rail.md) · [Journal module (Journal + Tasks)](docs/components-journal-module-journal-tasks.md) · [Calendar module (Calendar)](docs/components-calendar-module-calendar.md) · [Import & export (vCard / iCalendar)](docs/components-import-export-vcard-icalendar.md) · [Transactions page](docs/components-transactions-page.md) · [Transaction tag icons](docs/components-transaction-tag-icons.md) · [File viewer](docs/components-file-viewer.md) · [Analyze file](docs/components-analyze-file.md) · [Mail transport settings](docs/components-mail-transport-settings.md) · [Analysis log](docs/components-analysis-log.md) · [License / ToS acceptance](docs/components-license-tos-acceptance.md) · [Files page](docs/components-files-page.md) · [Authentication](docs/components-authentication.md) · [Users](docs/components-users.md) · [User Account](docs/components-user-account.md)

**Reference data:** [Contact types](docs/reference-data-contact-types.md) · [Contact-method labels](docs/reference-data-contact-method-labels.md) · [Contact aliases & lifecycle dates](docs/reference-data-contact-aliases-lifecycle-dates.md) · [File types](docs/reference-data-file-types.md) · [Term kinds](docs/reference-data-term-kinds.md) · [Account types (Property and Vehicle retired)](docs/reference-data-account-types-property-and-vehicle-retired.md) · [Contract types](docs/reference-data-contract-types.md) · [Contract party roles](docs/reference-data-contract-party-roles.md)

---

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
