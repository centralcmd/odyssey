# Move Account Terms to Contracts — Backend (Draft v1)

> **Frontend counterpart:** {{ FRONTEND_ISSUE }}

## 1. Overview

Terms (interest rates, fees, expected returns) can today be recorded directly on an account **or** on a
contract. Odyssey is consolidating on one owner: a term belongs to the **contract** an account takes part
in. This feature migrates every account-owned term onto a contract — reusing the account's existing
contract where exactly one fits, otherwise creating a `Deposit` (asset), `Loan` (liability) or `Other`
(unclassified) contract from the account's own data — and then removes account-level terms from the
backend. The migration interprets nothing it cannot derive: whatever it cannot map is left for the user
to complete and is recorded on the contract. This issue covers the backend half.

### Primary user value

- One place for terms: rates and fees live beside the agreement that sets them, with its parties,
  dates, events and roll-up.
- No silent data loss: every term is moved row-for-row (same id, value, history), and every contract the
  migration touched carries a system event listing what the user must review.
- A smaller surface: one term API, one claim pair, one validation path.

## 2. Goals and Non-Goals

### Goals

1. Move every `Terms` row with `AccountId` set onto a contract, in place (same `TermId`), in one
   `OdysseyContext` migration.
2. Resolve the target contract per account by the rule in §3.2 — reuse exactly one matching contract, else
   create one from the account's data (§3.3).
3. Record, per touched contract, one system `ContractEvent` listing everything that needs user attention
   (§3.5).
4. Remove account ownership from the schema: drop `Terms.AccountId`, its FK and index, and the
   `CK_Terms_ExactlyOneOwner` check; make `Terms.ContractId` `NOT NULL`.
5. Remove the account-term API (`/api/accounts/{accountId}/terms*`), the `accounts.terms.read` /
   `accounts.terms.write` claims, the account branches of the term service, and the account term
   projections on `ExistingAccount` (`TermCount`, `CurrentTerms`).
6. Re-target the demo seed so account terms are generated as contract terms under the same rules.
7. Publish, in this issue (§3.6), the exhaustive list of data that is lost or needs to be re-entered.

### Non-Goals (v1)

1. No change to `AccountEstimate` — it is not a term and stays on the account.
2. No inference of signing: created contracts are left **unsigned** (Draft); nothing sets `Ready`/`Signed`.
3. No synchronisation between `Account.CustodianId` and the contract's `Custodian`/`Lender` party after
   the migration — they are independent records (CLAUDE.md: "don't unify them").
4. No read-through of contract terms on the account endpoints (e.g. "terms of this account's contracts")
   — the account already lists its contracts.
5. No rename of `AccountCurrentTerm` (still used by `ExistingContract.CurrentTerms`).
6. No reversible `Down()` for the data (see §11).

## 3. Architecture Proposal

### 3.1 Components

1. **Data migration** — `Odyssey.Context/Migrations/<ts>_MoveAccountTermsToContracts.cs`: hand-written
   MariaDB SQL (`migrationBuilder.Sql`), same shape as `FoldRateTermKindsIntoFee` /
   `AddInsurancePolicyLinkCollections`. Data move first, schema drop last.
2. **Term service** — `Odyssey.Core/Finance/TermService.cs`: loses its account owner; contract becomes the
   only owner.
3. **Account service / DTOs** — `Odyssey.Core/Finance/AccountService.cs`, `Odyssey.Dtos/Finance/ExistingAccount.cs`:
   lose term count and current terms.
4. **API** — `Odyssey.Api/Controllers/TermsController.cs` deleted. Contract term endpoints in
   `ContractController` unchanged (stale Swagger text at l.305-309 corrected).
5. **Authorization** — `PermissionClaims` / `RolePermissions` lose `accounts.terms.*`;
   `RoleClaimSeeder` revokes the rows at next start.
6. **API client** — `AccountsApiClient` term methods deleted.
7. **Demo data** — `Odyssey.TestData/Generators/TermGenerator.cs` emits contract terms; `ContractGenerator`
   gains the contracts §3.2 would create.

### 3.2 Target contract resolution (per account holding ≥ 1 term)

Accounts without terms are **not touched**.

