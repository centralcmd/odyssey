# Readme

`OdysseyContext` is the single application-domain `DbContext`. It owns:

- **Finance** — accounts, transactions, budgets, tags, subscriptions, contracts, insurance policies,
  tax statements, the Files store (`FileMetadata`/`FileBlob`) and the file-analysis tables, plus the
  `Currencies` reference table (164 ISO-4217 rows seeded from `HasData` in `OnModelCreating`, so the
  initial migration carries them and nothing needs to seed them at runtime).
- **Journal** — entries, tasks, their tags and attachments.
- **Photos** — the photo library, its tags, people and albums.
- **Calendars** — calendars, events and recurrence patterns.
- **Contacts** — the Contact aggregate (people and organisations) with its person/organisation
  details and address/email/phone children.
- **Identity and auth** — the ASP.NET Identity schema plus `UserProfile`, `UserPreference`,
  `SystemSetting`, the encrypted `SystemSettingSecret` store and the three legal-acceptance tables.

## Why one context

This project is the former `Odyssey.Finance.Context`, `Odyssey.Journal.Context` and
`Odyssey.Application.Context` merged. The reason is referential integrity, not tidiness. EF cannot
declare a relationship whose principal lives in a different model, so every reference across a context
boundary was a bare key — validated by a lookup service on write and swept by a guard on delete, with
nothing stopping a write path that forgot to call either. One context makes them declarable, and
`OdysseyContext.OnModelCreating` declares them under **Cross-module foreign keys**:

| Reference | On delete |
|---|---|
| `Transaction.ContactId`, `Subscription.ContactId`, `Account.CustodianId`, `AccountFile.IssuedBy`, `FileAnalysisCandidateTransaction.MatchedContactId` → `Contact` | `SET NULL` |
| `ContractParty.ContactId` → `Contact` | `CASCADE` |
| `InsurancePolicy.InsurerId` → `Contact` (required) | `RESTRICT` |
| `Photo.FileId`, `JournalEntryAttachment.FileId`, `JournalTaskAttachment.FileId` → `FileMetadata` | `CASCADE` |

They are declared as relationships with **no navigation properties** (`HasOne<Contact>().WithMany()`),
so the modules stay one-directional in the source — a finance entity still has no `Contact` to
`Include`, and the Mapster projections are unchanged — while the database gets the real constraint.

### A library photo and its file are deleted together, both ways

`Photo.FileId` cascading is only half of it. Deleting a **photo** also deletes its file, in
`PhotoService.Delete`. That **reverses issue #321 §7**, which had the file survive its library record —
read that section without this note and the current behaviour looks like a bug. It was changed because
the two directions disagreed about which object was the durable one: a file delete destroyed the photo
and all its curation (tags, people, albums, journal placements), while a photo delete carefully
preserved the file. One of the two had to give, and treating the pair as one thing is the rule that
holds in both directions.

The safety catch is `IFileReferenceGuard`. Nine tables carry a cascading FK to `FileMetadata`, so
deleting a file as a side effect of deleting a photo would silently strip it off a transaction, a tax
statement or a journal entry that also holds it — the database obliges without complaint. The guard
refuses with a `409` naming the other holders, and deletes nothing: not the file, and not the photo.
**A new table referencing `FileMetadata` must be added to that guard**, or a photo delete starts
silently destroying rows in it.

The lookup services (`IContactLookup`, `IFileLookup`, `IPhotoLookup`, `IContactReferenceGuard`) are
**kept**, and are not redundant:

- they build the read-path projections the DTOs need without an `Include`;
- they turn a violation into a `400`/`409` with an explanation, which a raw FK error cannot;
- the EF InMemory provider enforces no foreign keys at all, so they are the only implementation the
  fast test tiers (`Odyssey.Core.Tests`, `Odyssey.Api.Tests`) ever see. The constraints themselves are
  covered by `Odyssey.IntegrationTests` against real MariaDB.

### User attribution foreign keys

Columns across the model name the user who created, updated, attached, uploaded, requested or reviewed
a row — `CreatedByUserId`/`UpdatedByUserId` on `Calendar`, `CalendarEvent`, `JournalEntry`,
`JournalTask`, `Photo`, `PhotoAlbum` and `RecurrencePattern`; `AttachedByUserId` on the file-link
tables; `FileMetadata.UploadedByUserId`; `FileAnalysisJob.RequestedByUserId`; and
`FileAnalysisCandidateTransaction.ReviewedByUserId`.

They were bare strings while identity lived in its own context, so deleting a user left every one of
them naming an account that no longer existed, and no transaction could span the two contexts to fix
it. Every one is now a real FK with **`SET NULL`**, and that direction is the design decision:

- these rows are **shared** data — a household's photos, journal and attachments — so they must
  survive the departure of whoever touched them;
- `RESTRICT` would make anyone who has ever created something permanently undeletable;
- `CASCADE` would destroy the shared record because one contributor left.

Nulling drops the name and keeps the record, which is what the read path already expects:
`IUserDisplayNameResolver` takes a nullable id and answers `"Unknown user"`. The columns are declared
without navigations, like the cross-module keys, and un-annotated for length so EF takes 255 from
`AspNetUsers.Id` — a mismatched width is refused outright by MariaDB (errno 150).

