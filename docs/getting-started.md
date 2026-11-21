# Getting started: running Odyssey yourself

This guide covers running Odyssey on your own machine — for development, or as a private
self-hosted instance — with Docker Compose or the .NET Aspire AppHost. It also covers the first
administrator, registration, mail and the demo dataset.

Deploying for real (TLS, the production overlay, backups, the Data Protection key ring, file
storage volumes, the security checklist) is [`deployment.md`](deployment.md). Where the two
overlap, this guide links there rather than repeating it.

## Prerequisites

- **Docker** with Compose v2, for the Compose stack, for the MariaDB container under Aspire, and
  for the `Odyssey.IntegrationTests` tier.
- **The .NET 10 SDK**, for Aspire, for building and for running the tests (`dotnet --info` should
  show a 10.x SDK). The Compose stack alone builds inside its containers and does not need it.

## Docker Compose

The full stack runs in containers. `Odyssey.Client/Dockerfile` builds the Blazor WebAssembly client
and serves it with NGINX; `Odyssey.Api/Dockerfile` builds and runs the API. `docker-compose.yml`
orchestrates five services:

| Service | What it is | Address on the host |
|---|---|---|
| `client` | The frontend, served by NGINX | `http://localhost:5199` |
| `api` | The ASP.NET Core API | `http://localhost:5188` |
| `migrations` | A one-shot job that applies migrations and seeds, then exits before the API starts | — |
| `worker` | The background-job host | no published port |
| `mariadb` | The database | `localhost:3307` |

Every published port binds `127.0.0.1` only, so none of the dev stack is reachable from the
network. Don't remove those prefixes to test from another machine; use the production overlay
instead (see [`deployment.md`](deployment.md)).

### Configure and start

`docker compose` reads a `.env` file in the repository root. Copy the template — its defaults work
as-is for local development — and start everything:

```bash
cp .env.example .env
docker compose up --build
```

Two runtime flags in `.env` are worth knowing:

- `ASPNETCORE_ENVIRONMENT=Development` runs the API in development mode.
- `SWAGGER_ENABLED=true` exposes Swagger/OpenAPI outside Development (never in Production — see
  [API and Swagger](#api-and-swagger)).

The default `docker-compose.yml` is a **development** stack: weak fallback database passwords,
Swagger on, demo data seeded, MariaDB published to the host. To deploy, layer
`docker-compose.prod.yml` on top and start from `.env.prod.example` instead — see
[`deployment.md`](deployment.md).

### Migrations

`docker compose up` runs the `migrations` service first and does not start the API until it has
completed successfully. To rerun it on its own (for example after changing a migration):

```bash
docker compose run --rm migrations
```

How to create a migration is in [`CLAUDE.md`](../CLAUDE.md) (*EF Core Migrations*).

### Networking

- The client container proxies `/api/*` to the API container (`api:8080`) via
  `Odyssey.Client/nginx/templates/default.conf.template`.
- The API reaches MariaDB through internal Docker DNS (`mariadb:3306`).
- There is a **single MariaDB database named `odyssey`**, created by
  `docker/mariadb/init/01-init.sql` and named by `ODYSSEY_DATABASE`. It cannot be split: one EF
  context owns the whole schema, with foreign keys throughout.

### Stop and clean up

```bash
docker compose down       # stop the stack
docker compose down -v    # also remove the database volume (and with it the demo data)
```

If a teammate's stack is already running on the same host, Compose and Aspire collide on ports
3307, 5188 and 5199 — follow
[`running-locally-alongside-a-live-stack.md`](running-locally-alongside-a-live-stack.md), and never
`down -v` someone else's volume.

## .NET Aspire

The AppHost at `Odyssey.AppHost` orchestrates the same resources for local development, with the
Aspire dashboard on top:

- `client` (`Odyssey.Client`)
- `api` (`Odyssey.Api`)
- `migrations` (`Odyssey.MigrationService`), which completes before the API starts
- `worker` (`Odyssey.Worker`)
- `mariadb`, a MariaDB container; its image version is `Aspire:MariaDb:Version` in
  `Odyssey.AppHost/appsettings.json`

```bash
dotnet run --project Odyssey.AppHost
```

**Run it in Debug — never with `-c Release`.** The client resolves the API address at compile time,
and a Release client only works behind the Compose stack's NGINX; [`CLAUDE.md`](../CLAUDE.md)
explains the failure it produces.

What the AppHost configures:

- MariaDB is initialised from `docker/mariadb/init`, and binds host port **3307** — make sure it is
  free (stop a running Compose stack first).
- Settings — database credentials, ports, URLs, database names, the demo-seed flag — are read from
  the `Aspire` section of `Odyssey.AppHost/appsettings.json` and passed into the resources at
  startup. The committed database credentials are the same throwaway defaults Compose falls back
  to, so a fresh clone runs with nothing configured. Override them with user secrets rather than
  editing the tracked file:

  ```bash
  dotnet user-secrets set "Aspire:MariaDb:Password" <value> --project Odyssey.AppHost
  ```

- The API connection string points at the single `odyssey` database (overridable in the same
  section).
- The API and client run over plain HTTP, so they need no local HTTPS development certificate. The
  API is fixed at `http://localhost:5188` (in `Odyssey.AppHost/AppHost.cs`, matching Compose and the
  client's Debug fallback); the client gets a dynamic port, shown on the dashboard.
- The **dashboard** is a separate matter: its default launch profile is HTTPS, so on Linux it shows
  a `No trusted development certificate was found` banner until the certificate is trusted in the
  .NET, OpenSSL and NSS stores — see
  [`https-dev-certificate-on-linux.md`](https-dev-certificate-on-linux.md), or run
  `dotnet run --project Odyssey.AppHost --launch-profile http` to avoid it.
- MariaDB keeps its data in a persistent volume (`mariadb-data`), so a credential changed after the
  first run does not re-initialise the existing database. Remove the volume to start over.

## The client's API address

The Blazor WebAssembly client reads `ApiBaseAddress` from `Odyssey.Client/wwwroot/appsettings.json`,
which the browser fetches as a static file. Host-process environment variables are not visible to
the WASM runtime, so that file is the only place to set it. Left blank (the default), a Release
build calls `/api/` on the current origin — which NGINX proxies under Compose — while a Debug build
falls back to `http://localhost:5188`. The logic is in `Odyssey.Client/Program.cs`.

## API and Swagger

| | Address |
|---|---|
| API | `http://localhost:5188` |
| Swagger UI | `http://localhost:5188/swagger` |
| OpenAPI JSON | `http://localhost:5188/swagger/v1/swagger.json` |

Swagger is served when `ASPNETCORE_ENVIRONMENT=Development`, or when `SWAGGER_ENABLED=true` in any
environment other than `Production`; it is never served in Production (issue #451). CORS allowed
origins are `Cors:AllowedOrigins` in `Odyssey.Api/appsettings.json`.

## Demo data

In the Development and Testing environments the stack seeds a **deterministic synthetic dataset**,
so the app is usable straight away and the E2E tests have data to assert on.

**Sign in** with any of the four role users. They share the password `Odyssey!Demo1` and are created
confirmed and enabled:

| Email | Role |
|---|---|
| `admin@demo.example.com` | Admin |
| `owner@demo.example.com` | Owner |
| `user@demo.example.com` | User |
| `guest@demo.example.com` | Guest |

**What is seeded:** those users, plus tags, contacts, an account portfolio with about ten years of
history, per-year budgets, recurring transactions and exchange rates for every currency pair in use
(so multi-currency accounts convert without warnings) — then demo files, photos and contact images.
Currencies, roles and permission claims are reference data created by the migrations; the seed
references them and never recreates them.

**How it works:** the generators live in `Odyssey.TestData` with a fixed seed — the single source
of truth, reused by the tests — so every fresh database gets identical data. The `migrations` job
runs `DemoDataSeeder` right after migrating, and the `worker` then writes the demo files, photos and
contact images onto the file volumes, so **those appear shortly after the stack is healthy**, not
before. Seeding is **gated** — only Development and Testing ever seed, and an explicit
`Seed:DemoData=true` cannot override that elsewhere — and **idempotent**: it skips what is already
there.

**Turn it off** with `SEED_DEMO_DATA=false` (Compose, default `true`) or `Aspire:Seed:DemoData`
(Aspire). To reseed from scratch, wipe the database volume (`docker compose down -v`). The details
are in [`CLAUDE.md`](../CLAUDE.md) (*Testing & Demo Data*).

## The first administrator

Registration order confers no privilege: whoever registers first is an ordinary account. The
initial administrator is created out of band by the `migrations` job from `BOOTSTRAP_ADMIN_EMAIL`
and `BOOTSTRAP_ADMIN_PASSWORD`, only on a completely empty user table, as a one-time password that
must be changed at first sign-in. After seeding, the job exits non-zero unless at least one enabled
`Admin` exists, and the API does not start behind a failed migrations job.

**Locally you need neither variable**: the demo seed already creates an `Admin`, which satisfies
that check. Set them only when you run with `SEED_DEMO_DATA=false` against an empty database (they
are commented out in `.env.example`).

The password policy, the first-sign-in sequence and why the values are not a recovery mechanism
are in [`deployment.md`](deployment.md) (*First-run notes*).

## Registration and approval

A newly registered account is created **disabled** and cannot sign in until an administrator
enables it on the **Users** page. A user blocked by this sees a sign-in error saying the account is
not active. The gate is independent of email confirmation (below), and applies to every account.
It is on by default and editable at runtime at **System settings → require admin approval**;
turning it off enables new accounts immediately.

### Runtime settings

Instance-wide policy is configured at **`/settings`** and stored in the database, not in
`appsettings.json`: the registration-approval and email-confirmation gates, the Contracts summary
windows, the import/export volume caps, the AI-analysis switch, processor disclosure and policy,
the whole email transport, sender identity and per-recipient send throttle, the per-request caps
on contracts, photos and journal links, and the maximum upload size. Credentials — the SMTP login,
the AI-analysis key and similar — are entered at **System settings → Credentials** and encrypted at
rest.

Connection strings and a few deploy-time values stay in environment configuration; issue #421's
Non-Goals record why each one does. When a setting moves from configuration into the store, the
migration seeds the shipped default and an administrator sets the real value in the UI.

Some settings move in one direction only. The photo link and album caps and the upload size are
**tighten-only**, because a raised value would be overridden by a limit applied earlier in the
request (request-model validation, and the transport ceiling fixed at startup). Both publish that
ceiling to the settings page so the control bounds itself, and reject a larger value with a `400`
naming it. The other one-way settings, and the three upload ceilings, are in
[`deployment.md`](deployment.md).

## Email: confirmation and password reset

Registration uses ASP.NET Core Identity's email-confirmation flow: a new account stays unconfirmed,
and cannot sign in, until the user follows the link sent to their address. The same mail setup
delivers the self-service password-reset link (issue #405).

**Mail is configured entirely at runtime; there are no email environment variables.** An
administrator holding `system-settings.security.update` edits it at **System settings → Email**, and
each change applies to the **next send**, with no restart and no cache wait:

| Setting | Notes |
|---|---|
| SMTP host | The relay. Empty means mail is not configured: every send is logged and skipped. |
| SMTP port | `587` for STARTTLS submission, `465` for implicit TLS, `25` for an unauthenticated internal relay. |
| Use STARTTLS | On for 587; off for implicit TLS on 465. |
| Client base URL | The public origin every confirmation and reset link is composed against. |
| From address / From name | The envelope sender; must be an address the relay is authorised to send as. |
| Messages per recipient / window / tracked addresses | The anti-mailbomb throttle on the anonymous mail path. |
| Require email confirmation | Off lets users sign in immediately after registering. **On by default.** |
| SMTP username / SMTP password | Entered at **System settings → Credentials**, encrypted at rest. |

**Changing the SMTP host or port, or turning STARTTLS off, clears the stored SMTP username and
password** in the same transaction. You are asked to confirm, and must re-enter the credential
afterwards — that is the control working, not a fault. Why, and what guards the client base URL
instead, is in [`deployment.md`](deployment.md) (*Mail is configured after first sign-in*).

**Until an SMTP host is set, no email is sent.** In Development and Testing the link is written to
the API log instead, which is enough for local testing; in any other environment only the failure
to send is logged, because a reset link is a working credential. On a fresh production deployment
mail therefore starts switched off — configure it right after the first sign-in, before the
one-time administrator password can be lost (see [`deployment.md`](deployment.md)).

Nothing proves mail arrives except sending some, so **send yourself a password reset after
configuring** — it is the only check that covers the whole path, relay credential included.

### Using a Gmail account

Gmail works with the settings above, but **not** with your normal account password — Google retired
password ("less secure app") access. Use an **App Password**:

1. Turn on **2-Step Verification** on the Google account; App Passwords are unavailable until it is
   on.
2. At **Google Account → Security → App passwords**, generate one (for example named "Odyssey") and
   copy the 16-character code.
3. At **System settings → Email**, set the SMTP host to `smtp.gmail.com`, the port to `587`,
   STARTTLS on, and the from address to your Gmail address. Then at **System settings →
   Credentials**, enter the Gmail address as the **SMTP username** and the App Password as the
   **SMTP password**.

Notes specific to Gmail:

- Gmail forces the `From` address to match the authenticated account, so mail comes from your Gmail
  address whatever from address you set.
- A personal account is limited to roughly 500 recipients a day and may throttle bursts — fine for
  development and low volume; use a transactional mail provider for anything more.
- The App Password grants access to the mailbox. Revoke it from the same Google page if it leaks.

## Running the tests

```bash
dotnet test Odyssey.sln                  # everything; Docker and browser tiers self-skip without their prerequisites
dotnet test Odyssey.IntegrationTests     # one tier, e.g. real MariaDB via Testcontainers (needs Docker)
```

The tiers and what each needs are listed in [`CONTRIBUTING.md`](../CONTRIBUTING.md) and, with the
skip rules and the perf tier, in [`CLAUDE.md`](../CLAUDE.md) (*Testing & Demo Data*); the full
strategy is [`test-environment-and-e2e-spec.md`](test-environment-and-e2e-spec.md). The browser
suite has its own [README](../Odyssey.E2ETests/README.md) covering how to point it at a running
stack (`E2E_BASE_URL`) or have it manage Compose for you (`E2E_MANAGE_STACK=true`).
