# Copilot Instructions — `centralcmd/odyssey`

## 🚀 High-level summary

**Odyssey** is a .NET full-stack finance application that includes:

- **Backend API:** Odyssey.Api — ASP.NET Core Web API (NET 10.0) providing financial data, file storage, and user preferences.
- **Frontend client:** Odyssey.Client — Blazor WebAssembly app served via NGINX.
- **Domain libraries:** Odyssey.Core (Finance + Journal modules), Odyssey.Dtos (all DTOs), Odyssey.ApiClient (the typed HTTP client).
- **Persistence:** MariaDB. **One** EF context, `OdysseyContext` in Odyssey.Context, owns the whole
  schema — identity and auth alongside finance, journal, tasks, photos, calendars and contacts —
  against a single `odyssey` database.
- **Dev orchestration:** docker-compose.yml and an Aspire-based dev host at Odyssey.AppHost.

---

## 🧰 Languages + Frameworks + Tooling

- **Runtime:** .NET 10.0 / ASP.NET Core
- **Frontend:** Blazor WebAssembly
- **ORM:** Entity Framework Core (EF Core), uses InMemory for tests
- **DB:** MariaDB (Docker)
- **Central package management:** Directory.Packages.props
- **Tests:** xUnit + `Microsoft.NET.Test.Sdk`

---

## 🏗️ How to build & validate changes

### 1) Prerequisites (always check)

- Install **.NET 10 SDK** (required).
- Ensure `dotnet` is on PATH and `dotnet --info` shows `.NET SDK 10.x`.
- Install EF tooling if working with migrations:
  ```bash
  dotnet tool install --global dotnet-ef
  ```

---

### 2) Restore + Build (always run before tests/PR)

From repo root:

```bash
dotnet restore
dotnet build Odyssey.sln -c Release
```

✅ *Expectation:* builds all projects without errors.

---

### 3) Run tests

From repo root:

```bash
dotnet test Odyssey.sln --no-build
```

- Tests are designed to run using **EF InMemory**, so they should not require Docker or a database.
- If you change DB schema or EF behavior, rerun tests to ensure no regressions.

---

### 4) Run locally (API + Client + DB)

#### Option A: Docker Compose (recommended for full stack)

From repo root:

```bash
docker compose up --build
```

Then:

- Frontend: `http://localhost:5199`
- API: `http://localhost:5188`
- Swagger: `http://localhost:5188/swagger`

To stop/clean:

```bash
docker compose down
docker compose down -v   # remove DB persistent data
```

📝 Note: docker-compose maps MariaDB to **host port 3307** (not 3306). If you want host 3306, adjust docker-compose.yml.

---

### 5) Run via Aspire dev orchestration (optional)

Run the Aspire stack from the Odyssey.AppHost project:

```bash
dotnet run --project Odyssey.AppHost
```

- Launches client + API + MariaDB, with the Aspire dev dashboard.
- ⚠️ Ports are **fixed, and the same ones Docker Compose uses**: MariaDB on host `3307`, client
  `5199`, API `5188`. Running Aspire and Compose at the same time collides — the second MariaDB
  comes up unreachable and host tools silently hit the wrong server. See
  [`docs/running-locally-alongside-a-live-stack.md`](../docs/running-locally-alongside-a-live-stack.md)
  for the port-remap recipe.
- Useful when you want the workspace orchestrated by Aspire (recommended for a consistent local config).

---

## 📁 Project layout & key locations

### Root
- Odyssey.sln → solution file, includes all projects.
- docker-compose.yml → standard docker stack.
- README.md → basic run instructions (docker + Aspire).
- Directory.Packages.props → central package versions used across all projects.

### Main projects
- Odyssey.Api → ASP.NET Core Web API (entry point Program.cs, config under appsettings.json)
- Odyssey.Client → Blazor WebAssembly frontend
- Odyssey.AppHost → Aspire orchestrator + dashboard

### Domain / shared libraries
- Odyssey.Core/Finance → finance business logic + services
- Odyssey.Core/Journal → journal, tasks, photos, calendar + contacts logic
- Odyssey.Context → the single `OdysseyContext` + all entities (one flat namespace, plus
  `Authorization/`, `Legal/` and `Secrets/` sub-namespaces) and the one `Migrations/` folder
