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

# Option C — point at any already-running instance (e.g. the Aspire stack)
E2E_BASE_URL=http://localhost:5199 dotnet test Odyssey.E2ETests
```

| Variable | Default | Purpose |
|---|---|---|
| `E2E_BASE_URL` | `http://localhost:5199` | Base URL of the client to drive |
| `E2E_MANAGE_STACK` | unset | When `true`, the fixture runs `docker compose up -d --build` and `down` |

If the stack is unreachable or Chromium can't be installed, the tests **skip** (they never
fail for a missing environment), so they're safe to include in a normal `dotnet test` run.

## Notes

- The Chromium download requires network access on first run.
- Building the client container performs Blazor WASM trimming; on some host architectures that
  publish step can fail in Docker. If so, run the client via Aspire (`dotnet run --project
  Odyssey.AppHost`) and use Option C with the Aspire client URL.
