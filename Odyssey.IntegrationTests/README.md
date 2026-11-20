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

## Parallel collections, one container

The tier is split into four xUnit collections that share **one** MariaDB container (`MariaDbFixture`
keeps the container, its startup outcome and the template cache in static state; each collection's
fixture awaits the same start, so a skip or a fault reaches every collection alike). Three run in
parallel; the fourth runs alone afterwards (`xunit.runner.json` states `parallelizeTestCollections`
and `maxParallelThreads: 4` rather than leaving them to the runner's core count):

| Collection | Holds | Parallel |
|---|---|---|
| `MariaDbCollection` (`MariaDb`) | The default: classes with private databases only — every migration (`MigrationSeam`) test among them | yes |
| `MariaDbPrivateDatabaseCollection` | More private-database classes, split out to balance the run (the template-fidelity and drift-guard suites, and the slow budget, job-run, instrument-price, weather and exchange-rate suites) | yes |
| `MariaDbSharedDatabaseCollection` | **Every class that keeps data in the shared `odyssey` or `odyssey_relational` database** (the photo store host, the `.ics`/vCard suites, the seeder suite), plus small private-database classes | yes, but its classes run one at a time |
| `MariaDbTimingCollection` | Classes that measure what contention distorts: elapsed-time budgets (P95, a lock wait bounded in seconds, a 60 s series delete) and `GC.GetTotalMemory`/`GC.GetTotalAllocatedBytes`, which count every thread in the process | **no** (`DisableParallelization`): after the others, alone |

Two rules keep the collections isolated, and both are checked by `DatabaseNameUniquenessTests`
(which needs no Docker):

- **A database name belongs to one source file.** Name a private database `odyssey_<something
  unique>` (or `odyssey_<prefix>_{Guid}`); two files naming the same database would drop and
  recreate it under each other from different collections — a scheduling-dependent flake. The test
  scans every `"odyssey_…"` literal and fails on a name found in two files.
- **A class that writes to `odyssey` or `odyssey_relational` goes in
  `MariaDbSharedDatabaseCollection`** (or the timing one). The test catches the two unmistakable
  spellings — `fixture.RelationalConnectionString` and `PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString…)`;
  any other data use of `odyssey` (a `UseMySql(fixture.OdysseyConnectionString…)` service graph) is
  on the author. Connecting to `odyssey` only to create or drop another database is not a use.

A new class with a private database joins `MariaDbCollection`; one that times something or reads a
process-wide counter joins `MariaDbTimingCollection`. The container's `--max-connections=1000` covers
the parallel pools (each private database is its own connection pool, idle for three minutes).

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
