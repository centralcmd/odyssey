# Odyssey.MigrationService

The migrations job: a short-lived host that brings the database up to date and seeds it, then exits. The API
starts only after it **succeeds** (`service_completed_successfully` in Compose, `.WaitForCompletion(migrations)` in
Aspire), so a failure here keeps the API down on purpose.

## Order of operations

`Worker.cs` runs five steps in a fixed order, each behind its own interface (`IMigrationStep.cs`) so the order stays
visible and `WorkerTests` can substitute them:

1. **Migrate** — `OdysseyMigrationService` → `MigrationRunner`: the drift guard, then every pending migration on
   `OdysseyContext` (staged around the one-time location conversion).
2. **Reconcile role claims** — `RoleClaimSeeder` makes `AspNetRoleClaims` match `RolePermissions`, additions and
   removals alike.
3. **Bootstrap the administrator** — `BootstrapAdminSeeder` creates one from `Bootstrap:Admin:Email`/`Password`, only
   on an empty user table. It runs before the demo seed, which would otherwise make the table non-empty.
4. **Seed demo data** — `DemoDataSeeder`, Development/Testing only (`DemoDataGate`), idempotent, no file bytes.
5. **Assert an administrator exists** — `AdministratorAssertion` fails the job rather than start an API nobody can
   administer.

Any failure sets exit code 1. Other files: `MigrationDriftDetector.cs` / `MigrationDriftException.cs` (the guard's
schema snapshot and its operator-facing error), `MigrationBaselineException.cs` (the cut-point guard's refusal),
`Fingerprint/` (the read-only `--schema-fingerprint` mode, see
[`docs/migration-rebaseline.md`](../docs/migration-rebaseline.md)), `MigrationServiceIdentity.cs` (the Identity
registration the tests share).

## Running and testing

```bash
dotnet test Odyssey.MigrationService.Tests   # EF InMemory: the seeders, the step order, the guard's logic
dotnet test Odyssey.IntegrationTests         # real MariaDB: migrations apply, the guard's schema snapshot
```

The job normally runs as part of the stack (root [`CLAUDE.md`](../CLAUDE.md) → *Commands*). It reads
`DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT` —
[deployment.md](../docs/deployment.md#the-migrations-job-reads-dotnet_environment-not-aspnetcore_environment).
Creating a migration is done with `dotnet ef` against `Odyssey.Context` —
[`Odyssey.Context/README.md`](../Odyssey.Context/README.md#database-migrations).

## Rules that matter most

- **The drift guard reports, never repairs, and stays narrow** — it fails only when a pending migration would create
  an object that already exists — [migration-history-drift.md](../docs/migration-history-drift.md).
- **`MigrateAsync` never gets the host's cancellation token**; a migration in flight finishes (MariaDB commits DDL
  implicitly) — `MigrationRunner.cs`, root [`CLAUDE.md`](../CLAUDE.md) → *EF Core Migrations*.
- **Never reintroduce a `HasData` claim seed**; `RoleClaimSeeder` is the only writer of role claims — root
  [`CLAUDE.md`](../CLAUDE.md) → *Permission claims*.
- **Keep the bootstrap seed before the demo seed** (issue #290) — the comment in `Worker.cs`.
- **This job holds no Data Protection key ring**; re-adding one is only right if a step must protect a value — the
  comment in `Program.cs`.
- **The schema and its keys are documented with the context** — [`Odyssey.Context/README.md`](../Odyssey.Context/README.md).
