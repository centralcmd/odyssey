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
if it's unreachable.

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

## Notes

- Authentication uses `POST /login?useCookies=true` and reuses the returned cookie via a
  `CookieContainer` — the same flow a browser/SPA uses.
- Expected allow/deny per role is derived from `PermissionClaims.{Admin,Owner,User,Guest}Claims`,
  so the matrix tracks the real policy and can't silently drift.
