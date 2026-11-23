# Rebaselining the migration chain

A **rebaseline** collapses every migration of `OdysseyContext` into one baseline migration. Existing
production databases cross it with **no manual SQL and no rebuild**; new installs and every test
database build from the one migration instead of replaying the whole chain. This page is the
procedure. The design and its review are issue #581.

It is the only sanctioned way to shorten the history. Between rebaselines, every schema change is an
additive migration.

## How it works

The baseline migration **keeps the id of the newest migration it replaces**. EF decides what is
pending by comparing the ids in the assembly with the rows in `__EFMigrationsHistory`, and it ignores
history rows whose ids the assembly no longer has. So:

| Database | History holds the cut id? | What the rebaselined build does |
|---|---|---|
| Production, at the cut | yes | nothing pending: it is already "applied" |
| New install | history empty | runs the baseline, building the full schema |
| Older than the cut | no | **refused** before anything runs (`MigrationBaselineException`) |

The old ids stay in the history table. They are harmless, and the drift guard ignores any that are
earlier than the baseline.

The rule is only safe when the baseline builds **exactly** the schema the old chain built. A
difference would leave production silently missing a constraint, or give new installs one production
lacks. That is what the fingerprint is for.

## The pieces

| Piece | Where | Role |
|---|---|---|
| Baseline declaration | `Odyssey.Context/Migrations/MigrationBaseline.cs` | `MigrationBaseline.Current`: the baseline id, and the **bridge release** (the last release that shipped the replaced chain) |
| Cut-point guard | `MigrationRunner.GuardAgainstBaselineGapAsync` | Refuses a non-empty database whose history lacks the baseline id. It reports and never repairs |
| Committed fingerprint | `Odyssey.Context/Migrations/Baseline/<baseline-id>.fingerprint.txt` | What an empty database migrated to the baseline id must produce |
| Parity test | `Odyssey.IntegrationTests/SchemaFingerprintParityTests` | Compares a freshly migrated database with the committed file |
| Fingerprint mode | `Odyssey.MigrationService --schema-fingerprint` | Prints the operator sections of a live database, read-only |
| Coupled-name registry | `Odyssey.Context/Migrations/CoupledMigrationNames.cs` | The only place code may name a migration |

### The fingerprint

One text format, in four sections. Lines starting with `#` are the header (server version and
defaults) and are never compared.

| Section | Contents | Written by |
|---|---|---|
| `[schema]` | Every table's `SHOW CREATE TABLE`, normalised in three ways only: `AUTO_INCREMENT=n` is removed; the column lines and the key lines are each sorted, because a rebaseline cannot reproduce the order a later migration appended a column in; and a table's charset and collation are written as "database default" when they equal the database's, because those come from the server's configuration. Every line is otherwise kept verbatim: types, defaults, engine, `ROW_FORMAT`, an explicit collation and every literal. Also a line saying there are no views, triggers, routines or events | the mode and the tests |
| `[seed-rows]` | Full rows of the seeded tables classified `Rows`, which is `AspNetRoles` only | the mode and the tests |
| `[seed-keys]` | Primary keys only of the seeded tables classified `Keys`, such as `SystemSettings` and the singleton leases and counters | the mode and the tests |
| `[parity-rows]` | **Every row of every table** | the integration tier only, over a database it created |

The first three are the **operator sections**. The mode prints exactly those, so its output against
a fresh database equals the committed file minus `[parity-rows]`; a test pins that. `Currencies` is
classified `Excluded` and appears in no operator section, because administrators add and delete
currencies and that must not read as drift. The classification is in
`Odyssey.MigrationService/Fingerprint/SeededTables.cs`, and a test requires it to cover exactly the
tables the model seeds with `HasData`.

The parity writer refuses any database whose name does not start with `odyssey_parity_`, that holds
an account, or whose history holds an id this build does not ship. That is why the all-rows section
can never be produced from production.

## Running the fingerprint mode

