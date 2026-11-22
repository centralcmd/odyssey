# Contracts, party roles and the contracts roll-up

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**A contract party is one-of-THREE** — an `Account`, a `Contact` ("Institution") or a `Property`
(issue #208). An earlier third target, `ContractParty.InsurancePolicyId`, was dropped when the design
system reduced parties to two kinds, and its ordinal stays a permanent hole: `ContractPartyKind` is
`Account = 0`, `Institution = 1`, `Property = 3` — never `2`, which a stale payload read as
`InsurancePolicy`. `ContractPartyRole.Property` (18, what the contract is *about*) and
`ContractPartyKind.Property` (the target *is* a property record) are different enums answering
different questions; no validation links them. A property party exposes only
`ContractPropertyReference` (id, name, type) under `contracts.read` — never an address, registration
number or VIN — and the write stays gated on `contracts.update` alone, which is licensed only while no
role holds it without `properties.read` (`AuthorizationPolicyTests` pins that). `PropertyService.Delete`
removes the property's party rows by tracked `RemoveRange`, stages a `PartyRemoved` event on each
contract and writes the shared `ContractPartyAudit` line per row — deliberately stricter than
`AccountService.Delete`, which relies on the FK cascade alone.

**Which party roles are legal is decided by the contract's TYPE, and the matrix is declared ONCE**
(issue #157). `ContractPartyRoleMatrix` lives in `Odyssey.Dtos/Finance/`, which holds zero project
references and is reachable from both the API and the WASM client, so the write-path validator and the
party picker name **one** symbol — the same precedent `SettingItem.Rule` and `SystemSettingsBounds` set.
A client-side *copy* of a server rule is the defect CLAUDE.md already forbids for caps; a shared
declaration is what avoids it. **81 of the 210 cells are legal** (`ContractPartyRoleGuardTests.LegalCellCount_Is81Of210`
pins it — read the count there, not here), and guard tests also pin the per-type legal and suggested sets, and the universality of `Guarantor`, `Broker` and
`Other`.

Rules about the matrix that are easy to get backwards:

- **The universal set is a TRIO, not a pair.** `Guarantor` joined `Broker` and `Other` on every type —
  a party standing behind another's obligation belongs to no particular kind of agreement. Because
  `Cell` composes `allowedBeyondUniversal` with `UniversallyAllowed`, a role in **both** lists yields a
  duplicated entry in `LegalFor`, which fails the distinctness guard on the ordinary path. Promoting a
  role means deleting it from every column that named it.
- **`Object` (17), `Property` (18), `Collateral` (19) and `Account` (22) name what a contract is
  ABOUT**, not who stands on a side of it. `Object` reaches eight types; `Property` is Rental, Purchase and Other;
  `Collateral` is Loan, Deposit and Other — **suggested** on Loan and only **allowed** on Deposit, a
  deliberate asymmetry (collateral is central to a loan and incidental to a deposit). The two exclusions are deliberate and stated so they are not "fixed"
  later: Employment's object is the employee's labour, which `Employee` already names, and Insurance's
  is already `Insured` — "the person, account or thing covered" — and a second name for one concept
  would split where the covered thing is recorded. `Account` is the ledger account a loan or deposit
  is booked in — **suggested** on Loan and Deposit, allowed on Other, nowhere else. Like
  `ContractPartyRole.Property`, it shares a name with a `ContractPartyKind` member and answers a
  different question: the *role* says what the party is to the agreement, the *kind* says which table
  the target lives in. No validation links them.
- **The "exactly two suggested roles" invariant is RETIRED, not loosened** (issue #169 §4.4). The shape
  is now 4 / 3 / 2 / 1: four for Insurance and Loan, three for Rental, Purchase and Deposit, two for
  the remaining named types, one for `Other`. The count stays pinned, at a new number.
- **`Deposit` is the mirror of `Loan`, with its own roles** (issue #187). `Depositor` (20) and
  `Custodian` (21) are separate members, **not aliases** of `Lender`/`Borrower`, and the two columns
  reject each other's counterparties — so a report never has to guess whether a `Lender` row is a
  creditor or a depositor. `Other` still permits **every** live role; a role appended later must widen
  that column too, and `OtherType_PermitsEveryLiveRole` fails otherwise. The `Custodian` *role* is
  independent of the account-level custodian (`Account.CustodianId`, the `Custodian` record) — neither
  is synchronised with nor validated against the other; don't "unify" them. Nor does `Custodian` block
  a contact delete the way `Beneficiary` does.

Further rules that are easy to get backwards:

- **`Suggested` carries no server-side meaning.** The validator only ever asks whether a cell is legal;
  the tier exists so the picker's ordering is declared beside the legality it must stay consistent
  with, rather than being re-derived client-side where the two could drift.
- **A rejected role is a `422`, never a `400`.** The body is well-formed and every value in it is a
  real member — what fails is the *combination*, which is the derived-bound case CLAUDE.md
  distinguishes from a compile-time one. So it is `DomainUnprocessableException`, keyed on `role`, and
  the check lives in `ContractPartyService` (the party writes, split out of `ContractService` by issue
  #287 M1) because legality depends on the *contract's* type, which model validation cannot see.
- **Only a type CHANGE is checked, and that condition lives in `ContractService`, not at either call
  site.** A contract keeping its type is never refused however illegal an existing party's role is:
  such a row is a legacy one, and freezing every other field on its contract would not help — the
  party endpoint is where it gets fixed. Both the controller's structured pre-check and the service's
  unconditional refusal read the same helper, so they cannot disagree about when the rule applies.
- **`Unspecified` (0) and `ServiceProvider` (5) are retired and their ordinals are permanent holes.**
  Reusing one would make an unmigrated row mean something new rather than nothing. With `Unspecified`
  gone, `Role` is `[Required]` **and nullable** on `ContractPartyRequest`: left non-nullable an omitted
  role binds to `0` and `[EnumDataType]` rejects it with an invalid-enum-value message, which
  misdescribes what the caller did wrong.
- **The `Beneficiary` role blocks deletion of its contact, with no FK behind it.** The
  `ContractParty → Contact` key stays `CASCADE` because every other role should keep cascading,
  so `IContactReferenceGuard` is the *only* enforcement — which is why the rule has to reach
  `IsReferencedByRestrictedLinkAsync` (the defence-in-depth probe) and not just the blocker query, and
  why `ClearAndCascadeReferencesAsync` must **exclude** that role: otherwise the cascade and the staged
  detach hit one row inside one `SaveChangesAsync` and EF raises `DbUpdateConcurrencyException` on the
  *ordinary* success path.

The transactional detach valve destroys rows in another domain, so its claim is demanded **per
blocker class actually present** — `contracts.update` — and the presence determination and the
destruction read **one snapshot inside the delete's transaction**. One class holds that list today;
the per-class machinery stays because it is what keeps those two readings on one snapshot. A controller
pre-flight would be a second snapshot, and a row inserted between the two would be destroyed by a
caller never asked to prove the claim for it (CWE-367). That is what `DomainForbiddenException` exists
for: the ordinary case is still an action-level `[Authorize]` policy or a controller pre-flight, and
this is the one shape neither can serve.

**`ContractType` reads in a different order from the one it is stored in.** The four members added
after the original set carry ordinals **4–7** (`Insurance`, `Subscription`, `Purchase`, `Membership`),
`Loan` appends at **8** and `Deposit` at **9**, while `Other` keeps **3** — an ordinal is a wire and persistence contract and is never renumbered, so
a stored `3` cannot be made to mean `Insurance`. Only the *reading* order pulls `Other` last — and
`Loan` in after `Purchase`, since a mortgage was filed as a `Purchase` before that member existed, with
`Deposit` directly after its mirror `Loan` — and it lives in one place: `OdsTypeRegistries.ContractTypes`. That matters beyond tidiness, because
`ContractTypeOf`'s documented fallback for an out-of-range value is the **trailing** entry — reorder
the registry so something other than `Other` ends it and every stale row silently renders as that
instead. `OdsTypeRegistriesTests` pins both halves.

**The contracts roll-up computes what the file COSTS, and both halves read the same rows.** The
in-force `Amount` terms of the **Active** contracts are summed into a run rate (monthly and
yearly, converted to the caller's display currency) and separately projected forward into the "next
charges" the header panel lists. Four things about it are easy to get backwards:

- **`IntervalCount` is a divisor.** 300 every three months is 100 a month. Reading it as a factor
  triples every quarterly line.
- **A fee with no cadence is excluded by construction.** `OneTime`, `PerOccurrence` and `PerUnit` name
  an occasion rather than a rhythm, so there is no rate to project and no next occurrence to predict —
  and a percentage fee carries no due amount at all.
- **An unconvertible currency is NAMED, never folded in at 1:1.** A silent 1:1 under-reports a strong
  currency and over-reports a weak one, and either reads as a whole figure rather than a partial one.
  The exclusion applies to the per-type split too, so the rows always sum to the totals.
- **Nothing is scheduled.** A charge date is a term's cadence anchor (`AnchorDate`, else
  `EffectiveFrom`) stepped forward at read time and never written back. Month steps are measured from
  the *original* anchor, so a fee anchored on the 31st recovers its day-of-month after a short
  February instead of drifting to the 28th forever.

**"Ending soon" is a slice of Active, not a fifth status** — it is already counted in `Active`, so the
four real buckets still sum to `TotalContracts` and this one deliberately does not. Its window and the
charge window are admin-editable settings (`ContractEndingWindowDays`, `ContractChargeWindowDays`), and
**`EndingWindowDays` travels to the client on `ContractSummary`** rather than living as a page
constant: the page interpolates it into the row label, so a local copy would caption a row with one
number while the server counted by another — the client-side-copy defect the root `CLAUDE.md` forbids.

`DELETE /api/contacts/{id}?detachBlockingLinks=true` is the supported release valve: it removes every
`Beneficiary` contract party naming the contact and deletes it **in one transaction**, composing
`contacts.delete` with `contracts.update` rather than adding a claim. Its `409` counterpart is
**claim-conditional**, and
that conditional lives in `ContactsController` — `DomainConflictException` carries a message and nothing
else, and the domain service has no `ClaimsPrincipal`, so neither it nor `GlobalExceptionHandler` could
shape one.
