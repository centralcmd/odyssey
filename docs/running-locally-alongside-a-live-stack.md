# Running Odyssey locally alongside a live stack

This guide exists so that **an automated agent (Claude Code) — or a new developer — can
build, run, and screenshot the app without clobbering a stack a teammate already has
running.** It is written to be self-contained: it assumes no prior machine-specific setup.

If you just want to run the app on a clean machine, `docker compose up --build` and the
[main README](../README.md) / `CLAUDE.md` are enough. This document is specifically about
the **coexistence** problem — running your own copy while another Odyssey stack is already
up on the same host.

## The core problem: two stacks fight over host port 3307

There are two ways to run the full stack locally, and **both publish MariaDB on host port
`3307`**:

| Stack | How it's launched | MariaDB volume | Client / API ports |
|---|---|---|---|
| **Docker Compose** | `docker compose up --build` | `mariadb_data` | 5199 / 5188 |
| **Aspire** | `dotnet run --project Odyssey.AppHost` | `mariadb-data` | 5199 / 5188 |

If one stack already holds `3307` and you start the other as-is, the second MariaDB comes
up **with no published host port** (or fails to bind). Host-side tools pointed at
`localhost:3307` then silently talk to the *wrong* database. The client/API ports collide
the same way.

> **Do not "fix" this by tearing down the other stack.** In particular, never run
> `docker compose down -v` or delete the `mariadb-data` / `mariadb_data` volume unless the
> stack's owner explicitly asks — that permanently wipes their database and seeded/test data.

## Recommended: run Compose with a remapped MariaDB port

The Compose app/api/migration containers reach MariaDB over the **internal Docker network**
(`server=mariadb:3306`) — see `docker-compose.yml`. The *published* host port only matters
to host-side tools, so you can remap it freely without breaking the app. Only the client
(5199) and API (5188) need to be reachable from the host to drive/screenshot the UI.

Create an override file:

```yaml
# /tmp/ods-override.yml
services:
  mariadb:
    ports: !override ["3308:3306"]
```

Bring the stack up with it:

```bash
docker compose -f docker-compose.yml -f /tmp/ods-override.yml up --build -d
```

Both stacks now coexist: Aspire keeps `3307`, your Compose MariaDB is on `3308`, and the app
still works because it never uses the host port internally.

`docker compose down` (note: **no `-v`**) is project-scoped
(`com.docker.compose.project=odyssey`) and will **not** touch an Aspire-launched MariaDB
container (which carries different labels). Add `-v` only when you deliberately want to
discard the Compose database.

> If you also need the client/API on different host ports (because Aspire is on 5199/5188),
> remap those in the same override file, e.g. `client: { ports: !override ["5299:8080"] }`
> and `api: { ports: !override ["5288:8080"] }`.

## Prefer Compose over the Blazor dev-server for a loadable frontend

Under Aspire the client runs as a `blazor-devserver` process. It recurrently
**fingerprint-desyncs**: the served `index.html` references a `dotnet.<hash>.js` that the
dev-server then 404s, so the WASM app never boots (blank page / *"Failed to fetch
dynamically imported module … dotnet.<hash>.js"*). That is an environment artifact, **not** a
bug in the page you're testing.

The Compose `client` container serves a **static WASM publish via NGINX** — no dev-server, no
fingerprint desync — so it loads reliably. For any "does the UI actually render" check, drive
the Compose stack.

Two related rules:

- **Don't `dotnet build` / `dotnet run` `Odyssey.Client` while a dev-server is serving it.**
  A rebuild writes new content-fingerprinted assets and desyncs the boot manifest of the
  already-running dev-server → the same 404 as above. In .NET 10 there is no
  `blazor.boot.json` (the manifest is embedded), so a 404 specifically on `blazor.boot.json`
  is normal and not the cause.
- **Don't `kill` the `blazor-devserver` process to force a reload.** Under Aspire, the
  orchestrator (dcp) treats a SIGTERM/SIGKILL as a *clean stop, not a crash*, and will not
  respawn it — the `client` resource stays down. Restart the `client` resource from your IDE
  / the Aspire dashboard instead, or relaunch the whole AppHost.

## Logging in: seeded demo users

Demo data is seeded automatically for the Compose dev stack (`SEED_DEMO_DATA=true` by
default; gated to Development/Testing — see `CLAUDE.md` → *Testing & Demo Data*). Four role-based
users are created, all sharing the password **`Odyssey!Demo1`** (defined in
`Odyssey.TestData/DemoDataDefaults.cs`; emails in `Odyssey.TestData/DemoUsers.cs`):

| Role | Email |
|---|---|
| Admin | `admin@demo.example.com` |
| Owner | `owner@demo.example.com` |
| User  | `user@demo.example.com` |
| Guest | `guest@demo.example.com` |

Seeded users are created confirmed + unlocked, so they can sign in despite the
admin-approval / email-confirmation gates. (A freshly *registered* account cannot log in
until confirmed/approved — use a seeded user instead.)

Drive the app at `http://localhost:5199` (the auth cookie is host-scoped); `/login` is
unauthenticated.

## Endpoints (Compose)

- Frontend: `http://localhost:5199`
- API: `http://localhost:5188`
- Swagger: `http://localhost:5188/swagger`
- MariaDB (with the override above): `localhost:3308`, database `odyssey`, user/password
  `odyssey` / `odyssey_password` (defaults in `docker-compose.yml`; override via the
  `MARIADB_*` env vars).

## Stale local config: `.env` and `appsettings.Development.json`

Both files are gitignored, so a copy that predates a rename keeps working right up until it
doesn't. Several renames have already landed:

- The `UserPreferences` context was merged into the application context, the Photos/Calendar contexts
  into the journal context, then the finance and journal contexts into a single `OdysseyContext`, and
  finally the application context into that too. A `.env` still carrying `USER_PREFERENCES_DATABASE`,
  `FINANCE_DATABASE`, `JOURNAL_DATABASE` or `APP_DATABASE` is setting variables nothing reads —
  `.env.example` now has one `ODYSSEY_DATABASE` for the whole schema.
- The same applies to a local `Odyssey.Api/appsettings.Development.json` or
  `Odyssey.MigrationService/appsettings.Development.json` that still names
  `UserPreferencesConnection`, `FinanceConnection`, `JournalConnection` or `ApplicationConnection`.
  There is one `OdysseyConnection` now, and it cannot be split: identity, finance and journal are one
  model with real foreign keys between them, so they must be the same database.

Diff your copies against `.env.example` after pulling. If a connection string is left unset, the
host now fails at startup with the key name and the fix:

```text
Connection string 'OdysseyConnection' is not configured. Set ConnectionStrings:OdysseyConnection
(or the ConnectionStrings__OdysseyConnection environment variable), or set UseInMemoryDatabase=true.
```

## Related tooling

- The **`run-odyssey`** skill builds, launches, and screenshots the stack in a real browser.
- The **`reset-environment`** skill wipes and reseeds the local database from the
  deterministic demo seed.

Both still need the coexistence rules above when another stack is already running.