Run it from the build that carries the committed fingerprint, which is the rebaseline's own branch or release.
The output's normalisation is part of the build, so an older build's output does not diff cleanly against a
newer file, and the release before a rebaseline may have no fingerprint mode at all.

```bash
ConnectionStrings__OdysseyConnection='Server=…;Port=3306;Database=odyssey;User=…;Password=…' \
  dotnet run --project Odyssey.MigrationService -- --schema-fingerprint > production.fingerprint.txt
```

Or, from the published image, run the migrations container's entry point with the same argument and
environment. The mode builds no host, so it starts no seeder or migration, and it opens the session
`READ ONLY`. It refuses `UseInMemoryDatabase`. On a failure it prints one fixed line and the
exception's type name, never the connector's message, which can carry the host, port and user.

Give it a dedicated account that can only read: `SELECT` on the database is enough, since it reads
only `information_schema`, `SHOW CREATE TABLE` and the seeded tables. The session's `READ ONLY` is a
second barrier, not a substitute for least privilege. Also note:

- **Pass the connection string through the environment, as above,** never as a command-line argument.
  The mode accepts `--ConnectionStrings:OdysseyConnection=…` like the host does, but an argument shows
  the password in `ps` and in shell history.
- **Treat the output as internal.** It discloses the server version and the full schema. Attach it to
  the rebaseline PR only while the repository is private, and nowhere public.

Compare the output with the committed file, dropping the header lines and the parity section:

```bash
f=Odyssey.Context/Migrations/Baseline/<cut-id>.fingerprint.txt
diff <(sed '/^\[parity-rows\]$/,$d' "$f" | grep -v '^#') <(grep -v '^#' production.fingerprint.txt)
```

The diff should be empty. Compare the two `# server:` header lines by eye as well: the diff skips them,
and a different default collation means the database's text columns compare differently, which is the
server's configuration rather than anything a rebaseline changed.

## The procedure

1. **Choose the cut.** Pick the newest migration of a released version `vX` that **every** production
   database has applied. Check each database:

   ```sql
   SELECT MAX(MigrationId) FROM __EFMigrationsHistory;
   ```

2. **Make sure the cut is the head of the tree you are squashing.** Start the rebaseline branch from
   `vX`'s commit, or from a tree whose newest migration is the cut. Before deleting anything, confirm
   that:
   - the newest id in `Odyssey.Context/Migrations/` is the cut id, and
   - `ModelSnapshotDriftTests` passes, so the model holds no change the chain lacks.

   If either check fails, **stop**. The new baseline would be scaffolded from a model with schema
   production does not have, and the same-id rule would mark it applied. The parity test would also
   catch this, later.

3. **Capture the reference on the pre-squash tree.** With Docker running:

   ```bash
   ODYSSEY_WRITE_BASELINE_FINGERPRINT=1 ODYSSEY_FINGERPRINT_MIGRATION=<cut-id> ODYSSEY_REQUIRE_TIER=integration \
     dotnet test Odyssey.IntegrationTests --filter "FullyQualifiedName~Regenerate_the_committed_fingerprint_when_asked"
   ```

   This migrates an empty database through the old chain to the cut and writes
   `Odyssey.Context/Migrations/Baseline/<cut-id>.fingerprint.txt`, all four sections. Commit it. Use
   the same MariaDB image production runs (`mariadb:11.4` today), so the header and the rendering
   match.

4. **Verify every production database.** Take a **verified backup** of each one, then run the
   fingerprint mode against it and diff with the operator sections of the reference, as above. Attach
   every diff, which should be empty, to the rebaseline PR as the change record. Any difference stops
   the rebaseline until it is understood and resolved; it usually means a hand edit or a hand-repaired
   interruption.

5. **Squash.** Delete every migration and `OdysseyContextModelSnapshot.cs`, then scaffold a new
   `InitialCreate` (CLAUDE.md → *EF Core Migrations* has the command). Rename its two files and its
   `[Migration]` attribute to the **cut id, verbatim**. Keep the class name `InitialCreate` and say in its
   doc comment which chain it replaces: EF reads the id from the attribute alone.

