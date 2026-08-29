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
with any of the seeded users listed in the README's *Demo data seeding* section.

Full setup detail — Aspire, SMTP, the bootstrap administrator — is in the
[README](README.md).

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
| `Odyssey.IntegrationTests` | Real MariaDB — migrations, FK cascade, decimal fidelity | Docker (Testcontainers); self-skips otherwise |
| `Odyssey.E2ETests` | Playwright browser smoke | a running, seeded stack; self-skips otherwise |
| `Odyssey.E2ETests.Api` | API security/permissions over real HTTP | a running, seeded stack; self-skips otherwise |

Please add tests with your change. CI runs build + the full suite on every pull request.

## Architecture in one minute

- `Odyssey.Api` — ASP.NET Core Web API; controllers split by domain.
- `Odyssey.Client` — Blazor WebAssembly frontend (MudBlazor v9), served by NGINX.
- `Odyssey.ApiClient` — the typed HTTP client, deliberately free of any web/UI dependency.
- `Odyssey.MigrationService` — runs EF Core migrations before the API starts.
- `Odyssey.<Domain>` / `.Context` / `.Dtos` — business logic, EF context, and DTOs per domain.

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
  claims, not config flags.

### `Odyssey Design System/` is vendored, not source

That directory is an **export** from the tool the design system is authored in, and it is the
source of truth the Blazor `Ods*` components are reconciled against — not the other way round.
Two consequences for a pull request:

- **Don't hand-edit it** to make an implementation look right. If the design is wrong, the fix
  is a new export; if the implementation is wrong, the fix is in `Odyssey.Client`.
- **`_ds_bundle.js` is generated but not disposable.** It is the compiled bundle every
  `preview/*.html` and `components/*.html` page loads (`window.OdysseyDesignSystem_*`), and the
  repository has no build step that would regenerate it — so it stays tracked despite being 2 MB
  and churning on each `docs: update design system` commit. `git log -- 'Odyssey Design System'`
  is noisy for that reason and safe to skip when reading history.

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

### Commit messages

This project uses **Conventional Commits** (`<type>(<scope>): <description>`).

#### Format

```
<type>(<scope>): <short description>

[optional body]

[optional footer(s)]
```

- The description is lowercase and does not end with a period.
- Keep the subject line under 72 characters.
- Use the body to explain *why*, not *what*.
- If this commit has a relevant issue on github, reference the number in the body, NOT in the short description.

#### Types

| Type | When to use |
|---|---|
| `feat` | A new feature visible to users or API consumers. |
| `fix` | A bug fix. |
| `chore` | Maintenance tasks — dependency bumps, tooling, configuration. |
| `refactor` | Code restructuring with no behavior change. |
| `test` | Adding or updating tests only. |
| `docs` | Documentation changes only. |
| `ci` | Changes to CI/CD configuration. |
| `perf` | Performance improvements. |

#### Allowed Scopes

Commits **must** use one of the following scopes, or omit the scope entirely for cross-cutting changes.

| Scope | Covers |
|---|---|
| `api` | `Odyssey.Api` — controllers, middleware, startup |
| `auth` | Authentication and authorization logic across any project |
| `client` | `Odyssey.Client` — Blazor pages, components, services |
| `core` | Domain business logic — `Odyssey.Core` (Finance + Journal modules) |
| `data` | `Odyssey.Context` — DbContext, entities, EF configuration |
| `migrations` | `Odyssey.MigrationService` and per-domain `Migrations` folders |
| `shared` | `Odyssey.<Domain>.Dtos` — DTOs and contracts |
| `infra` | Docker, Docker Compose, deployment configuration |
| `aspire` | `Odyssey.AppHost` — Aspire orchestration |
| `deps` | Dependency/package updates (any project) |
| `config` | App settings, secrets, environment variable changes |

#### Examples

```
feat(auth): add permission claims to JWT on login
fix(api): return 403 instead of 500 on missing permission claim
chore(deps): bump Pomelo.EntityFrameworkCore.MySql to 8.0.3
refactor(data): extract fluent config into separate IEntityTypeConfiguration classes
test(api): add integration tests for permission policy handler
docs: add git chapter to specification
feat(client): gate admin nav items behind users.manage_roles permission
fix(migrations): correct column type for ApplicationUser.CreatedAt
ci: add GitHub Actions workflow for build and test
```

---

## Versioning

This project follows **Semantic Versioning 2.0.0** (`MAJOR.MINOR.PATCH`).

| Segment | Increment when |
|---|---|
| `MAJOR` | A breaking change is introduced — removed or incompatible API endpoints, breaking schema changes, changed auth contracts |
| `MINOR` | New functionality is added in a backward-compatible way |
| `PATCH` | Backward-compatible bug fixes only |

### Git tags

Releases are tagged on `main` using the format `v<MAJOR>.<MINOR>.<PATCH>` (e.g. `v1.2.3`). Tags are annotated:

```bash
git tag -a v1.2.3 -m "release: v1.2.3"
git push origin v1.2.3
```

Pre-release identifiers follow the semver spec: `v1.0.0-alpha.1`, `v1.0.0-beta.2`, `v1.0.0-rc.1`.

### Version in code

The canonical version lives in the `<Version>` property of each `.csproj` that produces a deployable artifact (`Odyssey.Api`, `Odyssey.Client`). Both are kept in sync and match the git tag at the time of release.

```xml
<PropertyGroup>
  <Version>1.0.0</Version>
</PropertyGroup>
```

The API exposes the running version on the `/healthz` endpoint response body so it can be confirmed after deployment.

## Code style

This project follows the [Microsoft C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) with two exceptions covering field naming.
Use comments sparingly, only where they earn their place — prefer self-documenting code.

### Private fields — no underscore prefix

Microsoft convention prefixes private instance fields with `_`. This project uses plain camelCase instead:

```csharp
// Correct
private readonly AppDbContext db;
private int retryCount;

// Wrong
private readonly AppDbContext _db;
private int _retryCount;
```

### The Blazor-component carve-out

`Odyssey.Client`'s Razor components — `.razor` files and their `.razor.cs` code-behind — use
**`_camelCase`** for private fields (`private bool _isLoading;`). A component's fields sit next to
its `[Parameter]` properties, and the prefix is what keeps component-local state visually distinct
from the bound, externally-supplied ones.

Everything else in the client (`Services/`, `Auth/`, `Authorization/`, `Theme/`) and every other
project follows the plain camelCase rule above. The split is clean in both directions: a field in a
component should carry the prefix, a field in a service should not.

### Static fields — no `s_` prefix

Microsoft convention prefixes private static fields with `s_`. This project drops the prefix and applies the same casing rule as any other field — visibility determines case:

| Visibility | Convention | Example |
|---|---|---|
| `public` / `internal` static | PascalCase | `public static readonly string DefaultRole = "Guest";` |
| `private` / `protected` static | camelCase | `private static int instanceCount;` |

```csharp
// Correct
public static readonly string DefaultRole = "Guest";
private static int instanceCount;

// Wrong
private static int s_instanceCount;
```
