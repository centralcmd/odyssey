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

## A private database at head: copy, don't migrate

A test that needs a database of its own at the current schema calls
`fixture.CreateMigratedDatabaseAsync(name)`. It drops `name` if it exists, recreates it as a copy of
`odyssey_template` and returns its connection string. The fixture migrates that template through the
real migration chain once per container (`MigratedSchemaTemplate`). Locally a copy takes about 70 ms and
a migration from empty about 2.8 s.

Two kinds of test still migrate for real:

- a **migration test** (`MigrationSeam`) that needs the database *before* head, where a copy of head is
  no use;
- the **seeder test** and `MigrationDriftIntegrationTests`, whose point is that the migrations
  themselves apply.

`MigratedSchemaTemplateTests` compares a copy with a database migrated from empty: every column,
index, constraint, foreign key and table option, and every row (`CHECKSUM TABLE`). A copy comes from
`SHOW CREATE TABLE`, so the template refuses to build if a migration ever creates an object that does
not reproduce: a view, trigger, routine, event or sequence.