6. **Port what the model cannot regenerate.** Today the migrations create no view, trigger, routine,
   event or sequence. What the November 2026 rebaseline had to port was **column defaults**: a migration
   that adds a `NOT NULL` column with `defaultValue: 0` leaves that `DEFAULT` on the column, though the
   model declares none, so the regenerated baseline loses it. Put each one back as `defaultValue:` on its
   column in the baseline's `CreateTable`, never in the model. Expect reference rows inserted by raw SQL
   too. Drop the data rewrites; on an empty database they have nothing to do. The parity test tells you
   what you missed, because it compares every line and every row.

7. **Update the declaration.** Set `MigrationBaseline.Current` to `(cut id, "vX")` and remove the old
   fingerprint file, leaving only `<cut-id>.fingerprint.txt`.

8. **Remove what depended on deleted migrations.** `CoupledMigrationNamesTests` fails for every
   registry entry that no longer resolves, or now resolves to the baseline. Remove each such entry
   together with the code behind it and that code's tests. Today that would be the location-conversion
   staging, the non-positive-rate guard and the account-type retirement report. Also remove every
   `MigrationSeam` caller that names a removed migration; those calls fail at runtime only, in the
   Docker tier.

9. **Run the tiers that matter**, with the integration tier made mandatory so a missing daemon cannot
   pass the PR:

   ```bash
   dotnet test Odyssey.MigrationService.Tests
   ```

   ```bash
   ODYSSEY_REQUIRE_TIER=integration dotnet test Odyssey.IntegrationTests
   ```

   The parity test, the same-id tests (`MigrationBaselineGuardIntegrationTests`),
   `ModelSnapshotDriftTests` and the drift-guard tests must all pass. **A failing parity test is a defect
   in the new baseline. Never fix it by regenerating the fingerprint.**

10. **Release notes.** State: "databases below `vX` must upgrade to `vX`, and let its migrations job
    complete, before this release." An operator who later meets the `BelowBaseline` refusal should check the
    printed newest id against `vX`'s chain before following the advice: the guard decides by id order
    alone.

11. **After the deploy,** run the fingerprint mode against each production database again, diff it
    the same way, and record the result on the PR.

## What the refusals mean

| Message | Cause | Remedy |
|---|---|---|
| `…newest applied migration is '…', which is older. Upgrade to vX first…` | The database predates the baseline | Deploy `vX`, let its migrations job complete, then deploy this build |
| `…No earlier migration set can be upgraded; the database must be recreated.` | The database predates the baseline and no bridge exists | Recreate it; restore data from an export if you need it |
| `…built by a different migration set…` | The history's newest id is later than the baseline, but the baseline is missing | The database was not built by this chain. See [migration-history-drift.md](migration-history-drift.md) |

None of these write anything. Never insert the baseline id into the history by hand to get past one:
a database lacking it is by definition not at the cut, and the row would claim a schema it does not
have.

## Accepted limits

- **Column order differs for good between upgraded and new installs.** A database that crossed the
  rebaseline keeps its columns in the order the old migrations appended them; a new install has them in
  model order (40 tables differed at the November 2026 cut). EF addresses columns by name and the
  fingerprint discounts the order, but a `SHOW CREATE TABLE` compared by hand will show it.

- **The guard decides by id order alone.** A database from an unrelated set whose ids are all earlier
  than the baseline gets the "upgrade to the bridge" text. Upgrading through the bridge would then stop
  on the drift guard rather than corrupt anything.
- **A hand-inserted baseline row** passes the guard. The before-and-after fingerprint diffs are the
  compensating control.
- **Production is compared on schema, `Roles` rows and stable seed keys only.** A changed seed
  *value*, such as an edited setting or a currency, is data, not schema, and a rebaseline does not
  touch it.
- **All production databases are operated by the maintainer.** That is what makes "every database is
  at the cut" something you can check.
