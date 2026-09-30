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
