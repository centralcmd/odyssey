# TestShared

Test helpers shared between test projects. **This is not a project**: there is no `.csproj` here and nothing
references it. Each test project *links* the files it needs into its own compilation, in its `.csproj`:

```xml
<Compile Include="..\TestShared\TestTierGate.cs" Link="TestShared\TestTierGate.cs" />
<Compile Include="..\TestShared\FileStorage\*.cs" Link="TestShared\FileStorage\%(Filename)%(Extension)" />
<None Include="..\TestShared\MapOriginVectors.json" Link="Fixtures\MapOriginVectors.json" CopyToOutputDirectory="PreserveNewest" />
```

So every test assembly compiles its own copy, and types here are `internal` (or `public` within that assembly)
with no cross-project identity. A new file is picked up only by projects whose `.csproj` names it or a matching
wildcard — check the `TestShared` item group of each consumer.

## What lives where

| Path | Holds |
|---|---|
| `TestTierGate.cs` | The skip-or-fail rule for environment-dependent tiers and `ODYSSEY_REQUIRE_TIER` (`integration`, `e2e`, `e2e-api`, `design-system`, `perf`, `all`). |
| `PerfTier.cs` | The opt-in perf tier: `[Trait("Category", "Perf")]` plus `PerfTier.SkipUnlessRequested()` as a test's first statement. |
| `StackProbe.cs` | Polls a running stack and classifies the answer for `TestTierGate` (nothing listening = skip; anything else short of ready = fail). |
| `DemoFileWait.cs` | Bounded polling (3 minutes) for demo files and photos, which the worker writes after the stack is healthy. |
| `SourceTree.cs`, `SourceLint.cs` | The repository-root lookup and file walk (build output pruned), and comment/string stripping for source-lints. |
| `FileStorage/` | A test host for the object store, a test Files store, test storage features, and `StoredFileFixtures` to stage files and photos with their bytes on the store. |
| `Fixtures/` | `ContactImageFixtures` (real image containers and adversarial variants) and `met-symbol-codes.txt`. |
| `Http/`, `Logging/`, `Lookups/` | `RecordingHttpHandler`, `RecordingLogger`, a settable import/export limits lookup. |
| `News/`, `InstrumentPrices/` | A worker-process harness for the news job; a deterministic price source. |
| `HexColorNegativeCorpus.cs`, `CalendarZoneGoldenCases.cs`, `MapOriginVectors.json` | Single tables that several layers are tested against, so server and client cannot drift. |

## Rules that matter most

- **Only an absent prerequisite skips; a present-but-broken one fails**, and a CI job meant to run a tier sets
  `ODYSSEY_REQUIRE_TIER` — `TestTierGate.cs`, root [`CLAUDE.md`](../CLAUDE.md) → *Testing & Demo Data*,
  [`docs/test-environment-and-e2e-spec.md`](../docs/test-environment-and-e2e-spec.md) §4.4.
- **A new volume or time-budget test goes in the perf tier** with a small per-PR sibling for its functional half —
  `PerfTier.cs`.
- **A test that needs a demo file, photo, contact image or album polls with `DemoFileWait`**, never assumes it —
  root [`CLAUDE.md`](../CLAUDE.md) → *Testing & Demo Data*.
- **Stage files through `StoredFileFixtures`**, bytes on the store as production writes them; there is no
  database blob to fall back on — [file-storage.md](../docs/architecture/file-storage.md).
- **Source-lints walk files through `SourceTree.Files` and strip comments with `SourceLint`**, so build output and
  other agents' worktrees are excluded and documentation may discuss what a lint forbids.
- **Shared corpora are the one list**: add a case to `HexColorNegativeCorpus` or `CalendarZoneGoldenCases` rather
  than a local copy in one test project.
