# Components — Account value estimates

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The **Estimates** section (`AccountEstimates.jsx`) is a new zone inside the expanded account record (Accounts → account detail), positioned **above** the Terms section — the **§**-style one-word sibling (the `monitor` glyph) that leads the record. It backs the **AccountEstimate** feature: a time-versioned history of an account's **estimated value** — a single user-supplied money amount, in the account's own currency, effective from a date — for assets the transaction ledger can't represent (a house, a car, a valuable). The latest entry on or before a date is the value **in force** (implicit supersession — no `EffectiveTo`, step function, **identical resolution to `AccountTerm`**). It is deliberately the sibling of Terms so the two read and behave consistently; where it differs, it's because an estimate has no kind / unit / billing dimension — it is always one amount. Specimen: `preview/32-account-estimates.html` (Property with and without transactions + the empty state, chart / history / empty-state tweaks live); reference build: `ui_kits/web/AccountEstimates.jsx` + the `AddEstimateModal.jsx` dialog. Styled by `account-estimates.css` (the only net-new sheet, `.est-*`).

**It reuses the Terms anatomy, simplified to a single value.** Three stacked zones:

| # | Zone | What it shows · Maps to |
|---|---|---|
| 1 | **Value hero** | The current estimated value + a directional **change** chip vs the prior estimate, over a **value chart** of the estimate over time. Estimates hold flat between appraisals and extend to a dashed **Today** marker. The chart reads as a **step** line (discrete appraisals held flat) or a **smooth** value trend — tweakable. |
| 2 | **Current value** | The in-force estimate as the **headline**, with the **transaction balance** kept as a quiet secondary — the one place the "estimate replaces balance in net worth" decision reads. The `GET …/estimates/current` projection. |
| 3 | **History** | The full `GET …/estimates` list, newest first, as a **table** or a vertical **timeline** (tweakable). Each row shows the value, the **change** vs the prior estimate, and a status (**In force** · future-dated **Scheduled** · **Superseded**); editable / deletable in place. |

**The estimate is the headline; the balance stays quiet.** Today an account's `Balance` is the sum of signed transaction amounts — `0` for an Other-asset account with no transactions. When a current estimate exists it **replaces** the transaction balance in net worth (the spec's §9 *replace* policy): the section surfaces the estimate as the big mint figure and the transaction balance as a muted secondary, and the collapsed account row shows the estimate as the account's value (labelled **Est. value**), exactly as Terms surfaces the in-force rate. There is no extra "this counts toward net worth" prose — hierarchy carries it, with one quiet **In net worth** chip on the value tile.

**Value is asset worth, so it reads in the income color.** The hero figure, chart line/area, and the current-value tile use **`--finance-income`** (mint) — an estimate is a positive amount of worth, the same encoding the account balance uses when positive. The hero **glyph tile** carries the **account-type** color/icon (an Investment estimate reads in the investment hue), tying the section to its account. The change indicator is **directional** — mint ↑ for a gain, coral ↓ for a loss — since an asset's value moving up or down is unambiguous (unlike a rate's neutral delta in Terms). Brand **tide** stays out of it.

**Every account type is eligible.** Unlike Terms, estimates are **not** gated by account type — any account may carry one, so there's no eligibility kind-grid. A recommended practical subset (`OtherAsset` · `InvestmentAccount` · `PensionAccount` — asset accounts whose worth isn't transaction-derived; houses and cars carry estimates as Property records) is highlighted in the dialog and the guided empty state, but never enforced. The **empty state** has two tweakable treatments: a **standard** `EmptyState`, or a **guided** prompt that frames the value proposition and shows the transaction balance the estimate would stand beside.

**New / Edit estimate** (`AddEstimateModal.jsx`) is built on the shared DS **`Modal`**, like every dialog. Its field set mirrors the `NewAccountEstimate` DTO and enforces the spec's rules:

| Field | Maps to | Control · rule |
|---|---|---|
| Estimated value | `Value` | Hero money input. Required, **≥ 0**. |
| Currency | `CurrencyCode` | **Locked to the account currency** (shown read-only); the server rejects a differing currency (`400`). |
| Effective from | `EffectiveFrom` | DateField, required. Past or future allowed (future = Scheduled). An exact `(AccountId, EffectiveFrom)` duplicate is rejected — the server's `409`. |
| Note | `Note` | Optional, ≤ 512 chars. |

> **Stack reality check.** The section stands in for the account-nested `AccountController` estimate routes — `GET …/estimates` (history), `GET …/estimates/current` (the headline), `POST …/estimates` (New estimate), `PUT`/`DELETE …/estimates/{id}` (inline edit / delete), gated by the new **`accounts.estimates.read`** / **`accounts.estimates.write`** claims. Backed by `ExistingAccountEstimate` / `NewAccountEstimate` / `CurrentAccountEstimate` and the `AccountEstimate` entity (a sibling of `AccountTerm`, minus the kind/unit/billing columns). `ExistingAccount` gains a computed `CurrentEstimatedValue` (+ `CurrentEstimatedValueCurrencyCode`), populated server-side like `CurrentInterestRate`, and `AccountTotalsService` folds it into net worth per the replace policy. The prototype keeps the history in `data.js` (`accountEstimates`, keyed by account) and resolves current / series / supersession client-side (`OdysseyHelpers.currentEstimate` / `estimateSeries` / `estimatesForAccount`); wire it to the real endpoints in Blazor. The value chart is dependency-free SVG themed via the `--chart-*` tokens — sanctioned data viz, not illustration.
