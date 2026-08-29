---
name: run-odyssey
description: >
  Build, launch, and drive the Odyssey full-stack finance app (Blazor WASM + ASP.NET API +
  MariaDB) from a clean machine, then screenshot or smoke-test the running UI in a real browser.
  Trigger on "run Odyssey", "start the app", "launch the stack", "screenshot the app/page", "open
  the dashboard/accounts page", "drive the UI", "is the app working", or any request to see the
  running frontend (not just the test suite).
---

# Run Odyssey

Odyssey is a web app: an **NGINX-served Blazor WebAssembly SPA on `http://localhost:5199`** talking
to an **ASP.NET Core API on `http://localhost:5188`**, cookie-authenticated, backed by **MariaDB**.
The whole thing runs via **Docker Compose** (4 services: `mariadb` → one-shot `migrations` → `api`
→ `client`). The migration container also runs the deterministic **demo seed**, so a fresh stack
comes up pre-populated (4 demo users, 21 accounts, ~2.7k transactions).

You drive the running app with **`driver.mjs`** — a Playwright script that logs in as a seeded demo
user and screenshots/asserts authed pages. That is the agent path; a human just opens `:5199`.

**Paths below are relative to the repo root.** The driver lives at
`.claude/skills/run-odyssey/driver.mjs`.

## Prerequisites

- **Docker** + the Compose plugin (`docker compose`).
- **Node 22** (for the driver). Playwright's chromium is already cached under
  `~/.cache/ms-playwright`; the pinned `playwright@1.60.0` matches the cached `chromium-1223`
  build, so no browser download is needed.
- **.NET 10 SDK** — only needed to *reset* the DB (see below), not to run the stack.

One-time: install the driver's single dependency (browser download skipped — it's cached):

```bash
cd .claude/skills/run-odyssey && PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm install --no-audit --no-fund
```

## Build & launch the stack

From the repo root. Compose auto-reads `.env` (already present for the dev stack). First build
compiles the API + publishes the Blazor WASM client — **several minutes**; rebuilds are cached.

```bash
docker compose up --build -d
```

Wait until the API answers (the `client` container starts before the API is fully ready, so poll
`/healthz`, not the port):

```bash
until curl -fs http://localhost:5188/healthz >/dev/null; do sleep 2; done && echo "API ready"
```

Sanity-check the services and confirm the seed ran:

```bash
docker compose ps --format '{{.Service}}\t{{.Status}}'
docker logs odyssey-migrations 2>&1 | tail -3   # ends with "Demo data seeding complete."
```

## Run (agent path) — drive & screenshot the UI

```bash
cd .claude/skills/run-odyssey

node driver.mjs health                 # no browser: probes API /healthz + SPA root
node driver.mjs smoke                  # login → assert seeded account → shoot dashboard + accounts
node driver.mjs shot /counterparties   # login → navigate to any authed route → screenshot
node driver.mjs shot /budgets budgets  # optional 2nd arg names the output PNG
```

Screenshots land in `.claude/skills/run-odyssey/screenshots/` (gitignored). **Open them** — a blank
or `/login` shot means the flow broke.

`smoke` is the end-to-end proof: it signs in as the seeded demo **Admin**
(`admin@demo.example.com` / `Odyssey!Demo1`), waits to be redirected off `/login`, opens
`/accounts`, and asserts the seeded account **"Everyday Checking"** is visible — exercising cookie
auth + SPA + API + demo seed in one shot. Verified output:

```
logged in as admin@demo.example.com; landed on http://localhost:5199/
shot / -> .../screenshots/dashboard.png
seeded account "Everyday Checking" is visible — full stack OK
shot /accounts -> .../screenshots/accounts.png
```

Override target/creds via env: `ODYSSEY_BASE_URL`, `ODYSSEY_API_URL`, `ODYSSEY_EMAIL`,
`ODYSSEY_PASSWORD`, `HEADLESS` (default `1`). The four seeded role logins all share the password
`Odyssey!Demo1`: `admin@demo.example.com` (Admin), `owner@demo.example.com` (Owner),
`user@demo.example.com` (User), `guest@demo.example.com` (Guest).

## Run (human path)

`docker compose up --build` (foreground) then open `http://localhost:5199` and sign in with any
login above. Swagger is at `http://localhost:5188/swagger`. Useless headless — the driver is the
agent path.

## Reset / reseed

The demo seed is **idempotent** and the `mariadb_data` volume **persists**, so a plain re-`up` skips
reseeding (`docker logs odyssey-migrations` will say *"already present; skipping"*). To get a clean,
known dataset, use the sibling skill — invoke **`/reset-environment`** (it drops + recreates the DB
and re-runs migrations + seed against the running stack). Or wipe the volume:
`docker compose down -v` then `docker compose up --build -d`.

## Stop

```bash
docker compose down       # stop containers, keep DB data
docker compose down -v    # also delete the MariaDB volume (forces a reseed next up)
```

## Gotchas

- **No `chromium-cli` here** — the driver uses the Node `playwright` package against the cached
  chromium. The cache dir name is Playwright's build number (`chromium-1223`), *not* a chromium
  version; `playwright@1.60.0` is the version that maps to it. Bumping playwright without a matching
  cached build means a download (which may fail offline).
- **`client` is up before the API is ready.** Its `depends_on` waits only for the API *container*,
  not `/healthz`. Always poll `/healthz` before driving, or login will flake.
- **A re-`up` does NOT reseed** (idempotent seed + persistent volume). If you expect fresh data and
  don't get it, you wanted `/reset-environment` or `down -v`.
- **Login is label-driven** (MudBlazor): the driver fills `getByLabel('Username or Email')` /
  `getByLabel('Password')` and clicks the **`Sign in`** button, then waits for the URL to leave
  `/login`. Newly *registered* users can't sign in (`RequireConfirmedAccount` + admin-approval) —
  only the seeded demo users work out of the box.
- **MariaDB is on host port `3307`**, not 3306 (the in-container port is 3306).
- **Don't `dotnet run` the client** against this stack — the SPA is served by NGINX from the Docker
  build; rebuilding it separately desyncs the `blazor.boot.json` asset hashes.

## Troubleshooting

- `node driver.mjs health` → SPA OK but API FAIL: the API container is still starting or crashed —
  `docker logs odyssey-api 2>&1 | tail -30`. Migrations must complete first
  (`docker logs odyssey-migrations`).
- Driver hangs at login / times out waiting to leave `/login`: API not ready (poll `/healthz`), or
  the DB isn't seeded (`docker logs odyssey-migrations`), or you overrode creds with a non-seeded
  user.
- `Executable doesn't exist at .../chromium-XXXX`: the installed playwright version wants a browser
  build that isn't cached. Pin back to `playwright@1.60.0` (matches cached `chromium-1223`) or run
  `npx playwright install chromium`.
- Build fails on `docker compose up --build`: confirm the .NET 10 base images pull and there's disk
  for the multi-stage build; re-run — layer caching makes the retry fast.
