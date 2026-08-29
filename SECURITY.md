# Security policy

Odyssey handles personal financial records, authentication credentials, and uploaded
documents. Security reports are welcome and taken seriously.

## Reporting a vulnerability

**Please do not open a public issue for a security vulnerability.**

Report it privately through GitHub's private vulnerability reporting:

1. Go to the [Security tab](https://github.com/centralcmd/odyssey/security/advisories).
2. Click **Report a vulnerability**.

That creates a private advisory visible only to the maintainers. If private reporting is
unavailable to you, open a regular issue containing only a request for a private contact
channel — no details.

Please include, where you can:

- the affected component (API endpoint, client page, container, workflow) and version or commit;
- a description of the impact — what an attacker gains;
- reproduction steps or a proof of concept;
- any suggested remediation.

You should get an acknowledgement within a few days. Since this is a personal project
maintained in spare time, please allow reasonable time for a fix before public
disclosure. We will credit you in the advisory unless you would rather stay anonymous.

## Scope

In scope — anything in this repository:

- the ASP.NET Core API (`Odyssey.Api`) and the domain services;
- authentication, 2FA, password reset, and the permission-claim authorization model;
- the Blazor WebAssembly client (`Odyssey.Client`), including XSS and CSRF;
- file upload and the AI file-analysis path;
- container images, `docker-compose*.yml`, and the GitHub Actions workflows;
- the EF Core contexts and migrations.

Out of scope:

- findings that require an already-compromised host or an already-authenticated administrator;
- the deliberately weak defaults in `.env.example`, the base `docker-compose.yml`, and
  `Odyssey.AppHost/appsettings.json` — concretely, the database credentials `root_password`
  and `odyssey_password`, which appear in all three and are therefore published here. These
  are development values, dev-scoped by construction, and each of the three files says so at
  the point the values appear. They are weak on purpose: a placeholder that is obviously a
  placeholder is safer than one that looks real enough to survive into a deployment. Reporting
  that they are guessable is not a finding — they are not secret, and this policy publishes
  them. What keeps them contained: the base compose stack binds every published port to
  `127.0.0.1`, the production overlay (`docker-compose.prod.yml`) resets them, `.env.prod.example`
  ships every secret empty so the deploy refuses to start rather than falling back to a value
  published here, and the Aspire AppHost is a local dev orchestrator that is never deployed —
  it ships no image and is not part of any release. A finding that any of *those* containments
  fails — a weak value reaching the production path, an overlay that does not actually reset
  one, a published port escaping the loopback binding — is very much in scope;
- the seeded demo dataset and its shared password. Demo seeding is gated to the
  **Development and Testing environments only** — an allow-list, so any other environment
  name refuses, and an explicit `Seed:DemoData=true` cannot override it (it is logged as
  ignored). **This includes finding `Odyssey!Demo1` inside the released `…-migrations`
  container image.** The seeder lives in the migrations job and references
  `Odyssey.TestData`, so the demo generators — and that constant — are compiled into the
  published image and `strings` will find them. They are unreachable there:
  `DemoDataSeeder.IsEnabled` refuses outside Development/Testing, and the seeded users only
  exist on a database that was seeded. A report that the string is *present* is not a
  finding; a report that a deployment **outside Development or Testing** actually created
  those users is;
- vulnerabilities in third-party dependencies that already have a public CVE — Dependabot
  tracks those. Report them if Odyssey uses the dependency in a way that makes the impact
  worse than upstream describes.

## Supported versions

Only the latest release on `main` receives security fixes. Releases are tagged
`v<MAJOR>.<MINOR>.<PATCH>`; container images are published to GHCR per release. Pre-1.0
means no backports to older minors.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Anything older | No |

## Deploying Odyssey safely

If you self-host, the deployment notes in [`docs/deployment.md`](docs/deployment.md) are
part of the security posture, not optional advice. In particular: use
`docker-compose.prod.yml`, fill in every secret `.env.prod.example` ships empty, terminate TLS,
and never enable demo-data seeding on an internet-facing instance. The empty values are not an
oversight: the deploy refuses to start until they are set, rather than falling back to a value
published here.