| `AccountType` (via `AccountClassification`) | Expected contract type |
|---|---|
| Asset (`Cash`…`OtherAsset`, 1–8) | `Deposit` (9) |
| Liability (`CreditCard`…`OtherLiability`, 9–15) | `Loan` (8) |
| Unclassified (`Unknown`, 0) | `Other` (3) |

Candidates = contracts of the **expected type** on which the account is a `ContractParty` in **any role**,
archived or not.

- **Exactly 1 candidate** → move the terms onto it. The contract's fields and parties are **not changed**
  (no `Object` party is added, the account keeps its existing role).
- **0 candidates** → create a contract (§3.3) and move the terms onto it.
- **≥ 2 candidates** → create a contract (§3.3), move the terms onto it, and flag the ambiguity (A2).

Contracts of a *different* type naming the account are ignored for matching (flag A3 when present).

### 3.3 Created contract

| Contract field | Source | Note |
|---|---|---|
| `ContractId` | `UUID()` | raw SQL bypasses EF identity |
| `Name` | `Account.Name` | 256 → 256 |
| `Type` | §3.2 table | |
| `Description` | `Account.Description` | 256 → 1024 |
| `ReferenceNumber` | `Account.AccountNumber` | 64 → 64; flagged (A6) |
| `StartDate` | `Account.Opened` | |
| `EndDate` | `Account.Closed` if `Closed ≥ Opened`, else `NULL` + flag (A7) | |
| `CompletionDate` | `NULL` | |
| `Archived` | `Account.Archived` | |
| `Paused`, `Ready`, `Signed` | `NULL` | → derived status **Draft** (or Archived) |
| `CreatedAtUtc` | `UTC_TIMESTAMP()` | |

Parties (≤ 2, well under `ContractMaxPartiesPerContract` = 25):

| Target | Role on `Deposit` | Role on `Loan` | Role on `Other` |
|---|---|---|---|
| The account | `Object` (17) | `Object` (17) | `Object` (17) |
| `Account.CustodianId` contact, if set | `Custodian` (21) | `Lender` (13) | `Other` |

All six cells are legal in `ContractPartyRoleMatrix` (Object is allowed on Loan/Deposit/Other; Custodian
suggested on Deposit; Lender suggested on Loan; Other permits every role). `FromDate`/`ToDate` = `NULL`.
If `CustodianId` is `NULL`, no institution party is created (flag A5).

### 3.4 Term transformation (in-place `UPDATE`, same `TermId`)

| Column | Rule |
|---|---|
| `ContractId` | resolved contract |
| `AccountId` | `NULL` |
| `CurrencyCode` | `Amount` terms (`ValueUnit = 1`) with `NULL` currency → `Account.CurrencyCode` (the value the API already resolved on read). `Percentage` stays `NULL`. |
| `Direction` | **Target type `Deposit` and `ValueUnit = Percentage` → `Incoming` (1).** Everything else unchanged (`Outgoing`). Flag every flipped row (A8). |
| `Label`, `LabelKey`, `ValueUnit`, `Value`, `Interval`, `IntervalCount`, `AnchorDate`, `EffectiveFrom`, `Note`, `CreatedAtUtc` | unchanged |

### 3.5 Attention record

For each contract the migration created or moved terms onto, insert one `ContractEvent`:

- `Type = Other` (8), `Source = System` (1), `OccurredAt = UTC_TIMESTAMP()`, `CreatedByUserId = NULL`
- `Title` = `Terms migrated from account "<Account.Name>"` (truncated to 256)
- `Description` (≤ 1024) = the applicable flag codes and one line each, from §3.6