- Odyssey.Dtos → all DTOs, split by folder/namespace (`Application/`, `Journal/`, `Finance/`,
  `Authorization/`). Keeps **zero** project references so the WASM client can reference it.

### Tests
- Odyssey.Core.Tests → unit / service (EF InMemory)
- Odyssey.Api.Tests → API integration via WebApplicationFactory (EF InMemory)
- Odyssey.MigrationService.Tests → the demo seeder (EF InMemory)
- Odyssey.ApiClient.Tests → the shared typed HTTP client
- Odyssey.IntegrationTests → real MariaDB via Testcontainers (needs Docker, else self-skips)
- Odyssey.E2ETests / Odyssey.E2ETests.Api → browser + API smoke against a running, seeded stack

---

## 🔍 Common gotchas and important details

### ✅ Dotnet version matters
All projects target **`net10.0`**. If the local environment uses an earlier SDK, builds will fail.

---

### ✅ Database connection expectations
appsettings.json defines an empty connection string by default. Production requires environment variable overrides (this is done by docker-compose in normal flow).

Important env vars:
- `UseInMemoryDatabase` (if `true`, EF uses InMemory instead of MariaDB). Note InMemory enforces
  **no foreign keys at all**, so FK cascade behaviour is only exercised by Odyssey.IntegrationTests.
- `ConnectionStrings__OdysseyConnection` — the one connection string. There is no second one;
  the former `ApplicationConnection` was removed when the contexts merged.

---

### ✅ Central package management
All NuGet versions are pinned in `Directory.Packages.props`. **Do not add a `Version=` attribute**
to a `PackageReference` in an individual `.csproj` — that is an error under central management.

---

### 🧩 Migrations (EF Core)
If changing database schema, follow existing patterns:

Example workflow for migrations:

```bash
dotnet tool install --global dotnet-ef

# create migration — one context owns the whole schema, identity included
dotnet ef migrations add NameOfMigration \
  --project "./Odyssey.Context" \
  --startup-project "./Odyssey.Api/Odyssey.Api.csproj" \
  --context OdysseyContext

# apply migration (example) — same project/startup-project pair as above
dotnet ef database update \
  --project "./Odyssey.Context" \
  --startup-project "./Odyssey.Api/Odyssey.Api.csproj" \
  --context OdysseyContext
```

Normally you don't apply by hand: **Odyssey.MigrationService** runs migrations before the API
starts, in both the Compose and Aspire stacks. Scaffolding resolves the provider through
Odyssey.Api and there is no `IDesignTimeDbContextFactory`, so `UseInMemoryDatabase` must be false,
the environment must not be `Testing`, and `ConnectionStrings__OdysseyConnection` must be set —
otherwise the context binds to InMemory and no usable migration comes out.

---

## 🔎 Where to look first for changes

When implementing a feature/bugfix, start by identifying the correct layer:

1. **API changes**: Controllers, Program.cs
2. **Business logic**: Odyssey.Core/Finance or Odyssey.Core/Journal
3. **Database models**: `Odyssey.Context/` (entities + `OdysseyContext.cs`)
4. **Client UI**: Pages, `Odyssey.Client/Components`

---

## ✅ Best practice for Copilot PRs (so they pass CI / validation)

1. **Build and test locally** before generating a PR:
   - `dotnet build Odyssey.sln`
   - `dotnet test Odyssey.sln`
2. **Run the stack or API** to sanity-check runtime behavior:
   - `docker compose up --build` OR `dotnet run --project Odyssey.AppHost`
3. **Avoid editing generated/cached files** unless required
4. **Search first** before writing new code:
   - Where is the data model defined?
   - What existing service does this belong to?
   - Is there a similar pattern elsewhere (e.g., Finance vs FileStorage)?

---

## 🧭 When to search vs. when to trust these instructions

✅ **Trust these instructions** when they answer:
- “How do I run tests?”
- “How do I run the app locally?”
- “What is the project structure?”
- “What SDK/version should I use?”

🔎 **Search only if:**
- You need a file that’s not described here.
- You need to confirm behavior not covered (e.g., a new entity or migration).
- You need to locate a specific controller/service by name.

---

> 📌 These instructions are intended to prevent wasted time on failed build attempts, missing dependencies, and guessing project conventions. If something appears inconsistent with the repo state, **re-check file contents** before making changes.