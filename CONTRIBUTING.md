# Contributing to Odyssey

Thanks for taking an interest. Odyssey is a personal-finance application built as a
personal project, developed in the open. Issues, discussions and pull requests are
welcome.

Please read [`SECURITY.md`](SECURITY.md) before reporting anything security-related —
security reports go through private advisories, not public issues.

**Have a question rather than a change?** [`SUPPORT.md`](SUPPORT.md) says where it goes;
short answer, [Discussions](https://github.com/centralcmd/odyssey/discussions).

By taking part you agree to abide by the [Code of Conduct](CODE_OF_CONDUCT.md). Conduct
concerns go to <odyssey-code-of-conduct.trustee054@passmail.net>, which is separate from the
security channel above.

## Getting set up

Requirements: the **.NET 10 SDK** (`dotnet --info` should show a 10.x SDK) and **Docker**
for the full stack and the container-backed tests.

```bash
git clone https://github.com/centralcmd/odyssey.git
cd odyssey
cp .env.example .env          # defaults work as-is for local development
docker compose up --build
```

That brings up the client on `http://localhost:5199`, the API on `http://localhost:5188`,
and MariaDB on host port `3307`. The stack seeds a deterministic demo dataset; sign in
with any of the seeded users listed in the README's *Demo logins* section.

Full setup detail — Aspire, SMTP, the bootstrap administrator — is in
[`docs/getting-started.md`](docs/getting-started.md).

## Building and testing

```bash
dotnet build Odyssey.sln -c Release

# Everything. The Docker and browser tiers self-skip when their prerequisites are
# missing, so this is safe without Docker or a running stack.
dotnet test Odyssey.sln
```

The suite is tiered, and the fast tiers need nothing beyond the SDK:

| Project | Tier | Needs |
|---|---|---|
| `Odyssey.Core.Tests` | Unit / service | nothing (EF InMemory) |
| `Odyssey.Api.Tests` | API integration via `WebApplicationFactory` | nothing (EF InMemory) |
| `Odyssey.Client.Tests` | Blazor component tests | nothing |
| `Odyssey.ApiClient.Tests` | The shared API client | nothing |
| `Odyssey.MigrationService.Tests` | The demo seeder | nothing |
| `Odyssey.Worker.Tests` | The background-job host | nothing |
| `Odyssey.IntegrationTests` | Real MariaDB — migrations, FK cascade, decimal fidelity | Docker (Testcontainers); self-skips otherwise |
| `Odyssey.E2ETests` | Playwright browser smoke | a running, seeded stack; self-skips otherwise |
| `Odyssey.E2ETests.Api` | API security/permissions over real HTTP | a running, seeded stack; self-skips otherwise |
| *perf* (`Category=Perf`) | Volume and time-budget checks | skipped unless `ODYSSEY_REQUIRE_TIER` names `perf`; run on the Release PR |

Please add tests with your change. CI runs build + the full suite on every pull request.

## Architecture in one minute

- `Odyssey.Api` — ASP.NET Core Web API; controllers split by domain.
- `Odyssey.Client` — Blazor WebAssembly frontend (MudBlazor v9), served by NGINX.
- `Odyssey.ApiClient` — the typed HTTP client, deliberately free of any web/UI dependency.
- `Odyssey.MigrationService` — runs EF Core migrations before the API starts.
- `Odyssey.Worker` — the background-job host, its own container with no HTTP surface.
- `Odyssey.AppHost` — the .NET Aspire orchestrator for local development.
- `Odyssey.Core`, `Odyssey.Context`, `Odyssey.Dtos` — all business logic, the EF context, and
  all DTOs, each split by folder per module.
- `Odyssey.TestData` — the deterministic demo-data generators shared by the seeders and the tests.

There is one EF Core context, `OdysseyContext`, owning the whole schema — identity and auth
alongside the domain — in a single MariaDB database. `CLAUDE.md` documents the conventions in more
depth and is worth skimming before a first change — it is written for AI assistants but the
rules apply to everyone.

Two conventions catch people out:

- **Permission claims live in two places on purpose.** The vocabulary is in
  `Odyssey.Dtos/Authorization/PermissionClaims.cs`; the role mapping is server-only,
  in `Odyssey.Context/Authorization/RolePermissions.cs`. Adding a claim means adding the
  constant and adding it to `RolePermissions.AllClaims` — no migration, since `RoleClaimSeeder`
  reconciles the rows at runtime. `AuthorizationPolicyTests` fails if you miss the second step.
- **No runtime feature toggles for new features.** Gate capabilities with permission
  claims, not config flags. The exception is an on/off setting for an integration with a
  third-party system (Claude, a rates feed) — see `docs/architecture/settings.md`.

### The design system is not in the repository

The Odyssey Design System lives in a claude.ai artifact. It is the source of truth the Blazor
`Ods*` components are reconciled against, not the other way round.
[`.claude/design-system-artifact.md`](.claude/design-system-artifact.md) has the link, its layout
and how a sync works. Two consequences for a pull request:

- **Don't restate a design value in the client** to make an implementation look right. If the
  design is wrong, the fix is in the artifact; if the implementation is wrong, the fix is in
  `Odyssey.Client`.
- **CI does not compare the client with the design system.** The parity tests skip unless
  `ODYSSEY_DESIGN_SYSTEM_DIR` names a downloaded copy of the artifact, so a design-facing PR should
  say whether it was checked against the artifact.

Until October 2026 the design system was a Claude Design export committed as
`Odyssey Design System/`, so older history (`git log -- 'Odyssey Design System'`) and some code
comments still refer to paths in that folder. The reference doc maps them to the artifact.

## Database migrations

There is one `DbContext` and one migrations folder. Always use the dotnet EF tooling rather
than hand-writing the scaffold:

```bash
dotnet tool install --global dotnet-ef

dotnet ef migrations add <MigrationName> \
  --project "./Odyssey.Context" \
  --startup-project "./Odyssey.Api/Odyssey.Api.csproj" \
  --context OdysseyContext
```

`Odyssey.Context/README.md` has the details.

## Pull requests

- Branch off `main`; keep a PR to one logical change.
- Follow the commit conventions below — release automation reads them.
- Make sure `dotnet build` and `dotnet test` pass locally.
- Fill in the pull-request template.
- Note that CI validates fork PRs without access to repository secrets, so the
  coverage-badge step is skipped on them. That is expected.

Maintainers can invoke an automated review by commenting `@claude review` on a PR.

### Maintainers: reviewing an outside PR with `@claude`

**Read the diff yourself before typing `@claude review` on a pull request from someone
outside the project.** The workflow's author gate is on the *commenter*, not on the PR
author — that is deliberate, and it is what makes reviewing an outside contribution
possible at all. But it means the moment a maintainer asks, an agent holding the
repository's Claude credentials and `pull-requests: write` reads attacker-authored diff
text, file contents and PR description as part of its prompt. Prompt injection in that
content is the attack; the gate cannot filter it, because the gate is about who asked.

Two mitigations are already in place and neither replaces the read:

- Both workflows restrict the agent to a read-only tool allow-list — no bare `Bash`, no
  `Write`/`Edit`, no `WebFetch` — so an injected instruction has no way to execute
  anything or reach the network. See the comments in `.github/workflows/claude.yml`.
- `CLAUDE.md` carries a standing instruction that issue and PR text is untrusted data,
  never an instruction.

So: skim the diff for text aimed at the reviewing agent rather than at a human, and treat
anything of that shape as reason not to run the review at all. The same applies to a bare
`@claude` on an outsider's issue, which is the documented way to have Claude look at one.

### If you use Claude Code

`.claude/agents/` and `.claude/skills/` are tracked because CI runs them — they are the system
prompt the `@claude` workflows use. **No permission configuration is tracked**, deliberately:
a clone pre-approves no tool and auto-accepts no edit, so Claude Code behaves in this repo the
way it does in any other. If you want the maintainer's setup, put your own preferences in
`.claude/settings.local.json`, which is gitignored:

```json
{
  "permissions": {
    "defaultMode": "acceptEdits",
    "allow": ["Bash(dotnet build *)", "Bash(dotnet test *)"]
  }
}
```

---

## Licensing

Odyssey is released under the [BSD 2-Clause License](LICENSE). By submitting a contribution
you agree that it is your own work (or that you have the right to submit it) and that it is
licensed to the project under those same terms — inbound is outbound. There is no CLA and no
sign-off requirement.

**Please do not edit `LICENSE` in a pull request.** It is not only a legal file, it is a
runtime input: `LicenseDocumentProvider` hashes its contents and serves it as the agreement
users accept at sign-in, so any change to the file — including adding a name to the copyright
line — invalidates every existing acceptance and forces all users of every deployment to
re-accept. Contributors are credited through the git history and the GitHub contributors list,
not the copyright line. `LICENSE` is owned by the maintainers via
[`CODEOWNERS`](.github/CODEOWNERS).

---

## Git and versioning

Commits follow **Conventional Commits** (`<type>(<scope>): <description>`): a lowercase subject
under 80 characters where possible (120 at most) with no trailing period, the *why* in the body, and any issue number in the
body rather than the subject. Release automation reads these messages, so the type matters:
`fix:` makes a patch release and `feat:` a minor one. CI checks the PR title against these rules,
because it becomes the commit subject when the PR is squash-merged.

The allowed types and scopes, and how versioning works, are in
[`CLAUDE.md` → *Git & Versioning*](CLAUDE.md#git--versioning). In short: versions follow SemVer,
[release-please](https://github.com/googleapis/release-please) keeps a Release PR open and
merging it tags the release — nobody tags by hand or edits `<Version>` in `Directory.Build.props`.

## Code style

[Microsoft's C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions),
with comments used sparingly, and one house rule on field naming that surprises people: private
instance fields are plain camelCase with **no `_` prefix** — except in Blazor components
(`.razor` / `.razor.cs`), which use `_camelCase`. Static fields and constants are PascalCase. The
details and examples are in [`CLAUDE.md` → *Code Style*](CLAUDE.md#code-style).

CI fails on whitespace that differs from `.editorconfig`. Fix it before pushing with:

```bash
dotnet format whitespace Odyssey.sln
```
