# Components — Contract smart-tag scope

> Part of the [Odyssey Design System](../README.md) docs. Frontend counterpart of *Contract Smart-Tag Scope — Backend (Draft v5)*. No shipped code yet.

A contract's **Smart tags** section matches transactions that carry a watched tag, are paid to a contact party, **and are dated inside the contract's smart-tag scope**. The scope is the contract's own date range for that match. It is copied from the term when the contract is created and edited separately afterwards, so a final paycheck, next June's holiday pay or a deposit refund weeks after a lease ends can still count on the contract. The term itself stays as entered.

Specimens: `components/smarttagscope.html` (the field and the prompt), `components/contractsmarttags.html` (the section with the scope line). Reference build: `ui_kits/web/Contracts.jsx` (`ContractSmartTags`, `saveEdit`, `saveScope`), `ui_kits/web/AddContractModal.jsx` (`EditSmartTagScopeModal`, the sync prompt), `ui_kits/web/contracts-data.js` (backfill, `conScopeFromTerm`, the mock R2).

## The data

- `ExistingContract.smartTagScopeStart` / `smartTagScopeEnd` — `DateTime?`, always UTC midnight with a trailing `Z`. `null` means **open** on that side. The term dates keep their raw, `Kind`-less form; the difference is deliberate.
- `UpdateContract` carries the same two fields with **full-replacement** semantics: omitted or `null` opens that side.
- `NewContract` has no scope fields. The server copies the term (§8 rule 4) and the `201` body carries the result.
- `ContractListItem` has no scope. Nothing on the collapsed row shows it.

## Components

**`SmartTagScopeField`** — the editor. Two `DateField`s, **Match from** and **Match until**; an empty field is an open side and its placeholder says so (*No start limit* / *No end limit*). Below them:

1. **The strip** — term on one row, scope on the other, on a shared axis. An open side runs to the track edge and ends dashed; a one-off contract's term is a single point. It is decorative (`aria-hidden`).
2. **The relation sentence** — the same comparison in words, e.g. *Starts with the term, and runs 4 months past the term's end (31 Aug 2026).* This is what carries the meaning, and it is a live region.
3. **Shortcuts** — **Copy the contract term** (disabled when the scope already equals it) and **Remove both limits** (disabled when both sides are already open).

The field **never validates against the term**. A scope before the start or after the end is valid and is the purpose of the feature.

**`SMART_TAG_SCOPE_RULES`** — the client half of the server's `SmartTagScopeRules`:

| Member | Rule |
|---|---|
| `normalize(v)` | §8 rule 2. A bare day or an offset-less time is taken as written; `Z` or `±hh:mm` converts to UTC first, then truncates (`2027-06-30T00:30:00+02:00` → `2027-06-29`). |
| `fromTerm(term)` | §8 rule 4. One-off → open/open. Otherwise the stored term's calendar days, no conversion; an inverted legacy term keeps its start and opens its end. |
| `validate(scope)` | §8 rules 1 + 3. Each side within 1900-01-01…9999-12-31 (*Enter a date between 1 Jan 1900 and 31 Dec 9999.*), then start ≤ end, keyed `end` (*The scope end must not be before its start.*). Same day is valid. |
| `toWire(day)` | `YYYY-MM-DDT00:00:00Z`. The client never sends an offset. |
| `formatRange` · `describe` · `formatDay` · `equals` | Display helpers shared by the dialog, the prompt and the section. |

**`SmartTagScopeSyncPrompt`** — the "also update the scope?" band in **Edit contract**. It renders only when the term dates changed in this edit **and** the scope differs from what the new term would give. It names the current scope (*It stays 1 Mar 2024, no end unless you change it…*) and offers one checkbox: *Also set the smart-tag scope to ‹new range›*. **Unchecked by default.** The motivating case — adding an end date to an ongoing employment contract — must leave the scope open with no extra step.

## Surfaces

**Smart tags section (scope line).** The `Dated` item shows the window from the response (`scope.from` / `scope.toExclusive`, last covered day = one before `toExclusive`) followed by a muted note placing it against the term: *same as the term* · *includes dates after the term* · *includes dates before and after the term* · *narrower than the term* · *one-off contract*. The line ends in **Edit scope** (`AccountSmartTagsSection`'s new `scopeAction` prop), shown only with `contracts.update` and `transactions.read`.

**Edit smart-tag scope dialog** (`EditSmartTagScopeModal`). Title *Edit smart-tag scope*, icon `date_range`, primary **Save scope** — disabled while unchanged or while the order error stands. Bounds errors show on submit; the order error shows live. Save is a contract `PUT` that changes only the two scope fields and carries every other field forward. Announced as *Smart-tag scope saved.*

**New contract dialog.** Unchanged — no scope controls and no note. The server copies the term on create.

**Edit contract dialog.** The sync prompt sits under the term fields. Unticked, the save carries the stored scope forward unchanged; ticked, it writes `fromTerm(new term)`.

## Copy changes

- Empty state: *…dated inside the smart-tag scope and paid to a contact party, read here.*
- No match: *No transaction inside the smart-tag scope, paid to a contact party, carries the watched tags yet.*
- The `InvalidTerm` (3) state — *The term ends before it starts* + **Edit dates** — is removed. The ordinal stays reserved in `CON_EMPTY`; the server never emits it.

## Carry-forward

`PUT /api/contracts/{id}` is a full replacement, and an omitted scope **opens** it. Every write that rebuilds `UpdateContract` from a loaded record carries `smartTagScopeStart` / `smartTagScopeEnd` forward, exactly like `Ready`, `Signed`, `IsArchived`, `IsPaused` and `referenceNumber`: Edit (unless the prompt is ticked), Pause/Resume, Archive/Restore, Mark ready / Mark signed / Unsign. In the kit those writes spread the previous record; in the shipped client both `new UpdateContract { … }` sites — `ContractsCard.razor.cs` and `CreateContractDialog.razor.cs` — name the two fields, and `ContractPauseSurfaceTests.RequiredStamps` lists them.

## Not in this design

- Per-smart-tag scopes, relative grace periods ("end + 3 months") and party-date scoping (backend non-goals 2–4).
- A contract event for a scope change. The server writes a log line; the page shows nothing beyond the save announcement.
- Any scope display on the collapsed row or in the contract list.
