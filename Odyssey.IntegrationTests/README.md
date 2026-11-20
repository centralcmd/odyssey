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

`ODYSSEY_REQUIRE_TIER` is a comma-separated list of `integration`, `e2e`, `e2e-api` or `all`; an unknown
name fails the run rather than being ignored. The rule lives in `TestShared/TestTierGate.cs`, linked
into all three self-skipping test projects.

## A private database: copy, don't migrate

A test that needs a database of its own at the current schema calls
`fixture.CreateMigratedDatabaseAsync(name)`. It drops `name` if it exists, recreates it as a copy of
`odyssey_template` and returns its connection string. The fixture migrates that template through the
real migration chain once per container (`MigratedSchemaTemplate`). Locally a copy takes about 70 ms and
a migration from empty about 2.8 s.

A **migration test** (`MigrationSeam`) that needs the database *before* head calls
`fixture.CreateDatabaseAtMigrationAsync(name, "_SomeMigration")` (or
`CreateDatabaseBeforeMigrationAsync(name, "_ItsSubject")`) instead: the same copy, of a template migrated
to exactly that migration with every later one pending. Each target gets its own template database
(`odyssey_template_<16 hex digits of a hash of the migration id>`), built on first use and shared by every
test asking for the same target; tests asking for different targets build theirs independently.

Only tests whose point is that the chain applies from nothing still migrate for real: the **seeder
test**, `MigrationDriftIntegrationTests` and the drift-guard tests in `TermRenameMigrationTests`, which
need an empty database a copy cannot give them.

`MigratedSchemaTemplateTests` compares a copy with a database migrated the slow way, at head and at one
intermediate migration: every column, index, constraint, foreign key and table option, and every row
(`CHECKSUM TABLE`). A copy comes from `SHOW CREATE TABLE`, so the template refuses to build if a
migration ever creates an object that does not reproduce: a view, trigger, routine, event or sequence.

## Data migrations that shipped before production

A data migration's seam tests (its backfill, remap or relocation over seeded rows, and its `Down()` over
data) are retired once every production database has applied it: from then on the data path only ever
runs over an empty database, and a rollback is a backup restore. The tests for migrations up to
`v0.35.0` (`20260924202453_AddTextAndDateTimeTermKinds`) are gone for that reason. What a removed file
also asserted about the *current* schema — a constraint, an index, a foreign key's delete rule — was
kept, on a copy of head.
