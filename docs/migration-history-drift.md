# Migration-history drift: what it is, and how to repair it

The migrations job refuses to start and says something like:

```text
crit: Odyssey.MigrationService.Worker[0]
      Migrations job failed: Migration '20260829095318_InitialCreate' is recorded as pending, but the
      table 'Accounts' it creates already exists. The database is out of step with
      __EFMigrationsHistory ...
```

Nothing behind the job starts — the API waits on the migrations job *completing successfully*
(`docker-compose.yml`'s `service_completed_successfully`, and `.WaitForCompletion(migrations)` in
`Odyssey.AppHost`), so the whole stack stays down until the database is repaired.

## Why it happens

Two causes produce the identical symptom, and the repair is the same for both. The guard cannot tell
them apart — see below — so it names both and leaves the diagnosis to you. Start by looking at what is
actually in the history table:

```sql
SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;
```

### Cause 1: an interrupted run

**MariaDB commits DDL implicitly.** Every `CREATE TABLE` ends the surrounding transaction, so a
migration is *not* atomic there no matter what transaction EF opens around it. The
`__EFMigrationsHistory` row that records a migration as applied is written only after its last DDL
statement.

Interrupt a migration in between — a container stop, an AppHost restart, `Ctrl-C`, an OOM kill, a
`SIGKILL` — and you are left with:

- the tables the migration had created so far, committed and permanent, and
- **no** history row saying the migration ran.

The next run reads an empty (or short) history, concludes the migration is still pending, and replays
it from the top. It dies on the first object that already exists. So does every run after that. The
database cannot recover on its own, which is why the job now stops with the message above instead of
letting EF surface a bare `Table 'Accounts' already exists`.

### Cause 2: a superseded migration set

The other cause is a database that is entirely intact but was built by migrations this build no longer
ships — what a **squash or a renumber** leaves behind. Nothing was interrupted; the schema is complete
and consistent with the *old* migration set, and the new one is pending because its id has never been
seen. It then dies on the first table the old set already created.

The tell is in the query above: history rows carrying ids that are not in the current build. After the
merge of the application and domain contexts, for example, a database from before it holds

```text
20260828173324_InitialCreate   ← the old ApplicationContext
20260829005807_InitialCreate   ← the old OdysseyContext
```

neither of which exists any more, while the current `InitialCreate` is pending.

Since issue #581 the job reads that tell for you, in two places:

- **The cut-point guard runs first.** A history that lacks this build's baseline migration
  (`MigrationBaseline.Current`, today `20261122104734_AddRecipes`, the v0.51.0 cut) never reaches the drift guard: the
  job stops with `MigrationBaselineException`. If the history's newest id is *earlier* than the
  baseline, the message names the release to upgrade through (or says the database must be recreated,
  when no bridge exists); if it is *later*, the message says the database was built by a different
  migration set. The example above is the second case. Before following "upgrade to the bridge
  release", check that the printed newest id really belongs to that release's chain — the guard decides
  by id order alone.
- **The drift guard names a superseded set when it can prove one.** When it fires and the history holds
  ids this build does not ship that are **later than the baseline**, the message lists them and names
  the superseded set as the cause. Unknown ids *earlier* than the baseline never count: they are the
  retired ids a rebaseline leaves on every healthy database (see
  [`migration-rebaseline.md`](migration-rebaseline.md)), and counting them would report an ordinary
  interruption as a superseded set. Without such ids the message still names both causes.

Note that a squash is **not** repaired by re-running, and is not repaired by hand-editing the history
row either. The old schema is not the new schema — after the context merges it is missing every
cross-module foreign key and every user-attribution key — so writing the new id would claim the
migration applied while leaving a database without the constraints it exists to add. Rebuild it.

## What the guard looks at

Five kinds of object, because MariaDB commits each of their `CREATE`/`ALTER` statements independently
and an interruption can therefore land between any two of them: **tables**, **columns**, **indexes**,
**foreign keys** and **check constraints**. A pending migration that would create one which already
exists is drift.

Most migrations in the repository bundle their indexes and foreign keys into `CreateTable`, so in
practice a drifted database is usually caught on a table. The other kinds are covered because an
index-only or constraint-only migration drifts in exactly the same way — issue #218's
*RetirePropertyAndVehicleAccountTypes* (since folded into the v0.51.0 baseline), whose only DDL was
`CK_Accounts_AccountTypeNotRetired`, is why
check constraints joined. Adding another kind means one case in `MigrationRunner.CreatedBy` (and
`DroppedBy`), one arm in the snapshot query, and one member on `SchemaObjectKind`.

A create is **discounted** when an earlier operation of the same pending run dropped that name first:
the replayed drop removes whatever an interruption left, so the create cannot collide. EF scaffolds
every change to a check's expression that way — `DropCheckConstraint` then `AddCheckConstraint` of the
same name — and many shipped migrations do it in `Up()`, so without the discount an ordinary upgrade
across any of them would be refused.

### The renamed-table half-state

A **rename** counts as a create here, and that is not a technicality. `RenameTable`, `RenameColumn`
and `RenameIndex` each emit their own `ALTER TABLE`, so an interruption can land between any two of
them — and what it leaves behind is the object under its **new** name, with no history row. That is
precisely the shape the guard reports on, so `MigrationRunner.CreatedBy` maps each `Rename*Operation`
onto the object its `NewName` creates.

