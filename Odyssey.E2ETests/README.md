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

If the stack is **unreachable** or Chromium can't be installed, the tests **skip** (they never fail
for a missing environment), so they're safe to include in a normal `dotnet test` run. Note what that
does and does not cover: the fixture probes for a stack, not for a *working* one, so a stack that
answers on `:5199` but is internally broken — the two cases in **Notes** below — produces a suite of
red timeouts rather than skips.

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
  Release. Rebuild the stack without `-c Release` and re-run.
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
