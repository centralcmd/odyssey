# Odyssey.E2ETests

End-to-end smoke tests that drive the full running stack in a real browser with
[Playwright for .NET](https://playwright.dev/dotnet/): nginx → Blazor WASM → API → MariaDB.
They sign in as a seeded demo user and assert seeded data renders, exercising cookie auth, the
SPA, the API, and the demo seed together.

Credentials and the seeded names asserted on come from `Odyssey.TestData`, the same source the
seeder uses — so the tests stay in lockstep with the data.

## Running

The tests need a **running, seeded stack** (seeding is on by default in Development). Playwright
downloads its Chromium build automatically on first run.

```bash
# Option A — bring the stack up yourself, then test, then tear down
docker compose up -d --build
dotnet test Odyssey.E2ETests
docker compose down -v

# Option B — let the fixture manage the stack (up + down) for you
E2E_MANAGE_STACK=true dotnet test Odyssey.E2ETests

# Option C — point at any already-running instance (e.g. the Aspire stack).
# Build the Aspire stack DEBUG (the default) — see "A Release client can't reach the API" below.
dotnet run --project Odyssey.AppHost --launch-profile http
E2E_BASE_URL=http://localhost:5199 dotnet test Odyssey.E2ETests
```

| Variable | Default | Purpose |
|---|---|---|
| `E2E_BASE_URL` | `http://localhost:5199` | Base URL of the client to drive |
| `E2E_MANAGE_STACK` | unset | When `true`, the fixture runs `docker compose up -d --build` and `down` |
| `ODYSSEY_REQUIRE_TIER` | unset | Comma-separated tiers that must run: `e2e` here (also `integration`, `e2e-api`, `all`). A required tier **fails** instead of skipping when its stack is absent |
| `E2E_READY_TIMEOUT_SECONDS` | 10 s, 120 s when required, 180 s when managed | How long to wait for the stack to answer |

### Skip versus fail (issue #257)

Only an **absent** environment is a skip: nothing listening at `E2E_BASE_URL` (connection refused, no
answer inside the probe), or a Chromium download that cannot reach the network. That keeps the suite
safe in a normal `dotnet test` run — unless `ODYSSEY_REQUIRE_TIER` names `e2e`, which turns those into
failures too. CI's E2E job sets it.

A stack that **answers but is broken always fails**, whatever the variable says:

- the client never answers `2xx` inside the window (e.g. `502` throughout);
- the API behind the client's same-origin `/api/` path (the Compose NGINX proxy) never becomes healthy
  — the fixture waits for it up to the required-tier window, so a stack still migrating is waited out
  rather than failed on the first sign-in;
- **the Release-under-Aspire client** (see **Notes**). When `/api/healthz` on the client origin returns
  `text/html` — no proxy there, which is normal for a Debug client under Aspire — the fixture loads the
  app once in Chromium and watches its startup calls. If the *app itself* requests its own origin's
  `/api/…` and gets HTML back, the tier fails with a message naming the cause. A Debug client calls
  `http://localhost:5188` directly and never requests that path, so it cannot trip the check.

Other stack-internal breakage (for example the stale dev server below) still surfaces as test
timeouts; the fixture checks reachability and the API route, not every asset.

## Notes

- The Chromium download requires network access on first run.
- Building the client container performs Blazor WASM trimming; on some host architectures that
  publish step can fail in Docker. If so, run the client via Aspire (`dotnet run --project
  Odyssey.AppHost`) and use Option C with the Aspire client URL.
- **A Release client can't reach the API under Aspire or a bare `dotnet run`.** Build those Debug —
  which is the default, so this bites only when `-c Release` is passed explicitly (easy to do by
  reflex, since the build command in CLAUDE.md is `-c Release`). `Odyssey.Client/Program.cs` picks
  the API address at **compile time**: `#if DEBUG` hardcodes `http://localhost:5188`, while Release
  falls back to same-origin `/api/`, which is correct **only** under Docker Compose, where NGINX
  proxies it. Under Aspire nothing serves that path, so every API call lands on the SPA fallback and
  returns `index.html`; the WASM app then dies parsing HTML as JSON. The give-away is in the browser
  console — `JsonException: ExpectedStartOfValueNotFound, <` — and on the page itself, the red
  *"An unhandled error has occurred"* bar over a blank body. Compose is unaffected and should stay
  Release. Rebuild the stack without `-c Release` and re-run. The fixture's browser preflight
  (above) now catches this before any test runs and fails the tier with that diagnosis, instead of
  a suite of sign-in timeouts.
- **Against a `dotnet run` client (Option C), rebuild and re-*start* the dev server, in that order.**
  The Blazor dev server serves an `index.html` naming **fingerprinted** framework assets
  (`_framework/dotnet.<hash>.js`). A `dotnet build`/`dotnet test` of the solution regenerates those
  hashes, so a dev server left running from before the build serves an `index.html` whose
  `dotnet.<hash>.js` **404s** — the WASM runtime never starts and the page stays blank. The symptom
  is every test in the suite timing out identically on `waiting for GetByLabel("Username or Email")`,
  which reads like a rate-limited or slow sign-in and is neither.

A whole-suite failure of that shape means the stack, not the app — but **both** notes above produce
it, so tell them apart at the browser console rather than by guessing: a `dotnet.<hash>.js` **404**
is the stale dev server (restart it), while `JsonException: ExpectedStartOfValueNotFound, <` is a
Release client (rebuild it Debug). Either way, re-run after fixing — no test change is needed.
