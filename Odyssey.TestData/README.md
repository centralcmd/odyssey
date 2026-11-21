# Odyssey.TestData

The deterministic demo dataset: one pure function (`DemoDataSet.Build()`) from fixed catalogues and a fixed Bogus
seed to a complete object graph. It is the **single source of truth** for demo data — the seeders persist it and
the tests materialise it, so what a test asserts is what a developer sees in the running stack.

## What lives where

| Path | Holds |
|---|---|
| `DemoDataSet.cs` | `DemoDataSet.Build(anchorDate?)` — every entity list, files and photos included. |
| `DemoDataDefaults.cs` | The fixed inputs: `RandomizerSeed`, `AnchorDate` (the dataset's "today"), the year range, `UserPassword`, the demo terms text, growth rates, currencies. |
| `DemoUsers.cs` | The four role logins (Admin, Owner, User, Guest; `…@demo.example.com`). |
| `DemoDataGate.cs` | Whether a host may seed at all. |
| `DemoFiles.cs` | `DemoFile` / `DemoPhoto`: a row plus its bytes, which only the worker can write. |
| `DeterministicGuid.cs` | Stable ids from string keys, so foreign keys wire up without EF and E2E references stay stable. |
| `Catalog/` | Hand-enumerated reference sets: the account portfolio, contacts and tags. Names double as keys. |
| `Generators/` | One generator per entity family (transactions, budgets, contracts, calendars, photos, …), driven by the catalogues and the seed. |

## Who uses it

- **`Odyssey.MigrationService`** — `DemoDataSeeder` persists everything without bytes.
- **`Odyssey.Worker`** — `DemoFileSeeder` writes the files and photos onto the object store, with the rows that name them.
- **Tests** — `Odyssey.Core.Tests` (`DemoDataSetTests`), `Odyssey.Api.Tests`, `Odyssey.MigrationService.Tests`,
  `Odyssey.IntegrationTests`, and the E2E tiers, which sign in as `DemoUsers` and look for seeded records.

Currencies, roles and permission claims are reference data seeded elsewhere; the dataset references them and never
creates them.

## Rules that matter most

- **Seeding is an allow-list**: only Development and Testing ever seed, and `Seed:DemoData=true` cannot override
  that anywhere else; inside those two the flag can still turn it off — `DemoDataGate.cs`, root
  [`CLAUDE.md`](../CLAUDE.md) → *Testing & Demo Data*.
- **Keep it deterministic**: everything derives from `DemoDataDefaults` (seed and `AnchorDate`) — no `DateTime.Now`,
  no `Guid.NewGuid()`. `DemoDataSetTests` and `DemoDataDeterminismTests` (`Odyssey.MigrationService.Tests`) check it.
- **The demo password `Odyssey!Demo1` is public** (it is in the repository README), which is why the gate exists —
  never relax it.
- **Demo files appear after the stack is healthy**; a test that needs one polls with `TestShared/DemoFileWait` —
  [`TestShared/README.md`](../TestShared/README.md).
- **The seed is a dev fixture, not a sample of production**: test a data migration against shapes it does not
  produce — root [`CLAUDE.md`](../CLAUDE.md) → *EF Core Migrations*.

The dataset's design (portfolio, budgets, transaction streams) is specified in
[`docs/test-environment-and-e2e-spec.md`](../docs/test-environment-and-e2e-spec.md) §3.
