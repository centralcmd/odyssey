# Properties and the property event log

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**A property is a `Contact`-shaped aggregate that only contract parties reference** (issue #167;
issue #208 added the party link, which cascades with the property and never blocks its delete).
`Property` carries a `Type` (`RealEstate`/`Vehicle`) and exactly one 1:1 detail row sharing its key
(`RealEstateDetails`/`VehicleDetails`), plus two **sibling** tables — `PropertyEstimates` and
`PropertySmartTags` — rather than a second owner widened onto `AccountEstimates`/`AccountSmartTags`.
In this codebase a new owner gets its own table: #173 declined the polymorphic widening for smart tags
and #191 undid it for terms. Rules that are easy to get backwards:

- **`Type` is immutable, unlike a contact's.** `PUT` with a different type is a `422` keyed on `Type`,
  never a silent re-type and never a dropped detail row — a house that becomes a car is a different
  asset whose estimate history no longer describes it. `PUT` is also **not an upsert** (`404` on an
  unknown id), which deliberately differs from `PUT /api/accounts/{id}`.
- **The two query-shaped estimate rules live once, in `EstimateEffectiveDating`** — the
  duplicate-`EffectiveFrom` conflict and the current-as-of resolution (greatest `EffectiveFrom`, ties to
  the newest `CreatedAtUtc`). Both `AccountEstimateService` and `PropertyEstimateService` call it, and
  the list's `sortBy=value` uses its `CurrentValue` selector as a correlated subquery. A divergence there
  fails *silently*, which is why they are shared while the scalar checks (non-negative value, owner
  currency) stay duplicated like the smart-tag services' are. A source-lint in
  `EstimateEffectiveDatingTests` fails on a second copy in any estimate file under `Odyssey.Core/Finance/`.
- **The registration number and VIN are personal data** behind `properties.read` alone, and so is the
  address *as part of the property*. The address itself is a linked `Location` since issue #380, which
  `locations.read` reads directly (product decision 3). None of them goes in a cross-claim projection, an
  embed, an error body or a log line; messages echo the route id.
- **`TransactionTagService.CountDeleteBlockers` has a fifth clause** for `PropertySmartTags`, and the
  `RESTRICT` key behind it is the backstop, not the guard — the same shape as the other four.
- **The estimate figures outside the estimate endpoints follow `properties.estimates.read`, not
  `properties.read`.** `ExistingProperty.EstimateCount`/`CurrentEstimatedValue*` and
  `PropertySummary.Value` are `null` without it, and `sortBy=Value` is a `403` — the order alone ranks
  the properties by worth. The shape is `ExistingAccount.ContractCount` following `contracts.read`.
  The smart-tag count is `properties.read` data and is always filled.

**`AccountType.Property` (6) and `AccountType.Vehicle` (7) are retired, and the ordinals are permanent
holes** (issue #218). The *RetirePropertyAndVehicleAccountTypes* migration (folded into the v0.51.0 baseline) moved every such account onto a property
**under the same GUID**; an account holding transactions was kept as an archived `OtherAsset` (a property
holds none), so one GUID can legitimately name an account *and* a property — nothing may resolve a bare id
across both tables. `CK_Accounts_AccountTypeNotRetired` forbids 6/7 in the database and `[EnumDataType]`
on `NewAccount` returns `400`. Never reuse either ordinal.

**A property also carries an event log, and it shares ONE table with the contract log** (issue #209).
`ContractEvent` and `PropertyEvent` are TPH branches of the abstract `OwnedEvent`, stored in `Events`
(formerly `ContractEvents`) with an `OwnerKind` discriminator. **This is a recorded, deliberate exception
to the sibling-table rule above** — a product-owner decision, not an oversight — so do not "fix" it back
into two tables. What contains it, and must not be loosened:

- **Disjoint ordinals.** `ContractEventType` stays below **100**; `PropertyEventType` lives in
  **100–199**. `CK_Events_TypeMatchesOwner` enforces it in the database (with an explicit
  `Type IS NOT NULL`, because TPH makes the shared column NULLable and an UNKNOWN CHECK passes), and
  `PropertyEventTypeGuardTests` is its build-time twin — a contract member appended at 100 fails a unit
  test, not a production insert.
- **Exactly one owner, the one the discriminator names** — `CK_Events_ExactlyOneOwner`.
- **Every query is owner-scoped twice**: the `DbSet` by discriminator, and the service by the route's
  owner id, so a contract event id on a property route (or the reverse) is a `404`.
- **The shared `Type` column keeps `DEFAULT 8`** (contract `Other`). `PropertyEvent.Type` restates that
  default only because EF demands shared columns agree; its sentinel `0` and the fact that no property
  member is `8` make an omitted property type fail the range CHECK instead of passing.
- **Which types a property may carry is `PropertyEventTypeMatrix`** (`Odyssey.Dtos/Finance`, shared with
  the client picker, the `ContractPartyRoleMatrix` precedent): 17 legal cells per property type, 34 of
  42. Four members are **system-only** (`Archived`, `Unarchived`, `AcquisitionDateCleared`,
  `DisposalReversed`) — refused on `POST`, keepable but never introducible on `PUT`. Both refusals are
  `422` keyed `type`, and so is a future `occurredAt` on this log — unlike the contract log's `400`,
  which predates it and keeps its wire behaviour. The tolerance itself is `EventFieldRules`, shared.
- **System rows are recorded by `PropertyService.Create`/`Update`** (which therefore take the acting
  `userId`) through `PropertyEventRecorder`, staged on the caller's context in the same save. Their
  prose is `PropertyEventCatalogue` and takes **only** the transition and its date — never a property
  name, detail, note or estimate figure. There are deliberately **no** automatic estimate events: an
  event is read under `properties.read`, and an "estimate added" line would disclose what
  `properties.estimates.read` withholds.
