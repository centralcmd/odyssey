# Odyssey.Client.Tests

Tests for the Blazor WebAssembly client (`Odyssey.Client`), with no browser and no server. Three kinds live here,
side by side in one flat folder:

- **bUnit component and page tests** — render a component with MudBlazor and mocked services and assert on the
  markup and behaviour (`*Tests.cs` named after the component or page).
- **Source-lints** — text checks over the checked-in `.razor`, `.cs`, `.js`, `.css` and nginx files, for
  conventions a compiler cannot see (`*SourceLintTests.cs`, `*SurfaceTests.cs`, `SourceConventionTests.cs`,
  `RazorStringBindingTests.cs`, `NginxProxyConfigTests.cs`, …).
- **Design-system parity checks** — compare client values with the design-system artifact; these **skip** unless
  a downloaded copy is supplied (below).

## Helpers

| File | Use it for |
|---|---|
| `PageTestContext.cs` | A `BunitContext` for a whole page: MudBlazor, a principal holding the given permissions, auto-mocked services. |
| `ClientSource.cs` | The client's source tree for a lint: `Root`, `RazorFiles()`, `RazorFilesIn(…)`, `CodeBehind(…)` (follows partial files). |
| `DesignSystemSource.cs` | Locates a design-system file; skips or fails through `TestTierGate`. |
| `RecordingSnackbar.cs`, `FakeBrowserFile.cs`, `FixedImportLimits.cs`, … | Test doubles for client services. |
| `Fixtures/` | The Material Icons ligature list and the script that regenerates it from the bundled font. |
| `TestShared/…` (linked) | `SourceTree`, `SourceLint` (comment stripping), `TestTierGate`, `Http/` and a few shared corpora — see [`TestShared/README.md`](../TestShared/README.md). |

A lint reads files through `ClientSource` / `SourceTree` and strips comments with `SourceLint`, so build output is
skipped and a doc comment discussing a forbidden pattern does not trip it.

## Running

```bash
dotnet test Odyssey.Client.Tests                     # parity checks skip

# With the design-system parity checks (what a design-system sync runs):
ODYSSEY_DESIGN_SYSTEM_DIR=/path/to/artifact/project \
ODYSSEY_REQUIRE_TIER=design-system dotnet test Odyssey.Client.Tests
```

`ODYSSEY_DESIGN_SYSTEM_DIR` names a downloaded copy of the artifact's `project/` folder;
[`.claude/design-system-artifact.md`](../.claude/design-system-artifact.md) says how to get one.
`ODYSSEY_REQUIRE_TIER=design-system` turns a missing copy from a skip into a failure.

## Rules that matter most

- **Don't remove the parity checks' skip, and don't pin the artifact's values as literals** — CI cannot read the
  artifact — root [`CLAUDE.md`](../CLAUDE.md) → *The design system*.
- **Only an absent prerequisite skips**; a copy that is named but missing a file fails — `TestShared/TestTierGate.cs`.
- **A client-side copy of a server cap or rule is a defect**, and the upload/import cap lints here enforce it —
  root [`CLAUDE.md`](../CLAUDE.md) → *Cross-cutting rules*.
- **Don't weaken a lint to fit new code**; most pin a defect a review found in several places at once. Fix the
  code, or extend the lint's allow-list where one exists and say why.
- **Component private fields are `_camelCase`**, everything else in the client plain camelCase — root
  [`CLAUDE.md`](../CLAUDE.md) → *Code Style*.