`LicenseAcceptances.UserId` and `TermsOfServiceAcceptances.UserId` are the **deliberate exception** and
carry no foreign key: they are compliance records that must outlive the account, so `users.delete`
pseudonymizes them in the same transaction rather than cascading them away (see
`UserAdministrationService.DeleteAsync` and `Legal/LicenseDocumentProvider.cs`, which lives here so the
API and the demo seeder hash the shipped `LICENSE` identically). `UserProfile` and `UserPreference` go
the other way — both `CASCADE`, which is why preferences live here rather than in a context of their
own.

`Odyssey.IntegrationTests/UserAttributionForeignKeyTests` pins all of this at the database; EF InMemory
enforces no foreign keys, so the fast tiers never exercise it.

The rule reaches one table that is **not** an entity: `_InsurancePolicyFileRelocation` (below) carries
`AttachedByUserId` with the same `SET NULL` key, because a ledger recording who attached a document
must outlive that person's account exactly as the document does.

### The insurance relocation ledger

`_InsurancePolicyFileRelocation` is not part of the EF model. It is an operational record, written by
the `MoveInsurancePolicyFilesToRenewals` migration (issue #26), of every `InsurancePolicyFiles` row it
moved onto a renewal period before `DropInsurancePolicyFiles` removed that table.

It carries the **full source payload**, not just ids, because it is the only surviving record of those
rows and the sole basis for `Down`. It has the same three foreign keys the dropped table had —
`CASCADE` to `InsurancePolicies` and `FileMetadata`, `SET NULL` to `AspNetUsers` — which is what lets
`Down` reinsert into a table that has those keys without ever violating one: a parent deleted since
`Up` has already cascaded its ledger row away, so there is nothing stale to restore.
`DestinationPolicyRenewalFileId` and `DestinationPolicyRenewalId` deliberately carry no key, because
they must survive a detach or a period deletion.

It is retained indefinitely. It is small, it is the reversibility mechanism, and there is no later
phase to dispose of it. **Do not add an entity type for it** — the tables an EF model owns are the ones
the application reads and writes, and nothing outside the migration touches this one.

### Permission claims are not seeded here

`AspNetRoleClaims` rows are reconciled at runtime by `RoleClaimSeeder` in `Odyssey.MigrationService`,
not seeded by `HasData`. Adding or removing a claim is an edit to `PermissionClaims` and
`RolePermissions` and needs **no migration**.

They used to be a `HasData` seed, and that is worth knowing only so it is not reintroduced:
`IdentityRoleClaim.Id` is an int identity, so the seed assigned ids positionally from a counter running
across all four role lists. Appending a claim anywhere but the very end shifted the id of every claim
after it, which scaffolded a migration full of `UpdateData` operations renumbering unchanged rows. The
workaround was to hand-write a raw-SQL migration per addition at fresh explicit ids and strip the
renumbering out of the scaffold — which left the model snapshot and every real database deliberately
disagreeing on claim ids. Reconciling on `(RoleId, ClaimType, ClaimValue)` and letting the database
assign ids removes the problem instead of managing it.

**Roles** are still seeded by `HasData`: their ids are fixed GUIDs, so they have none of this trouble.
So is `SystemSetting`, whose `Key` is a natural primary key — a plain `HasData` insert, with a null
`UpdatedBy` meaning no administrator has ever taken ownership of the row, which is what the settings
page's provenance line reads. `SystemSettingSecret` carries no seed at all: an absent row means *not configured*, which
is a secret's correct initial state.

Issue #8 added four such rows — `EmailSmtpHost`, `EmailSmtpPort`, `EmailUseStartTls` and
`EmailClientBaseUrl` — in the `AddEmailTransportSettings` migration, seeded with `587` and `true` for
the two typed keys and the **empty string** for the two string ones. The empty values are the real
ones, not placeholders awaiting an adoption step: there is no path from configuration or the
environment into this store, so a fresh deployment starts with mail switched off until an
administrator sets a relay at `/settings`. A compile-time `InsertData` cannot see an operator's
environment variable in any case, and would silently overwrite their value with the shipped default if
it tried. `DemoDataSeeder` sets `EmailClientBaseUrl` for the Development and Testing stacks only, and
skips a row whose `UpdatedBy` is non-null — ownership, never a value comparison, which cannot tell
"never touched" from "deliberately set back to the default".

### One database

Because this is one model with keys throughout, its halves **cannot be pointed at different
databases**. There is one connection string, `OdysseyConnection`.

Two check constraints are declared on the model and so are reproduced by the initial migration:
`CK_ContractParties_ExactlyOneTarget` and `CK_TransactionFiles_Type_AllowedValues`.

## Database Migrations

Prerequisites:

```bash
dotnet tool install --global dotnet-ef
```

Create a new migration — replace `<MigrationName>` with a real name:

```bash
dotnet ef migrations add <MigrationName> --project "./Odyssey.Context/" --startup-project "./Odyssey.Api/Odyssey.Api.csproj" --context OdysseyContext
```

Apply migrations:

```bash
dotnet ef database update --project "./Odyssey.Api" --context "OdysseyContext"
```

Scaffolding resolves the provider through `Odyssey.Api` and there is no `IDesignTimeDbContextFactory`,
so `UseInMemoryDatabase` must be false, the environment must not be `Testing`, and
`ConnectionStrings__OdysseyConnection` must be set — otherwise the context binds to the InMemory
provider and the generated migration is unusable.

In normal operation nobody runs `database update` by hand: `Odyssey.MigrationService` migrates the
context on start, and the API waits for it to exit successfully.
