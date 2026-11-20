# Odyssey.IntegrationTests

Integration tests that run against a **real MariaDB engine** via
[Testcontainers](https://dotnet.testcontainers.org/), covering the subset of behaviour that EF
InMemory cannot represent:

- the actual EF migrations apply cleanly (the one context into a single `odyssey` database,
  mirroring how the app runs under Aspire);
- the demo seeder persists with referential integrity (no orphan currency references);
- foreign-key `ON DELETE CASCADE` is enforced at the database;
- `decimal(18,6)` and `datetime(6)` columns round-trip at full precision.

## Running

```bash
dotnet test Odyssey.IntegrationTests
```

**Requires Docker.** The fixture starts a `mariadb:11.4` container (waiting on the image's own
`healthcheck.sh`, since the Testcontainers MySql module's default probe uses a `mysql` client the
mariadb image no longer ships). The container is reaped automatically.

**Skip versus fail** (issue #257). Only one condition is a skip: **no Docker daemon is reachable**
(Testcontainers' `DockerUnavailableException`), so the tier is safe to include in a normal
`dotnet test` run on a machine without Docker. Everything that goes wrong once Docker *is* reachable —
an image pull, the container start or readiness wait, the provisioning SQL — **fails** the tier,
because a skip there is how CI used to go green having run none of it. Set
`ODYSSEY_REQUIRE_TIER=integration` to make the missing-Docker case fail too; CI should set it on the job
that is expected to run this tier:

```bash
ODYSSEY_REQUIRE_TIER=integration dotnet test Odyssey.IntegrationTests
```

`ODYSSEY_REQUIRE_TIER` is a comma-separated list of `integration`, `e2e`, `e2e-api`, `design-system`,
`perf` or `all`; an unknown name fails the run rather than being ignored. The rule lives in
`TestShared/TestTierGate.cs`, linked into every self-skipping test project.

**The perf tier.** The volume, memory and time-budget tests here (the 100,000-row calendar export, the
50,000-event import, the 100,000-transaction net-worth budget, the weather batch P95 targets) are
`[Trait("Category", "Perf")]` and skip unless `ODYSSEY_REQUIRE_TIER` names `perf`; with it named, a
missing Docker daemon fails them. The `perf.yml` workflow runs them on the release-please Release PR; locally:

```bash
ODYSSEY_REQUIRE_TIER=integration,perf dotnet test Odyssey.IntegrationTests --filter Category=Perf
```

## A private database: copy, don't migrate

A test that needs a database of its own at the current schema calls
`fixture.CreateMigratedDatabaseAsync(name)`. It drops `name` if it exists, recreates it as a copy of
`odyssey_template` and returns its connection string. The fixture migrates that template through the
real migration chain once per container (`MigratedSchemaTemplate`). Locally a copy takes about 70 ms and
a migration from empty about 2.8 s.

A **migration test** (`MigrationSeam`) that needs the database *before* head calls
`fixture.CreateDatabaseAtMigrationAsync(name, "_SomeMigration")` instead: the same copy, of a template
migrated to exactly that migration with every later one pending. Each target gets its own template database
(`odyssey_template_<16 hex digits of a hash of the migration id>`), built on first use and shared by every
test asking for the same target; tests asking for different targets build theirs independently. A data
migration's seam test is temporary (see below). Today the only callers are `MigratedSchemaTemplateTests`,
which uses an intermediate migration only as a fidelity sample, and the half-applied-Contract drift test in
`LocationConversionMigrationTests`.

Only tests whose point is that the chain applies from nothing still migrate for real: the **seeder
test**, `MigrationDriftIntegrationTests`, the drift-guard tests in `TermRenameMigrationTests` and the
fresh-database tests that drive `MigrationRunner` or the migrations job, which need an empty database a copy
cannot give them.

`MigratedSchemaTemplateTests` compares a copy with a database migrated the slow way, at head and at one
intermediate migration: every column, index, constraint, foreign key and table option, and every row
(`CHECKSUM TABLE`). A copy comes from `SHOW CREATE TABLE`, so the template refuses to build if a
migration ever creates an object that does not reproduce: a view, trigger, routine, event or sequence.

## Data migration tests are retired once production has applied them

A data migration's seam tests — its backfill, remap or relocation over seeded rows, an interrupted run
resuming, its `Down()` over data, "pre-existing rows read back as X" — are retired once every production
database has applied the migration. From then on the data path only ever runs over an empty database, and a
rollback is a backup restore, so the tests pin behaviour no database will ever exercise again while costing a
per-migration template build on every run.

**The rule for a new data migration:** its seam tests ship with it, and are removed in the first PR after the
release containing it reaches production. Removing them follows two rules:

- **Keep every assertion about the current schema**, moved onto a copy of head
  (`CreateMigratedDatabaseAsync`): a foreign key's delete rule, a `CHECK`, a unique index, a generated column,
  a column's type or collation, a seeded row. A seeded settings value belongs in
  `SystemSettingsSeedIntegrationTests`. Moving a check into a sibling file is fine; losing it is not.
- **Keep the `MigrationRunner` guards** — the drift guard and the pre-migration checks a fresh database still
  passes through — tested from an empty database or a head copy, since every fresh install runs them.

Two rounds of this have happened. The tests for migrations up to `v0.35.0`
(`20260924202453_AddTextAndDateTimeTermKinds`) went first. The rest went once production had applied
`v0.48.0`, whose newest migration is `20261119224858_StoreFetchedRatesInPublishedDirectionOnly`: no seam test
for a data migration remains in this project.
