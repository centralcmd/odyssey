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
- **Node 22** (for the driver). The pin is `playwright@1.62.0`, deliberately equal to the
  `Microsoft.Playwright` version in `Directory.Packages.props`, so the driver and `Odyssey.E2ETests`
  want the **same** chromium build (currently `chromium-1234`). Keep the two in lockstep when either
  is bumped: a driver pinned to a build nothing else installs means a second ~650 MB download, or an
  outright launch failure where the browsers are baked in read-only.
- **.NET 10 SDK** — only needed to *reset* the DB (see below), not to run the stack.

One-time, from the repo root:

```bash
cd .claude/skills/run-odyssey && npm install --no-audit --no-fund
npx playwright install chromium    # no-op if the matching build is already present
```

**Don't assume the browser is cached.** Where it already is (a Claude Code session bakes it into
`/opt/pw-browsers` and exports `PLAYWRIGHT_BROWSERS_PATH`), `install chromium` costs a version check
and exits; on a normal workstation it downloads once into `~/.cache/ms-playwright`. Skipping it with
`PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1` only pays off when the cached build is *exactly* the one the
pinned playwright wants, and that is precisely what a package bump on either side silently breaks.

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

### When Compose can't build: the CA override, or Aspire

In a Claude Code session, outbound TLS is intercepted. The daemon holds the CA but a **build**
container does not, so a plain `docker compose up --build` dies at `dotnet restore` with `NU1301
UntrustedRoot` after several minutes — image *pulls* and Testcontainers are unaffected, which makes
it read as a NuGet outage. The session-start hook detects this and says so.

**To build Compose anyway, layer `docker-compose.ca.yml`.** It hands each Dockerfile the trust
bundle as an optional BuildKit secret, used for the restore and the runtime stage's `apk add` and
discarded with the step, so no CA reaches an image and a build without it is the one CI runs:

```bash
docker compose -f docker-compose.yml -f docker-compose.ca.yml up --build -d
```

Use this when the thing under test is Compose-specific — NGINX, the `/api/` proxy path, a Release
client, the migrations job's ordering. `ODYSSEY_PROXY_CA` overrides which bundle is passed; the hook
exports it. If it still fails with `UntrustedRoot`, the bundle is the wrong one rather than the
mechanism being broken — that file's header says how to identify the right issuer.

**Otherwise prefer Aspire**, which is faster and needs none of this: it builds on the **host** and
containerises only MariaDB, while serving the same ports (client 5199, API 5188, MariaDB 3307), so
everything below this section works unchanged.

```bash
dotnet run --project Odyssey.AppHost --launch-profile http
until curl -fs http://localhost:5188/healthz >/dev/null; do sleep 2; done && echo "API ready"
```

**Never add `-c Release` to that command.** `Odyssey.Client` picks the API address at compile time
(`Program.cs`): `#if DEBUG` hardcodes `http://localhost:5188`, while Release falls back to
same-origin `/api/` — correct *only* under Compose, where NGINX proxies it. Under Aspire that path
hits the SPA fallback, so every API call returns `index.html` and the WASM app dies parsing HTML as
JSON. `node driver.mjs health` still passes (it probes the API directly), and the stack looks
healthy, but every browser action fails and the screenshot is a blank page with a red *"An unhandled
error has occurred"* bar. Debug is the default; the trap is reflexively matching the `-c Release`
that CLAUDE.md uses for `dotnet build`.

The `--launch-profile http` part is unrelated to this and merely avoids the Linux dev-certificate
banner on the Aspire *dashboard*; the API and client are HTTP either way.

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

Aspire instead: stop the AppHost (Ctrl-C, or kill the `dotnet run` process) — it owns the MariaDB
container's lifetime, so the container goes with it. The **data** does not: `AppHost.cs` gives
MariaDB a named volume, so like Compose the volume outlives the container and the idempotent seed
skips on the next launch. `/reset-environment` is the way to get a clean dataset from either stack.

## Gotchas

- **No `chromium-cli` here** — the driver uses the Node `playwright` package against the cached
  chromium. The cache dir name is Playwright's build number (`chromium-1234`), *not* a chromium
  version; `playwright@1.62.0` is the version that maps to it. Bumping playwright without a matching
  cached build means a download (which may fail offline).
- **`PLAYWRIGHT_BROWSERS_PATH` may redirect the lookup.** A Claude Code session exports it as
  `/opt/pw-browsers`, so the driver looks there and **not** in `~/.cache/ms-playwright` — which is
  why the pin has to match what that directory actually holds, and why `~/.cache` being empty is not
  evidence of a missing browser. `/opt/pw-browsers/chromium` is a convenience symlink for
  `executablePath`; the session-start hook repoints it at the installed build, because an image that
  bakes browsers in pins it to *its* build and that pin does not move when the package is bumped.
- **`client` is up before the API is ready.** Its `depends_on` waits only for the API *container*,
  not `/healthz`. Always poll `/healthz` before driving, or login will flake.
- **A re-`up` does NOT reseed** (idempotent seed + persistent volume). If you expect fresh data and
  don't get it, you wanted `/reset-environment` or `down -v`.
- **Login is label-driven** (MudBlazor): the driver fills `getByLabel('Username or Email')` /
  `getByLabel('Password')` and clicks the **`Sign in`** button, then waits for the URL to leave
  `/login`. Newly *registered* users can't sign in (`RequireConfirmedAccount` + admin-approval) —
  only the seeded demo users work out of the box.
- **MariaDB is on host port `3307`**, not 3306 (the in-container port is 3306).
- **Don't `dotnet run` the client** against the **Compose** stack — the SPA is served by NGINX from
  the Docker build; rebuilding it separately desyncs the `blazor.boot.json` asset hashes. This does
  not apply to the Aspire stack, where a dev-server client is exactly what the AppHost starts.

## Troubleshooting

- `node driver.mjs health` → SPA OK but API FAIL: the API container is still starting or crashed —
  `docker logs odyssey-api 2>&1 | tail -30`. Migrations must complete first
  (`docker logs odyssey-migrations`).
- Driver hangs at login / times out waiting to leave `/login`: API not ready (poll `/healthz`), or
  the DB isn't seeded (`docker logs odyssey-migrations`), or you overrode creds with a non-seeded
  user.
- `Executable doesn't exist at .../chromium-XXXX`: the installed playwright version wants a browser
  build that isn't present **in the directory it is looking in** — check `PLAYWRIGHT_BROWSERS_PATH`
  before concluding the browser is missing, since it may be resolving `/opt/pw-browsers` rather than
  `~/.cache/ms-playwright`. Fix by running `npx playwright install chromium`, or by realigning the
  pin with `Microsoft.Playwright` in `Directory.Packages.props` (both are `1.62.0`).
- Driver reports a blank page or `An unhandled error has occurred`, while `node driver.mjs health`
  passes: an Aspire stack built `-c Release`. See the Aspire section above — rebuild it Debug.
- Build fails on `docker compose up --build`: confirm the .NET 10 base images pull and there's disk
  for the multi-stage build; re-run — layer caching makes the retry fast.
