# Reference data — File types

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


Files attach in **five contexts, each with its own enum** — a distinction worth getting right:

- **`AccountFileType`** (`Odyssey.Finance.Dtos/AccountFileType.cs`, field `FileType` on `ExistingAccountFile`) — documents filed against an **account**: **Other(0) · Message(1) · Statement(2) · Contract(3) · Tax(4) · Documentation(5) · InsurancePolicy(6) · LoanAgreement(7) · RepaymentSchedule(8) · PurchaseAgreement(9) · Valuation(10) · Warranty(11) · Registration(12) · Prospectus(13)**.
- **`TransactionFileType`** (`TransactionFileType.cs`, field `Type` on `ExistingTransactionFile`) — proof attached to a single **transaction**: **Receipt(0) · Invoice(1) · Other(2) · CreditNote(3) · Quote(4) · PaymentConfirmation(5) · Documentation(6)**.
- **`PropertyFileType`** (`Odyssey.Dtos/Finance/PropertyFileType.cs` + its `Odyssey.Context` mirror, field `FileType` on `PropertyFile`) — documents on a **property**: **Other(0) · Deed(1) · PurchaseAgreement(2) · Valuation(3) · Inspection(4) · Registration(5) · Insurance(6) · Warranty(7) · Receipt(8) · Maintenance(9) · Tax(10) · Drawing(11)**. `Other` is ordinal 0 on purpose, so an omitted type degrades to Other and never to Deed.
- **`TaxStatementFileType`** (`TaxStatementFileType.cs`, field `FileType` on `TaxStatementFile` — newly added) — documents on a **tax statement**: **TaxReturn(0) · TaxAssessment(1) · SupportingDocument(2) · Other(3)**.
- **`ContractFileType`** (`Odyssey.Dtos.Finance.ContractFileType` + its `Odyssey.Context` mirror, field `FileType` on `ContractFile`) — documents on a **contract**: **Signed(0) · Amendment(1) · Correspondence(2) · Other(3) · Draft(4)**. Registry: `OdysseyData.contractFileTypes` (`ui_kits/web/contracts-data.js`).

So `Receipt` is a real `TransactionFileType` member, not a missing `AccountFileType` value. As with contacts, each enum carries only a name; the **icon** and **color** live in canonical registries — `OdysseyData.accountFileTypes`, `transactionFileTypes`, `taxStatementFileTypes` and `propertyFileTypes` (`ui_kits/web/data.js`), siblings of `accountTypes` and `contactTypes`. Every file surface reads them, so a kind looks identical everywhere; a merged `OdysseyData.fileTypeByKey` lookup renders any kind from any of the four enums (the Files-table avatar, kind chip, the upload picker, the edit-file picker, the file viewer, the account-detail list). Specimen: `preview/34-data-file-types.html`.

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

**PropertyFileType** — files on a property *(new)*. Keys shared with another enum (PurchaseAgreement, Valuation, Warranty, Registration, Tax, Receipt, Other) reuse that enum's icon and color, so a key reads the same everywhere:

| Type | Enum | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **Deed** | 1 | `workspace_premium` | `0.74 0.15 290` (violet) | Title deed / skjøte, grunnboksutskrift. |
| **PurchaseAgreement** | 2 | `sell` | `0.79 0.14 60` (amber) | Purchase or sale contract. |
| **Valuation** | 3 | `price_check` | `0.80 0.15 140` (green) | Valuation / appraisal / takst. |
| **Inspection** | 4 | `troubleshoot` | `0.78 0.12 180` (teal) | Condition report / tilstandsrapport, EU-kontroll. |
| **Registration** | 5 | `app_registration` | `0.74 0.15 310` (purple) | Vehicle registration certificate / vognkort. |
| **Insurance** | 6 | `shield` | `0.74 0.15 30` (orange) | Insurance certificate or policy schedule. |
| **Warranty** | 7 | `verified` | `0.77 0.13 205` (cyan) | Warranty / guarantee. |
| **Receipt** | 8 | `receipt_long` | `0.80 0.15 150` (green) | Purchase receipt, invoice for an improvement. |
| **Maintenance** | 9 | `build` | `0.80 0.14 95` (yellow-green) | Service or maintenance record. |
| **Tax** | 10 | `request_quote` | `0.75 0.16 330` (magenta) | Property-tax / wealth-tax assessment. |
| **Drawing** | 11 | `architecture` | `0.76 0.12 240` (blue) | Floor plan, site plan, technical drawing. |
| **Other** *(default)* | 0 | `insert_drive_file` | `0.74 0.02 250` (neutral) | Anything else. |

**ContractFileType** — documents on a contract. Listed in **reading order**, which is the registry order the client mirrors:

| Type | Enum | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **Draft** | 4 | `edit_note` | `0.82 0.035 75` (warm neutral) | A pre-signature version of the agreement — proposal, negotiation or review draft. **A label only.** |
| **Signed** | 0 | `history_edu` | `0.72 0.16 295` (violet) | The executed agreement — the document of record. |
| **Amendment** | 1 | `edit_document` | `0.80 0.13 85` (amber) | An addendum, variation or amendment to the signed contract. |
| **Correspondence** | 2 | `forum` | `0.77 0.14 205` (cyan) | Letters, notices or email relating to the agreement. |
| **Other** *(attach default)* | 3 | `insert_drive_file` | `0.74 0.02 250` (neutral) | Anything outside the categories above. |

**Draft is appended, never inserted.** The ordinal is a wire and persistence contract (`int` column, numeric JSON), so `Draft` takes `4` and every existing member keeps its number. The registry puts it **first** for reading order; `Other` stays **last** because the client's unknown-ordinal fallback (`ContractFileTypeOf`) is the trailing entry — an older client receiving `4` renders it as *Other*. Picker defaults are explicit (`Signed` when a file is uploaded, `Other` on the API's attach default), so the order never changes what is pre-selected, and `Draft` is never a default. The upload guess offers `Draft` only when the filename says *draft*, *proposal* or *redline*.