`20260918225310_RenameAccountTermsToTermsAndReworkInterval` is the first migration in the repository
to rename anything, and it renames a table, two columns and an index in one run. Interrupted, it
leaves a database where, for example, `Terms` exists and `AccountTerms` does not, while
`__EFMigrationsHistory` still says the migration is pending. The guard reports:

```text
Migrations job failed: Migration '20260918225310_RenameAccountTermsToTermsAndReworkInterval' is
recorded as pending, but the table 'Terms' it creates already exists. ...
```

Diagnosing it is the same exercise as any other half-state — read the message, decide which of the
two causes below you are in, and repair per the sections that follow. Note only that the *symptom*
reads oddly: the table the message names is the one the database has, so on this migration the
half-state shows up as objects under their **new** names, not their old ones. `SHOW TABLES` will show
`Terms` and not `AccountTerms`; that is the rename having got that far, not a schema from a different
build.

The corresponding *drop* halves (`DropTable`, `DropColumn`, `DropIndex`, `DropForeignKey`) are
deliberately **not** mapped. Replaying a drop cannot collide with an object already present, which is
the only failure this guard is about.

The test stays deliberately narrow: a pending migration creating an *existing* object. The cheaper
test — "there are pending migrations and the schema is not empty" — describes every ordinary upgrade,
and would fail every deploy.

## What the job does and does not do

It **detects and reports**. It deliberately does **not** repair itself.

Writing the missing history row would be the obvious automatic fix and is the dangerous one: an
interruption leaves an arbitrary *prefix* of the migration applied, so marking it complete records a
half-built schema as finished. The failure then moves from startup — loud, early, and in front of an
operator — to the first request that touches a column which was never created.

## Repair

### Development, demo, or any database you can throw away

Drop and recreate the database. Every migration re-applies cleanly and the demo seed re-runs:

```bash
.claude/skills/reset-environment/reset.sh
```

Or, with Compose, discard the volume entirely:

```bash
docker compose down -v && docker compose up -d
```

### A database with real data

**Both options below assume Cause 1**, an interrupted run of the migration named in the error. Confirm
that first — if the history table holds ids this build no longer ships, you are looking at Cause 2 and
neither applies: there is no partial application to undo or finish, and the schema the old set produced
is not the one the new set describes. Restore onto a database rebuilt from the current migrations
instead.

For an interrupted run there is no safe automatic repair, so this is a deliberate manual choice between
two directions. Take a backup first (`docs/deployment.md` → *Backups*), and work from the migration
named in the error.

**Option A — undo the partial migration (preferred).** Drop the objects the interrupted migration had
created, returning the schema to the state before it ran, then start the job again and let it apply the
migration properly. Read the migration's `Up` method to get the exact list; everything it creates before
the point of interruption is what needs removing.

**Option B — complete it by hand.** Only if you can verify the migration is in fact fully applied —
every table, column, index and constraint in its `Up` method is present. Apply anything missing by
hand, then record it:

```sql
INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('<the migration id named in the error>', '<ProductVersion>');
```

Take `ProductVersion` from the other rows in the table. Getting this wrong reintroduces exactly the
silent half-migrated schema the guard exists to prevent, so prefer Option A unless the schema is
demonstrably complete.

After either option, start the stack normally and confirm the job exits 0.

**Data migrations that are not replay-safe: fix forward, never replay.** *WeatherPointsExpand* and
*WeatherPointsContract* (issue #387, folded into the v0.51.0 baseline) were the precedent: neither Option A nor
Option B applies to such a migration as written. If one fails, restore the pre-deploy backup, or repair forward
with a follow-up migration or a manual fix of the half-applied step — do not delete its history row to make it
run again.

## Reducing the chance of hitting it

The job no longer cancels a migration that is already applying DDL: a shutdown signal waits for it,
governed by `HostOptions.ShutdownTimeout` (5 minutes) in `Odyssey.MigrationService/Program.cs`, with a
matching `stop_grace_period: 5m` on the `migrations` service in `docker-compose.yml`. **Both are
required.** Compose's own default grace period is 10 seconds, and Docker sends `SIGKILL` when it
expires no matter what the .NET host intends — so a raised `ShutdownTimeout` on its own would be inert
under Compose. (The production overlay inherits the base file's value; it needs no entry of its own.)

That narrows the window to the time between the process being signalled and the DDL completing. It does
not close it. A `SIGKILL`, an OOM kill or a hard container stop still lands wherever it lands. Atomic
DDL is not available on this engine, so the guard — fail fast, say what happened, say how to repair
it — is the durable part of the answer.

## Where this lives in the code

| Piece | File |
|---|---|
| The shared migrate call, the guard, and the cancellation rule | `Odyssey.MigrationService/MigrationRunner.cs` |
| The drift decision, as a pure function | `Odyssey.MigrationService/MigrationDriftDetector.cs` |
| The operator-facing message | `Odyssey.MigrationService/MigrationDriftException.cs` |
| The critical log line | `Odyssey.MigrationService/Worker.cs` |
| Real-engine regression test | `Odyssey.IntegrationTests/MigrationDriftIntegrationTests.cs` |