The event is editable/deletable like any other system event — it is a to-do marker, not an audit log
(consistent with `ContractEvent`'s own remarks). The immutable record is a migration log line per account
(account id, contract id, created/reused, term count, flags).

### 3.6 Data loss and user attention — exhaustive list

**Lost (not recoverable after migration)**

| # | What | Why |
|---|---|---|
| L1 | Currency *fallback* on `Amount` terms | Before: a `NULL` currency followed the account's currency, including later changes. After: frozen to the account currency at migration time. |
| L2 | Original `Direction` of flipped rows | Every account term was forced `Outgoing`; flipped rows (A8) keep no record of it except the event text. |
| L3 | Terms deleted with their account | Before: deleting an account cascaded its terms. After: deleting the account removes only its `Object` party; the contract and its terms survive. **Behaviour change.** |
| L4 | Guest read access to these terms | Guest held `accounts.terms.read` but not `contracts.read` (see §7.2). |
| L5 | Per-term history events | Moved rows get no `ContractEvent`/log per term (only the one summary event, §3.5). |
| L6 | Account-level term count / current-term tiles | `ExistingAccount.TermCount` and `.CurrentTerms` removed from the API. |
| L7 | `TermExport.AccountId` | Always `NULL` after migration; column removed from the export document. |

**Needs user attention / refill** (flag codes used in the event text)

| Code | Condition | User action |
|---|---|---|
| A1 | Every **created** contract | Review; mark Ready and Signed with the real dates. Until signed it is Draft and **excluded from the run rate**. |
| A2 | ≥ 2 candidate contracts existed | Move terms to the right contract and delete the created one, or keep it. |
| A3 | Account is party on a contract of another type | Check whether terms belong there. |
| A4 | `Unknown` account → `Other` contract | Choose the real contract type (party roles may need changing first). |
| A5 | No `Account.CustodianId` | Add the institution as `Custodian`/`Lender`. |
| A6 | `ReferenceNumber` copied from the account number | Replace with the agreement's reference, if different. |
| A7 | `Closed < Opened` on the account | Set the contract's end date. |
| A8 | Percentage term flipped to `Incoming` on a Deposit | Verify — a negative rate or a percentage **fee** (e.g. platform fee %) should stay `Outgoing`. |
| A9 | Reused contract already has a series with the same `LabelKey` + `EffectiveFrom` | Delete or relabel one row — any edit of either returns `409` until then. |
| A10 | Reused contract now exceeds `ContractMaxTermsPerContract` | New terms are refused (`422`) until below the cap; edits still allowed. |
| A11 | Reused contract is archived | Terms are hidden with it; unarchive or move them. |
| A12 | `Amount` term currency filled from the account and that currency is inactive | Term cannot be re-saved until the currency is active or changed. |
| A13 | Account also carries estimates (`AccountEstimate`) | Nothing moved; informational only — estimates stay on the account. |
| A14 | Periodic `Amount` terms now on a Deposit/Loan | Once signed, they **count in the contracts run rate**; confirm the amounts. |
| A15 | Reused contract: account keeps its existing role, custodian contact not added | Add `Object`/institution parties if wanted. |

## 4. Data Model Changes

No new entity.

### `Term` (table `Terms`)

- **Removed:** `AccountId`, FK `FK_Terms_Accounts_AccountId` (CASCADE), index
  `IX_Terms_AccountId_LabelKey_EffectiveFrom`, check `CK_Terms_ExactlyOneOwner`, navigation `Term.Account`.
- **Changed:** `ContractId` `Guid?` → `Guid` (`NOT NULL`), FK → `Contracts` stays **CASCADE**.
- Index `IX_Terms_ContractId_LabelKey_EffectiveFrom` stays (serves the FK — InnoDB errno 1553).

### `Account`

- **Removed:** navigation `Account.Terms` and its `WithMany` configuration.

### Unchanged but relevant

- `ContractParty.AccountId` → `Accounts` **CASCADE**: deleting an account removes its `Object` party only
  (L3).
- `Account.CustodianId` stays; not synchronised with the new party (Non-Goal 3).

## 5. API Endpoints

### Removed

| Method | Path | Former claim |
|---|---|---|
| `GET` | `/api/accounts/{accountId}/terms` | `accounts.terms.read` |
| `GET` | `/api/accounts/{accountId}/terms/current` | `accounts.terms.read` |
| `POST` | `/api/accounts/{accountId}/terms` | `accounts.terms.write` |
| `PUT` | `/api/accounts/{accountId}/terms/{termId}` | `accounts.terms.write` |
| `DELETE` | `/api/accounts/{accountId}/terms/{termId}` | `accounts.terms.write` |

All five return `404` after removal (no route). No redirect/shim.

### Changed response shapes

- `GET /api/accounts`, `GET /api/accounts/{id}` → `ExistingAccount` **loses** `termCount` and
  `currentTerms`. Breaking wire change; licensed by the no-deployed-data premise (§11).
- `ExistingTerm` **loses** `accountId`; `contractId` becomes non-nullable.
- Admin data export document: `TermExport` **loses** `accountId`.

### Unchanged (the only term API)

1. `GET /api/contracts/{id}/terms` — `contracts.read`
2. `GET /api/contracts/{id}/terms/current` — `contracts.read`
3. `POST /api/contracts/{id}/terms` — `contracts.update`
4. `PUT /api/contracts/{id}/terms/{termId}` — `contracts.update`
5. `DELETE /api/contracts/{id}/terms/{termId}` — `contracts.update`

Swagger text on `POST` (l.305-309) is corrected: it still describes `TermKind` and a direction restriction
that no longer exist.

## 6. Supported File Types and Detection

> Not applicable for this feature — no file handling.

## 7. Security, Privacy, and Compliance

1. **Tenancy** — shared single-tenant finance domain; no ownership change.
2. **Permission claims** — `accounts.terms.read` / `accounts.terms.write` are **deleted** from
   `PermissionClaims`, `RolePermissions.AllClaims` and every role array. `RoleClaimSeeder` revokes the
   `AspNetRoleClaims` rows on next start; no migration. Issued cookies still carry the dead values until
   sign-out/in — harmless, no endpoint checks them. `TermVocabularyGuardTests` pins updated.
3. **Effective access change per role** (terms now read under `contracts.read`, written under
   `contracts.update`):

   | Role | Read before → after | Write before → after |
   |---|---|---|
   | Admin | ✓ → ✓ | ✓ → ✓ |
   | Owner | ✓ → ✓ | ✓ → ✓ |
   | User | ✓ → ✓ | ✗ → ✗ |
   | Guest | ✓ → **✗** | ✗ → ✗ |

   Guest **loses** read access (L4). Deliberate: not widening Guest to `contracts.read` (which exposes all
   contracts, parties and events). No role gains access.
4. **Read exposure** — moved terms become visible to `contracts.read` holders together with the contract's
   parties; for Admin/Owner/User that set already includes `accounts.read`. No new field.
5. **Write exposure** — no new write path; the migration adds parties by scalar `AccountId`/`ContactId`.
6. **Third-party flow / secrets** — none.
7. **Audit** — one migration log line per account (§3.5); the system `ContractEvent` is a to-do, not an
   audit record.
8. **Error disclosure** — removed routes return a plain `404`.

## 8. Validation and Mapping Rules

- The migration writes by SQL and **bypasses** `TermService.ApplyAndValidate`; every value it writes is a
  value that was valid under the account rules, and every rule difference is flagged (A8–A12) rather than
  "fixed".
- Removed from `TermService`: account owner resolution, rule V4 (account terms Outgoing only),
  `TermOwnerFacts.DefaultCurrencyCode`, `IsTermCapped = false` branch, `TermOwnerKind.Account`.
- Unchanged for contract terms: currency required on `Amount`, cap on create only, duplicate
  `(LabelKey, EffectiveFrom)` → `409`, direction free.
- `ContractPartyRoleMatrix` is **not** re-checked by the migration; §3.3 uses only legal cells, pinned by
  an AC.

## 9. Error Handling and Failure Modes

| Class | Behaviour |
|---|---|
| **MigrationLeftAccountTerms** | After the data steps, if any `Terms.AccountId IS NOT NULL` remains, the migration aborts (`SIGNAL SQLSTATE '45000'`) **before** any DDL, naming the count. No schema is dropped. |
| **MigrationInterrupted** | Data steps are idempotent (`INSERT … WHERE NOT EXISTS`, `UPDATE … WHERE AccountId IS NOT NULL`); re-run completes. DDL follows the `MigrationRunner` drift guard (issue #468). |
| **RemovedRoute** | `404`. |
| **TermEditOnDuplicateSeries** (A9) | Existing `409` problem details. |
| **TermCreateOverCap** (A10) | Existing `422`. |

## 10. Performance Targets

- Migration over the demo set: < 5 s.
- Linear in term count; one pass per statement, no per-row loop; ≤ 10 000 terms < 60 s on the dev MariaDB.
- No hot-path change except `GET /api/accounts` becoming cheaper (no term sub-queries).

## 11. Database Migration Approach

- One migration on **`OdysseyContext`**:

```bash
dotnet ef migrations add MoveAccountTermsToContracts \
  --project "./Odyssey.Context" \
  --startup-project "./Odyssey.Api/Odyssey.Api.csproj" \
  --context OdysseyContext
```

- `Up()` order:
  1. Temp mapping `account → (expected type, candidate count, target contract, created?)`.
  2. `INSERT` created contracts (§3.3), then parties.
  3. `UPDATE Terms` (§3.4).
  4. `INSERT` one system `ContractEvent` per touched contract (§3.5).
  5. Guard: abort if any `AccountId` remains (§9).
  6. Drop check, FK, index, column; alter `ContractId` to `NOT NULL`.
- `Down()`: restores the **schema** only (nullable `AccountId`, FK, index, check). Data is not moved back
  — lossy, documented in the XML summary, same precedent as `FoldRateTermKindsIntoFee`.
- Seeds nothing (`HasData` unchanged).
- Licensed by the standing premise that no deployed database holds data anyone must keep — **re-confirm
  before merge**. The migration itself is written so it would also be safe on real data.
- No feature toggle; no `SystemSettings` row; no secret.

## 12. Acceptance Criteria

1. After migration no `Terms` row has an owner other than a contract; `Terms.AccountId` does not exist and
   `Terms.ContractId` is `NOT NULL` (IntegrationTests, MariaDB).
2. Every pre-migration account term exists after migration with the **same `TermId`** and unchanged
   `Label`, `LabelKey`, `ValueUnit`, `Value`, `Interval`, `IntervalCount`, `AnchorDate`, `EffectiveFrom`,
   `Note`, `CreatedAtUtc`.
3. Asset account, no contract → one `Deposit` contract, account party role `Object`, custodian contact
   role `Custodian`; fields per §3.3; derived status Draft.
4. Liability account, no contract → `Loan`, `Object` + `Lender`.
5. `Unknown` account → `Other`, `Object` + `Other`; event contains A4.
6. Account with `CustodianId = NULL` → one party only; event contains A5.
7. Account party on exactly one contract of the expected type → terms moved there; that contract's fields
   and parties byte-identical to before; no contract created.
8. Account party on two such contracts → a new contract is created; event contains A2.
9. `Amount` term with `NULL` currency gets the account's currency; `Percentage` keeps `NULL`.
10. On a `Deposit` target, `Percentage` terms become `Incoming`, `Amount` terms stay `Outgoing`; on `Loan`
    and `Other` all stay `Outgoing`; event lists A8 per flipped row.
11. A9, A10, A11 are each detected and listed on a fixture built to trigger them.
12. Accounts with no terms: no contract, party or event created.
13. Running `Up()`'s data steps twice produces no duplicate contract, party or event.
14. A remaining account term aborts the migration before any DDL.
15. Deleting a migrated account leaves its contract and terms; removes only its `Object` party.
16. All five removed routes return `404`.
17. `accounts.terms.read/write` absent from `PermissionClaims` and every role; after `RoleClaimSeeder`,
    no `AspNetRoleClaims` row carries them.
18. `ExistingAccount` has no `termCount` / `currentTerms`; `ExistingTerm` has no `accountId`;
    `TermExport` has no `accountId`.
19. Guest gets `403` on `GET /api/contracts/{id}/terms` (E2E permission matrix).
20. Every party role written by §3.3 is legal per `ContractPartyRoleMatrix.LegalFor` (unit test over the
    six cells).
21. Demo seed produces no account terms and passes `DemoDataSeederTests`; seeded demo accounts with terms
    follow §3.2 (HighYieldSavings/CarLoanVolvo reuse their existing Deposit/Loan contracts).
22. `grep -rn "AccountsTerms\|accounts.terms" --include=*.cs .` returns nothing outside migrations.

## 13. LLM Prompt/Output Contract

> Not applicable for this feature.

## 14. Rollout Plan

### Phase 1 (MVP) — one PR

- Migration `MoveAccountTermsToContracts` + IntegrationTests fixture covering §12 1–15.
- Backend removal (§5, §7, §8), demo seed re-target, test clean-up.
- Client **compile-coupled removal** in the same PR (the claims and API-client methods it references are
  deleted): `AccountTermsSection`, account branches of `AddTermDialog` / `TermVisuals`, `AccountsCard`
  term badge/tiles/menu item. Any replacement UX is the frontend counterpart's.
- Docs: `Odyssey Design System/README.md` §terms note, `docs/deployment.md` claim table, release note
  listing L1–L7 and A1–A15.