**Draft is a label, not a state.** It is drawn in a low-chroma warm neutral so it sits apart from `Other`'s cool grey without borrowing a status hue. It shares the `edit_note` glyph with the contract's **Draft** status chip, so "draft" has one icon across Odyssey; the colour (and the chip vs. file-avatar shape) keeps the document label distinct from the status — it must never read like the contract's own **Draft / Ready** status chip, which it does not drive. Likewise nothing branches on `Signed`: both are self-asserted by any `contracts.update` holder and are never evidence that the agreement was executed. No validity rule, roll-up or permission changes with either.

> **Notes.** Each list is enum order with `Other` (the default in each) pulled last. The enums share only `Other` — different enum values, identical icon/color. Only account **`Statement`** is analyzable — the server rejects every other type, and `OdysseyHelpers.canAnalyze` keys off the kind, not the icon. Account types **6–13** and the new transaction / tax-statement types were added to cover what property, vehicle, loan, and investment accounts actually file; keep each registry's keys in lockstep with its C# enum. The upload picker is context-aware: `AddFileModal` shows account types, `AddTransactionModal` passes the transaction vocabulary, and the tax-statement upload passes the tax vocabulary.

### Document validity metadata (`AccountFile`, `ContractFile`, `PropertyFile`)

An `AccountFile` row — and, as of the contract- and property-document changes, a `ContractFile` and a `PropertyFile` row — carries four **optional, nullable** join-entity fields. They describe the *document's* validity, not the raw bytes (which live on `FileMetadata`), and they sit on the **link** row, so the same stored file filed against two records can carry a different period in each:

| Field | Type | Purpose |
|---|---|---|
| `ValidFrom` | `DateTime?` | When the document takes effect — e.g. an insurance policy start. |
| `ValidTo` | `DateTime?` | When it expires — policy end, warranty expiry. |
| `IssuedAt` | `DateTime?` | When the document was issued / signed. |
| `IssuedBy` | `Guid?` | Issuing institution — an FK to **Contacts**. |

Because they're nullable, existing attachments are unaffected — a document attached before the feature existed prints an em dash, never a guess. In the UI they surface in three places: the **FilesTable** validity columns (`validityColumns`) and detail well (`issuerFor` resolves the contact id to a name), the table's **Edit dialog**, and the **upload modal** (`AddFileModal` for accounts, `AddContractFileModal` for contracts, `AddPropertyFileModal` for properties) behind a quiet *Add validity* toggle per file. Transaction and tax-statement attachments don't carry them.

**One rule, four write paths.** Account attach / update and contract attach / update share a single service-layer rule: each date is normalised to UTC and checked against the range its column can store (**years 1000–9999**), and `ValidTo` is never before `ValidFrom`. Each failure is a per-field `400` — `errors["ValidTo"]`, `errors["IssuedBy"]`, or the offending date's own name — so the client renders it **on the control that holds the mistake**, not only in a toast. The front end mirrors both checks inline (the same messages, before the round-trip); it never enforces them on **read**, so a row that already violates the ordering still displays exactly as stored.

**Nothing derives a status from the pair.** No expiry badge, no "expiring soon" roll-up, no filtering or sorting by validity, and no tie between a document's term and its contract's own term — a predecessor agreement legitimately predates the contract and a warranty legitimately outlives it. The dates are recorded, shown and edited; that is the whole surface.

**A contract document's edit is narrower than an account file's.** `PUT /api/contracts/{id}/files/{fileId}` replaces the document **type and the four fields only** — the name belongs to the `FileMetadata` the link references — so the dialog opens as **Edit document** with no rename (`renameable={false}`). The type carries the obligation `*` (`requireType`): the update is a full replacement, `ContractFileType.Signed` is the enum's **zero** member, and an unsent type must be refused rather than defaulted into a claim that the file is the signed agreement. An omitted date or issuer **clears** the stored value. An **archived** contract refuses both writes, so the table drops Edit and Delete and the section states why in text instead of opening a doomed dialog. After a save the client re-reads `GET /api/contracts/{id}/files` — that contract's documents alone — rather than refetching the whole record. Specimen: `preview/59b-contract-document-validity.html`.

**A property document's edit is the contract one.** `PUT /api/properties/{id}/files/{fileId}` is a full replacement of type + the four fields, so the table runs `renameable={false}` + `requireType`. Its danger item reads **Detach** (`deleteLabel` / `deleteIcon` on `FilesTable`, here `link_off`): the verb removes the link row and the file stays in Files.

**Typed pickers.** One pair per enum, all thin wrappers over `Select` / `MultiSelect` with each option's icon in its category color: **`FileTypeSelect kind="account"`** / **`FileTypeMultiSelect kind="account"`** (the Files-page filter is wired to the latter), **`FileTypeSelect kind="transaction"`** / **`FileTypeMultiSelect kind="transaction"`**, **`FileTypeSelect kind="taxStatement"`** / **`FileTypeMultiSelect kind="taxStatement"`**, and **`FileTypeSelect kind="property"`** / **`FileTypeMultiSelect kind="property"`**. Value is the enum key; pass `types` to subset. `ACCOUNT_FILE_TYPES`, `TRANSACTION_FILE_TYPES`, `TAX_STATEMENT_FILE_TYPES` and `PROPERTY_FILE_TYPES` are exported on the DS namespace (contract documents use the `contractFileTypes` registry directly as the FileUpload / FilesTable `kinds`, not a typed picker) (mirroring the `OdysseyData` registries). Specimen: `preview/27-components-file-type-pickers.html` (live).
