# Odyssey.E2ETests.Api

API end-to-end tests — the HTTP sibling of `Odyssey.E2ETests` (which drives the browser). These
hit the **real running API** over HTTP and authenticate through the **real `/login` cookie flow**
(no `TestAuthHandler`, no injected claims), so they verify security, permissions, status codes and
contracts the way a real client experiences them.

They reuse the same already-running, seeded stack: the migration service seeds the deterministic
demo data (including the four role users — Admin / Owner / User / Guest), and these tests log in as
those users to assert the permission matrix end to end.

## What it covers

- **Permission matrix** — for each seeded role, log in for real and assert each gated endpoint
  returns `200` or `403` according to that role's actual `PermissionClaims` (e.g. `GET /api/users`
  is Admin-only). This proves the real login bakes the role's claims into the cookie and the
  `[Authorize(Policy = …)]` gates enforce them — which the in-process faked-auth tests cannot.
- **Authentication** — unauthenticated requests are challenged with `401`; a wrong password is
  rejected with `401`.
- **Contracts/status codes** — unknown resource → `404`; seeded data is actually served as JSON.

All tests are **read-only**, so they're safe against the shared seeded database.

## Running

Needs a **running, seeded stack** (the API on `http://localhost:5188`). Tests **skip** (not fail)
if nothing is listening there — see *Skip versus fail* below for when they fail instead.

```bash
# Bring up just what the API tests need (no client image), then test, then tear down.
docker compose up -d --build api      # starts mariadb + migrations + api
dotnet test Odyssey.E2ETests.Api
docker compose down -v

# Or let the fixture manage the full Compose stack itself:
E2E_MANAGE_STACK=true dotnet test Odyssey.E2ETests.Api

# Or point at any running instance (e.g. the Aspire stack's API):
E2E_API_BASE_URL=http://localhost:5188 dotnet test Odyssey.E2ETests.Api
```

| Variable | Default | Purpose |
|---|---|---|
| `E2E_API_BASE_URL` | `http://localhost:5188` | Base URL of the API to drive |
| `E2E_MANAGE_STACK` | unset | When `true`, the fixture runs `docker compose up -d --build` / `down` |
| `ODYSSEY_REQUIRE_TIER` | unset | Comma-separated tiers that must run: `e2e-api` here (also `integration`, `e2e`, `all`). A required tier **fails** instead of skipping when the API is absent |
| `E2E_READY_TIMEOUT_SECONDS` | 10 s, 120 s when required, 180 s when managed | How long to poll `/healthz` |

**Skip versus fail** (issue #257). Only **nothing listening** at the API address is a skip, and only
while `ODYSSEY_REQUIRE_TIER` does not name `e2e-api`. An API that answers but never turns healthy
inside the window fails, as does a `/healthz` that answers `text/html` — that is the client's SPA
fallback, meaning `E2E_API_BASE_URL` names the client rather than the API. Requiring the tier also
stretches the wait to 120 s, so a stack whose port is live while migrations still run is waited for
rather than skipped.

## Notes

- Authentication uses `POST /login?useCookies=true` and reuses the returned cookie via a
  `CookieContainer` — the same flow a browser/SPA uses.
- Expected allow/deny per role is derived from `PermissionClaims.{Admin,Owner,User,Guest}Claims`,
  so the matrix tracks the real policy and can't silently drift.
