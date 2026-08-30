# Odyssey

**A self-hosted personal finance and life-admin application.** Odyssey tracks accounts,
transactions and budgets across multiple currencies, and keeps the paperwork that goes with
them — contracts, subscriptions, insurance policies, tax statements and the source documents
themselves — in one place, alongside a journal, task board, calendar and photo library.

It is a full-stack .NET 10 application: an ASP.NET Core API, a Blazor WebAssembly frontend,
and MariaDB. You run it yourself; there is no hosted service and your financial data stays on
your own infrastructure.

[![CI](https://github.com/centralcmd/odyssey/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/centralcmd/odyssey/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/centralcmd/64518dff4edba6bddbefae35f7cc0f65/raw/odyssey-coverage.json)](https://github.com/centralcmd/odyssey/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/centralcmd/odyssey)](LICENSE)
[![Latest release](https://img.shields.io/github/v/tag/centralcmd/odyssey?label=release&sort=semver)](https://github.com/centralcmd/odyssey/tags)
[![Last commit](https://img.shields.io/github/last-commit/centralcmd/odyssey)](https://github.com/centralcmd/odyssey/commits/main)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

## What it does

**Money**

- **Accounts** — 15 account types spanning assets and liabilities (cash, current, savings,
  investment, pension, property, vehicle, credit card, mortgage, loans, tax debt) with
  balances, custodians, interest-rate terms and fee schedules, plus per-account value estimates.
- **Transactions** — searchable, taggable and server-paged, with statuses, comments, attached
  documents and a counterparty resolved against the contact book.
- **Budgets** — per-year budgets broken down by tag, reported against actual spend.
- **Multi-currency** — explicit exchange rates per directed currency pair, so cross-currency
  accounts convert without silent guesswork.
- **Contracts, subscriptions and insurance policies** — recurring commitments with terms,
  renewal and derived status.
- **Tax statements** — with a reconciliation report that flags what does not line up.

**Documents and everything else**

- **Files** — upload and attach documents to the records they belong to.
- **AI file analysis** *(optional, off by default)* — extracts transactions from statements and
  receipts and suggests contact and tag matches. Enabling it sends the uploaded document to the
  Anthropic API; it is consent-gated, audit-logged, and stays off unless you configure a key.
- **Journal, tasks and calendar** — with RFC 5545 iCalendar import/export (`VEVENT`, `VTODO`,
  `VJOURNAL`).
- **Photos** — an album-based library with tagging, favourites and embedded-metadata extraction.
- **Contacts** — people and organisations, shared across finance and journal records.

**Administration**

- Cookie-based authentication with TOTP two-factor, email confirmation, self-service and
  admin-initiated password reset, and admin approval for new registrations.
- Fine-grained authorization: 101 permission claims mapped onto Admin / Owner / User / Guest
  roles, enforced server-side on every endpoint.
- A dark-first design system, keyboard command palette, and per-user page-state persistence.

## Screenshots

The Finance module, running against the deterministic demo seed — the figures, names and
addresses below are synthetic.

![The Odyssey dashboard in dark theme: a "Good afternoon, Owner" header reading "Net worth
$270,865.76 across 21 accounts", a teal net-worth area chart running from 2016 to 2026, and a
recent-transactions table listing groceries, dining out and a card purchase with their tags,
statuses, amounts and dates.](docs/images/dashboard.png)

**Dashboard** — net worth across every account, with the most recent transactions underneath.

<table>
<tr>
<td width="50%">

![The Accounts page with its overview panel open: two donut charts, "Asset allocation" over 10
accounts and "Liabilities" over 7, each with a ranked legend — Primary Residence at 70% of
$977,499.02 in assets, Home Mortgage at 77% of $453,133.26 owed.](docs/images/accounts.png)

**Accounts** — asset and liability allocation over a 21-account portfolio, with per-type and
per-status rollups.

</td>
<td width="50%">

![The Transactions page with its overview and filter regions open: status and direction
breakdown tiles above a search box and account, status, tag and direction filters, over a
paged table of 2,743 transactions showing description, contact, account, tag, status, amount
and date.](docs/images/transactions.png)

**Transactions** — server-paged, searchable, and filterable by account, status, tag and
direction.

</td>
</tr>
<tr>
<td width="50%">

![An expanded budget record, "Household Budget 2026", showing its period, base currency and
status, then two donut charts: planned income of $98,000.00 across salary, bonus and
investment income, and planned expenses of $79,200.00 across 14 tags led by housing at
31%.](docs/images/budgets.png)

**Budgets** — a year's planned income and expenses broken down by tag, reported against
actual spend.

</td>
<td width="50%">

![An expanded tax statement, "Tax Year 2025", flagged with the warning "Declared net worth
diverges from derived balances — review before approval", above a table comparing declared
figures with Odyssey-derived ones: total assets $980,000 against $718,999, a variance of
$261,001 in amber.](docs/images/tax-reconciliation.png)

**Tax reconciliation** — declared figures against the ones Odyssey derives from your
accounts and tags, with every variance called out.

</td>
</tr>
<tr>
<td width="50%">

![The Subscriptions page with its renewals and overview regions open: three upcoming-renewal
rows for Weekly Meal Kit, Spotify Family and Netflix, then monthly and yearly run-rate tiles
reading kr 1,786.27 and kr 21,435.24, and counts by interval and
status.](docs/images/subscriptions.png)

**Subscriptions** — recurring commitments with a monthly and yearly run rate, and what
renews next.

</td>
<td width="50%">

![The Insurance page with its renewals and overview regions open: two red alerts for lapsed
Auto and Home cover, then current premium of $4,380 USD shown as approximately kr 47,608.69
in total and coverage of $1M USD as approximately kr 10.9M insured, with counts by type and
status.](docs/images/insurance.png)

**Insurance** — policies with per-currency premium and coverage subtotals, converted into a
single figure.

</td>
</tr>
</table>

## Architecture

| Project | Role |
|---|---|
| `Odyssey.Api` | ASP.NET Core Web API — controllers split by domain |
| `Odyssey.Client` | Blazor WebAssembly frontend (MudBlazor v9), served by NGINX |
| `Odyssey.ApiClient` | Typed HTTP client, free of any web/UI dependency so non-browser consumers can use it |
| `Odyssey.MigrationService` | Runs EF Core migrations before the API starts |
| `Odyssey.AppHost` | .NET Aspire orchestrator for local development |
| `Odyssey.<Domain>[.Context\|.Dtos]` | Business logic, EF Core context and DTOs per domain |

One EF Core context — `OdysseyContext` — owns the whole application: identity, profiles and
preferences alongside finance, journal, tasks, photos, calendars and contacts, in a single MariaDB
database. Keeping them in one model is what makes every cross-module reference, and every
"who created this" column, a real foreign key.

## Quick start

```bash
cp .env.example .env
docker compose up --build
```

Then open `http://localhost:5199` and sign in with one of the seeded demo users listed under
[Demo data seeding](#demo-data-seeding).

> The default `docker-compose.yml` is a **development** stack: weak database passwords, Swagger
> on, demo data seeded, MariaDB published to the host. To deploy for real, layer
> `docker-compose.prod.yml` on top and copy `.env.prod.example` instead — see
> [`docs/deployment.md`](docs/deployment.md).

## Docker setup

This repository now includes container support for the full stack:

- `Odyssey.Client/Dockerfile` builds and serves the Blazor WebAssembly client with NGINX.
- `Odyssey.Api/Dockerfile` builds and runs the ASP.NET Core API.
- `docker-compose.yml` orchestrates:
  - `client` (frontend) on `http://localhost:5199`
  - `api` (backend) on `http://localhost:5188`
  - `mariadb` (database) on `localhost:3307`

### Start everything

```bash
docker compose up --build
```

### Run migrations (if needed)

When running via Docker Compose, the stack now includes an explicit migration step.
The migrations service runs once, then exits successfully before the API starts.

If you need to rerun migrations (e.g., after changing migrations), run:

```bash
docker compose run --rm migrations
```

> Note: `docker compose up` will automatically run the migrations service and will not start the API until migrations complete.

### Environment configuration (`.env`)

`docker compose` automatically reads a `.env` file in the repository root.

1. Copy the template:

```bash
cp .env.example .env
```

2. Set runtime flags:

- `ASPNETCORE_ENVIRONMENT=Development` to run the API in development mode.
- `SWAGGER_ENABLED=true` to expose Swagger/OpenAPI even outside development.

### The first administrator (bootstrap)

The initial administrator is created **out of band**, by the `migrations` job, from two configuration
values. Registration order confers no privilege at all: whoever registers first is an ordinary account
like everyone else.

```bash
BOOTSTRAP_ADMIN_EMAIL=admin@demo.example.com
BOOTSTRAP_ADMIN_PASSWORD=<16+ chars, upper + lower + digit + symbol>
```

| Variable | Purpose |
|---|---|
| `BOOTSTRAP_ADMIN_EMAIL` | Email/username of the seeded administrator. |
| `BOOTSTRAP_ADMIN_PASSWORD` | One-time password; must satisfy the password policy and must be changed at first sign-in. |

Four things to know about it:

- **It fires only on a completely empty user table.** On any later deploy the values are read and
  ignored, so a redeploy can never revert a password changed in the app — and this is *not* a way to
  recover a lost administrator (use the forgot-password flow for that).
- **The password is a one-time secret.** The account is created with `MustChangePassword` set, so
  signing in with it lands on `/change-password-required` and the API refuses every endpoint bar the
  handful needed to escape until a new password is set. After that come the ordinary first-run gates:
  `/accept-terms`, then `/onboarding`.
- **Booting with no administrator is impossible.** After seeding, the migrations job asserts that at
  least one enabled `Admin` exists and exits non-zero if not — and the API does not start behind a
  failed migrations job.
- **Locally you need neither variable.** The demo seed (`SEED_DEMO_DATA=true`, the dev default) already
  creates an `Admin`, which satisfies that assertion. Set them only if you run with
  `SEED_DEMO_DATA=false` against an empty database.

### Registration (admin approval)

By default, a newly registered account is created **disabled** and cannot sign in until an
administrator enables it (Users admin page → enable). This is the email-confirmation flow's
companion gate and is independent of it, and it now applies to every account without exception.

> Configured at runtime in **System Settings**, not `appsettings.json`: the registration-approval and
> email-confirmation gates, the insurance summary knobs, the sixteen import/export volume caps, and the
> AI-analysis processor disclosure and policy, the transactional-email sender identity and
> per-recipient send throttle, the nine per-request caps on contracts, insurance, photos and
> journal links, and the maximum upload size. Secrets, connection strings and the SMTP transport stay
> in environment configuration — see issue #421's Non-Goals for why each one does. A value you had
> configured for one of the migrated settings is carried into the store on upgrade and keeps applying
> until an administrator changes it in the UI.
>
> Two of these are **tighten-only**, because a raised value would be silently overridden by a limit
> compiled or configured somewhere earlier in the request: the photo link/album caps (request-model
> validation) and the upload size (the startup transport ceiling — see
> [`docs/deployment.md`](docs/deployment.md), "Upload size"). Both publish that ceiling to the settings
> UI so the control bounds itself, and reject a larger value with a `400` naming it.

The gate is admin-editable at runtime (System Settings → require admin approval); turning it off
enables new accounts immediately. A user blocked by it sees a sign-in error indicating the account is
not active.

### Email (account confirmation and password reset)

Registration uses ASP.NET Core Identity's built-in email-confirmation flow: a new account
stays unconfirmed (and cannot sign in) until the user clicks the link sent to their address.
The same SMTP setup delivers the self-service password-reset link (issue #405), so a user who
forgets their password can get back in without an administrator.

**Mail is configured entirely at runtime — there are no email environment variables.** Every part of
it is edited at **System settings → Email** by an administrator holding
`system-settings.security.update`, is stored in the database, and takes effect on the **next send**
with no restart and no cache wait:

| Setting | Notes |
|---|---|
| SMTP host | The relay. Empty means *mail is not configured*: every send is logged and skipped. |
| SMTP port | `587` for STARTTLS submission, `465` for implicit TLS, `25` for an unauthenticated internal relay. |
| Use STARTTLS | On for 587; off for implicit TLS on 465. |
| Client base URL | The public origin every confirmation and reset link is composed against. |
| From address / From name | The envelope sender. Must be an address the relay is authorised to send as. |
| Messages per recipient / window / tracked addresses | The anti-mailbomb throttle on the anonymous mail path. |
| Require email confirmation | Off means users can sign in immediately after registering. **On by default.** |
| SMTP username / SMTP password | Entered at **System settings → Credentials**, encrypted at rest. |

The transport moved into the store in issue #8; the sender identity and throttle in #421; the relay
credential in #445. What made the transport the last to move is worth knowing, because it shows up as
behaviour you will meet:

> **Changing the SMTP host or port — or turning STARTTLS off — clears the stored SMTP username and
> password**, in the same transaction as the change itself. The SMTP client connects *first* and
> authenticates *second*, so whatever relay is set receives the stored credential — and host and port
> together are what identify a relay, so a port change moves it to a different listener just as a host
> change does. A credential entered for an encrypted transport must likewise not be replayed over a
> cleartext one. Clearing it means there is nothing left to hand over. You will be asked to confirm,
> and you will have to re-enter the credential afterwards. That is the control working, not a fault.

Until an SMTP host is set, no email is sent — the link is written to the API log instead, which is
fine for local testing but means users cannot self-confirm or reset. One limit on that fallback,
because a reset link is a working credential rather than a mere convenience: **the link is only ever
logged in Development and Testing.** Any other environment logs that the mail could not be sent and
nothing else.

> **On a fresh production deployment mail starts switched off**, and the API no longer refuses to
> start without it — a value entered through the settings UI cannot be a precondition for that UI
> coming up. Configure SMTP immediately after your first sign-in, *before* you blank
> `BOOTSTRAP_ADMIN_PASSWORD`: if that one-time password is lost while mail is unconfigured there is no
> self-service recovery, because the forgot-password flow is exactly the thing that needs working
> mail. The System settings page says so in its header while the host is empty. See
> [`docs/deployment.md`](docs/deployment.md).

Nothing proves mail actually arrives except sending some, so **send yourself a password reset after
configuring** — that is the only check covering the whole path, relay credential included.

#### Using a Gmail account

Gmail works with the settings above, but **not** with your normal account password —
Google retired password ("less secure app") access. Use a Gmail **App Password** instead:

1. Enable **2-Step Verification** on the Google account (App Passwords are unavailable until
   it is on).
2. Go to **Google Account → Security → App passwords**, generate one (e.g. named "Odyssey"),
   and copy the 16-character code.
3. At **System settings → Email**, set the SMTP host to `smtp.gmail.com`, the port to `587` and
   STARTTLS on, and the from address to your Gmail address. Then, at **System settings →
   Credentials**, enter the Gmail address as the **SMTP username** and the 16-character App Password
   as the **SMTP password**.

Notes specific to Gmail:

- Gmail forces the `From` address to match the authenticated account, so confirmation emails
  will come from your Gmail address regardless of the from address you set.
- A personal Gmail account is limited to roughly 500 recipients/day and may throttle bursts —
  fine for development and low volume, but use a dedicated transactional email provider for
  production-scale sending.
- The App Password grants access to the mailbox; revoke it from the same Google settings page
  if it leaks.

## Client

- The Blazor WebAssembly client uses `ApiBaseAddress` from `Odyssey.Client/wwwroot/appsettings.json`, which the browser fetches as a static file. Host-process environment variables are not visible to the WASM runtime, so that file is the only place to set it. Left blank (the default), a Release build requests relative to the current origin at `/api/` — which is what nginx proxies under Docker — while a Debug build falls back to the fixed local API port `http://localhost:5188`.

### API and Swagger endpoints

- API base URL (from Docker): `http://localhost:5188`
- CORS allowed origins are configured in `Odyssey.Api/appsettings.json` under `Cors:AllowedOrigins`.
- Swagger UI: `http://localhost:5188/swagger`
- OpenAPI JSON: `http://localhost:5188/openapi/v1.json`

> Swagger is enabled when `ASPNETCORE_ENVIRONMENT=Development` **or** `SWAGGER_ENABLED=true`.

### Networking notes

- The client container proxies `/api/*` requests to the API container (`api:8080`) via `Odyssey.Client/nginx.conf`.
- The API container connects to MariaDB using internal Docker DNS (`mariadb:3306`).
- There is a **single MariaDB database named `odyssey`** (created by `docker/mariadb/init/01-init.sql`), named by `ODYSSEY_DATABASE`. It cannot be split: one EF context owns the whole schema, with foreign keys throughout.

### Stop and clean up

```bash
docker compose down
```

To also remove database data:

```bash
docker compose down -v
```

## Aspire orchestration

The repository now includes an Aspire AppHost project at `Odyssey.AppHost` that orchestrates the local development stack:

- `client` (`Odyssey.Client`)
- `api` (`Odyssey.Api`)
- `mariadb` (Docker container `mariadb:11.4`)

### Run with Aspire

```bash
dotnet run --project Odyssey.AppHost
```

This starts the Aspire dashboard and launches all required resources. The AppHost configures:

- MariaDB initialization using `docker/mariadb/init`
- Aspire settings (DB credentials, ports, URLs, and DB names) are read from `Odyssey.AppHost/appsettings.json` and passed into resources at startup. The committed database credentials are the same throwaway local-development defaults Docker Compose falls back to, so a fresh clone runs with nothing configured; override them with user secrets (`dotnet user-secrets set "Aspire:MariaDb:Password" <value> --project Odyssey.AppHost`) rather than editing the tracked file
- The API connection string pointing at the single `odyssey` database (overridable in the `Aspire` settings)
- HTTP-only URLs for API/client in Aspire to avoid requiring local HTTPS dev certificates

### Notes

- Aspire binds MariaDB to host port `3307`, so ensure that port is available.
- Aspire runs the API/client on HTTP, so no local HTTPS development certificate is required. The API endpoint is fixed at `http://localhost:5188` (hardcoded in `Odyssey.AppHost/AppHost.cs`, matching Docker Compose and the client's Debug fallback); the client gets a dynamic port.
- If you already have the Docker Compose stack running, stop it first to avoid port conflicts.
- MariaDB is a container with a persistent volume, so a credential you change after the first run does not re-initialise the existing database — remove the `mariadb-data` volume to start over.
- For full containerized deployment scenarios, `docker-compose.yml` remains available.
- If you need to customize Aspire startup values, edit the `Aspire` section in `Odyssey.AppHost/appsettings.json`.

## Testing

The full strategy and rationale live in [`docs/test-environment-and-e2e-spec.md`](docs/test-environment-and-e2e-spec.md). There are five tiers:

| Project | What it covers | Prerequisite |
|---|---|---|
| `Odyssey.Core.Tests` | Unit / service logic | none (EF InMemory) |
| `Odyssey.Api.Tests` | API integration via `WebApplicationFactory` + the shared `OdysseyApiFactory` fixture | none (EF InMemory) |
| `Odyssey.MigrationService.Tests` | The demo-data seeder | none (EF InMemory) |
| `Odyssey.IntegrationTests` | Real-engine behaviour InMemory can't model — actual migrations, FK `ON DELETE CASCADE`, decimal/datetime fidelity | **Docker** (Testcontainers-MariaDB) |
| `Odyssey.E2ETests` | Playwright browser smoke: sign in as a seeded user and see seeded data | a **running, seeded stack** |
| `Odyssey.E2ETests.Api` | API security/permissions/contracts over real HTTP + real login cookie (permission matrix across the seeded role users) | a **running, seeded stack** |

```bash
# Everything. The Docker and browser tiers self-skip when their prerequisites
# are missing, so this never fails for lack of Docker or a running stack.
dotnet test Odyssey.sln

# A single tier, e.g. the real-engine integration tests (needs Docker):
dotnet test Odyssey.IntegrationTests
```

The integration and E2E tiers **skip cleanly** (reported as skipped, not failed) when Docker or a
reachable stack is absent, so they're safe in any `dotnet test` run. The E2E project has its own
[README](Odyssey.E2ETests/README.md) covering how to point it at a running stack
(`E2E_BASE_URL`) or have it manage Compose for you (`E2E_MANAGE_STACK=true`).

## Demo data seeding

In the Development and Testing environments the stack seeds a **deterministic synthetic dataset** so the app is
immediately usable and the E2E tests have data to assert on.

- **What's seeded:** four role-based login users, plus tags, contacts, a 21-account
  portfolio (~10 years of history), per-year budgets, recurring transactions, and exchange
  rates covering every currency pair in use (so multi-currency accounts convert without
  warnings). Currencies, roles, and permission claims are reference data (already created by
  migrations) and are referenced, never recreated.
- **Login users** (all share the password `Odyssey!Demo1`, all confirmed + enabled):

  | Email | Role |
  |---|---|
  | `admin@demo.example.com` | Admin |
  | `owner@demo.example.com` | Owner |
  | `user@demo.example.com` | User |
  | `guest@demo.example.com` | Guest |

- **How it works:** generators live in `Odyssey.TestData` (the single source of truth, reused by
  the tests); `Odyssey.MigrationService` runs the `DemoDataSeeder` right after migrations. Seeding
  is **gated** (only Development and Testing ever seed — every other environment refuses, and an
  explicit `Seed:DemoData=true` cannot override that; inside those two the flag still turns
  seeding off) and **idempotent** (skips if already present).
- **Toggle it:** Compose reads `SEED_DEMO_DATA` (default `true` for the dev stack); Aspire reads
  `Aspire:Seed:DemoData`. The data is identical on every fresh database because the generator seed
  is fixed — wipe the DB volume (`docker compose down -v`) to reseed from scratch.

## Contributing

Setup, the test tiers, commit conventions, versioning and code style are in
[`CONTRIBUTING.md`](CONTRIBUTING.md). Security reports go through a private advisory —
see [`SECURITY.md`](SECURITY.md).

## Getting help

Questions — configuration, SMTP, deployment, whether a behaviour is intended — go in
[Discussions](https://github.com/centralcmd/odyssey/discussions). [`SUPPORT.md`](SUPPORT.md)
covers where each kind of report belongs and what to include.

## License

Odyssey is released under the [BSD 2-Clause License](LICENSE).

Third-party material redistributed with Odyssey — the bundled Roboto, Roboto Mono and
Material Icons webfonts, and the NuGet dependency set — is inventoried in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

> Note that `LICENSE` is also a runtime input: the API hashes it and serves it as the
> agreement users accept at sign-in. Editing that file invalidates every existing
> acceptance and forces all users to re-accept, so third-party notices are kept separate
> from it deliberately.
