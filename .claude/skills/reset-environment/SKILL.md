---
name: reset-environment
description: >
  Reset / reseed / regenerate the Odyssey local dev or test database so you can start over
  testing from a clean, known state. Wipes the database and re-runs migrations + the
  deterministic demo seed. Works against both the Docker Compose and Aspire stacks. Trigger on
  "reset the database", "reseed", "regenerate test data", "wipe and reseed", "start over",
  "clean database", "fresh demo data".
---

# Reset the Odyssey dev/test environment

Regenerates the database from scratch: it **drops and recreates** the database, then runs
`Odyssey.MigrationService`, which re-applies every migration and re-runs the gated, idempotent,
**deterministic** demo seeder. You end up with the exact same known dataset every time
(4 demo users, 19 accounts, per-year budgets, ~2.7k transactions, exchange rates).

A plain stack restart does **not** reseed — the seeder skips when its data is already present —
so the reset has to wipe first. That's what the driver does.

Paths below are relative to the repo root (`<repo>/`). The driver lives at
`.claude/skills/reset-environment/reset.sh`.

## Prerequisites

- **Docker** (the driver runs migrations from the host and talks to MariaDB through a throwaway
  `mariadb:11.4` client container — no local SQL client needed).
- **.NET 10 SDK** (`dotnet`) — the driver runs `Odyssey.MigrationService`.
- **A running stack** — either Docker Compose (`docker compose up -d`) or Aspire
  (`dotnet run --project Odyssey.AppHost`). Both publish MariaDB on host port **3307**, and only
  one runs at a time.

## Run (agent path)

```bash
.claude/skills/reset-environment/reset.sh
```

That's it. The script:

1. Finds MariaDB on `127.0.0.1:3307` and resolves credentials automatically — it tries, in
   order: explicit `DB_USER`/`DB_PASSWORD`, then the repo `.env` (Compose), then AppHost
   user-secrets `Aspire:MariaDb:*` (Aspire) — using the first that authenticates. So it works
   against whichever stack is up without being told which.
2. Drops and recreates the database (as the app user; no root needed).
3. Empties every file volume (issue #346), keeping the roots: each Aspire root under `.aspire/files/`, and
   each of this checkout's Compose volumes named `<project>_files_*` (through a throwaway container). Markers go too, so the API
   initialises each volume afresh on its next start.
4. Runs `Odyssey.MigrationService` (migrate + seed).
5. Prints a verification summary.

Verified output (against the Compose stack):

```
==> Locating a running MariaDB on 127.0.0.1:3307 and resolving credentials
    using credentials from: .env (Compose) (user 'admin', database 'odyssey')
==> Wiping database 'odyssey'
==> Re-applying migrations and reseeding demo data
...
==> Verifying seeded data
  accounts       = 21
  users          = 4
  exchange_rates = 60
  transactions   = 2743
==> Reset complete. (Running API/client need no restart — data is read live from the DB.)
```

Restart the **api and worker** afterwards (`docker compose restart api worker`, or restart the Aspire
stack). Data is read live from the DB, but the file volumes were emptied under them: their startup
verifier is what re-initialises an empty volume, and a running API keeps writing to a volume whose
marker is gone. The client needs no restart. Re-running the script is safe and produces identical counts.

### Overrides

Set any of these before invoking for a non-standard setup:

| Variable | Default | Purpose |
|---|---|---|
| `DB_HOST` / `DB_PORT` | `127.0.0.1` / `3307` | Where MariaDB is published |
| `DB_USER` / `DB_PASSWORD` | resolved from `.env` / user-secrets | Force specific credentials |
| `DB_NAME` | `odyssey` (or `.env`'s `ODYSSEY_DATABASE`) | Database name |
| `SEED_DEMO_DATA` | `true` | Set `false` to reset to an empty (migrated) DB with no demo data |

## Alternative: full Compose volume wipe

If you want to throw away the database **volume** entirely (not just the schema) — e.g. to also
reset DataProtection state — use Compose's own teardown instead of this driver:
`docker compose down -v` then `docker compose up -d` (the migration service reseeds on the fresh
volume). `-v` also removes the `odyssey_files_*` file volumes along with `mariadb_data`, which is
what you want here: removing the database alone and keeping a file volume that held files makes the
API refuse to start (`UnknownMarker`). This is heavier (rebuilds/restarts containers); the driver
above is the fast path. Never run it against a stack a teammate is using — see
`docs/running-locally-alongside-a-live-stack.md`.

## Gotchas

- **Idempotent seeder.** Restarting the stack will not regenerate data — the seeder no-ops when
  the demo data is already present. You must wipe first; the driver does.
- **Both stacks use host port 3307.** They can't run at once. The driver targets 3307 and works
  for whichever is up.
- **Credentials live in different places.** Compose reads them from `.env`
  (`MARIADB_USER`/`MARIADB_PASSWORD`); Aspire reads them from AppHost user-secrets
  (`Aspire:MariaDb:User`/`Password`). The driver checks both — don't hardcode.
- **Don't connect as `root` over the published port.** A client reaching 3307 from another
  container/host appears to MariaDB as the Docker bridge gateway, and `root` isn't allowed from
  there. The driver uses the app user (whose db-scoped `ALL` privilege can drop/recreate its own
  database and survives the drop).
- **`.env` is not sourced.** Its values can contain shell metacharacters (sourcing it actually
  broke an early version), so the driver greps the specific keys it needs.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `could not authenticate to MariaDB at 127.0.0.1:3307` | The stack isn't running, or it's on a different port. Start Compose/Aspire, or set `DB_HOST`/`DB_PORT`/`DB_USER`/`DB_PASSWORD`. |
| `Access denied for user 'root'@'172.x.x.x'` | You overrode to root — don't. Use the app user (the default). |
| Reset succeeds but the app still shows old data | Hard-refresh the browser; the Blazor client caches in-memory view state, not the data itself. |
| API or worker exits with `File storage check UnknownMarker / UnrecognisedContent failed` | A file volume still holds files from before the reset. Re-run the driver (it empties every volume), or empty `.aspire/files/<name>/` by hand, keeping the folder. |
