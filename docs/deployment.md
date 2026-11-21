# Deploying Odyssey to a single VPS

This describes the supported production deployment: a **single Linux VPS running Docker**,
fronted by **Caddy** for automatic TLS, deploying **pinned SemVer image tags manually**.

There are **two** of them, and they share one overlay (`docker-compose.prod.yml`) and one
`Caddyfile` — they differ only in the env file you point at:

| | Reachable from | Env file | Certificate |
|---|---|---|---|
| **Public** | the internet | `.env.prod` | Let's Encrypt, for your domain |
| **Private** | that machine only | `.env.localhost` | Caddy's internal CA, for `odyssey.localhost` |

Read this document straight through for the public deployment; everything in it applies to the
private one too, with the two-variable diff and the reasoning in
[Running Odyssey privately](#running-odyssey-privately-localhost-only). Neither is the development
stack — that is a bare `docker compose up` on the base file, with demo data and published
credentials.

The app runs as five containers behind one origin:

```
            ┌──────────────────────────── VPS ────────────────────────────┐
 Internet ──┤  Caddy :443 (TLS) → client nginx :80 ─/api/→ api :8080       │
   (443)    │                                          │                   │
            │                                       mariadb :3306 (internal)│
            │                          migrations (runs once, then exits)  │
            │                          worker (background jobs, no port)   │
            └──────────────────────────────────────────────────────────────┘
```

Because the client's nginx proxies `/api/*` to the API, the browser only ever talks to one
origin (`https://your-domain`). That keeps cookie auth working without CORS configuration.

Images are published to GHCR automatically on every release (`…-api`, `…-client`,
`…-migrations`, `…-worker`), tagged with an immutable SemVer. Production **pulls** these — the server
never builds.

This is the reference for how a deployment works **now**. What changed in a particular release, and what to
do when you upgrade across it, is in **[Upgrade notes](upgrade-notes.md)**; read every note between the
version you run and the one you deploy before you set `IMAGE_TAG`.

## Contents

- [Files involved](#files-involved)
- [One-time server setup](#one-time-server-setup)
- [Deploying / upgrading](#deploying--upgrading)
- [Running Odyssey privately (localhost only)](#running-odyssey-privately-localhost-only)
- [First-run notes](#first-run-notes)
- [Client IP: two proxy hops](#client-ip-two-proxy-hops)
- [Upload size: three ceilings, in order](#upload-size-three-ceilings-in-order)
- [Tunable policy lives in System Settings, not in configuration](#tunable-policy-lives-in-system-settings-not-in-configuration)
  — [credentials](#credentials-live-in-the-encrypted-secret-store),
  [mail](#mail-is-configured-after-first-sign-in), [file analysis](#file-analysis)
- [Backups](#backups) — [the Data Protection keys volume](#the-data-protection-keys-volume),
  [two containers hold the key ring](#two-containers-hold-the-key-ring)
- [`ASPNETCORE_ENVIRONMENT`: use `Production`, never `Testing`](#aspnetcore_environment-use-production-never-testing)
- [Rollback](#rollback)
- [Who can read contact personal data](#who-can-read-contact-personal-data)
- [User profile pictures](#user-profile-pictures)
- [What leaves the deployment](#what-leaves-the-deployment)
- [Household locations: what leaves the deployment, and how to erase one](#household-locations-what-leaves-the-deployment-and-how-to-erase-one)
- [Location map: what leaves the deployment, and how to switch it off](#location-map-what-leaves-the-deployment-and-how-to-switch-it-off)
- [Weather: what leaves the deployment, and how to erase it](#weather-what-leaves-the-deployment-and-how-to-erase-it)
- [Exchange-rate sync: what leaves the deployment](#exchange-rate-sync-what-leaves-the-deployment)
- [File storage (filesystem volumes)](#file-storage-filesystem-volumes)
- [Background jobs: the run log](#background-jobs-the-run-log)
- [Photo thumbnails](#photo-thumbnails)
- [Photo originals](#photo-originals)
- [Files on the object store](#files-on-the-object-store)
- [News reader: feeds, what leaves the deployment, and the worker's network](#news-reader-feeds-what-leaves-the-deployment-and-the-workers-network)
- [Security checklist](#security-checklist)

## Files involved

| File | Purpose |
|---|---|
| `docker-compose.yml` | Base service definitions (shared with dev). |
| `docker-compose.prod.yml` | Production overlay: pull images, Production env, Caddy, no host ports, persisted keys. Serves **both** deployments below. |
| `Caddyfile` | TLS termination + reverse proxy to the client. |
| `.env.prod.example` | Template for the internet-facing deployment. Copy to `.env.prod`. |
| `.env.localhost.example` | Template for a private, machine-local deployment. Copy to `.env.localhost`. See [Running Odyssey privately](#running-odyssey-privately-localhost-only). |

## One-time server setup

1. **Provision a VPS** (Debian/Ubuntu recommended) and install Docker Engine + the Compose
   plugin. Open inbound TCP **80** and **443** in the firewall.

2. **Point DNS** — an `A`/`AAAA` record for your domain (e.g. `odyssey.example.com`) at the
   server's public IP. Caddy needs this resolvable to issue a Let's Encrypt certificate.

3. **Get the deploy files onto the server.** Either clone the repo, or copy just
   `docker-compose.yml`, `docker-compose.prod.yml`, and `Caddyfile`. (The images carry the
   app itself; these three files are all the server needs.)

   > **If you ran a bare `docker compose up` on this machine first — to try Odyssey out — destroy
   > that stack's data before deploying for real:**
   >
   > ```bash
   > docker compose down -v          # base file only: removes the dev containers AND the volume
   > ```
   >
   > The base `docker-compose.yml` is the **development** stack. It defaults to
   > `ASPNETCORE_ENVIRONMENT=Development` with `SEED_DEMO_DATA=true`, so its `mariadb_data`
   > volume holds the demo dataset — including an Administrator whose password is published in
   > this repository's README. It also **publishes MariaDB on host port 3307**, along with the
   > API on 5188 and the client on 5199 — all three bound to `127.0.0.1` only, so they are not
   > reachable from the network. Keep those prefixes; removing one to "test from another
   > machine" puts that Administrator account on a public IP.
   >
   > The volume is the part `down` alone does not fix. The production overlay resets all three
   > port mappings (`ports: !reset []`), but it reuses the same `mariadb_data` volume name — so
   > bringing the overlay up over a volume the dev stack initialised carries those accounts, and
   > that database password, straight into production. `down -v` is the only thing that removes
   > it.

   > **On an SELinux host (Fedora, RHEL, CentOS Stream, Rocky, Alma), the two bind mounts
   > relabel the files they mount.** Both carry the `z` option — `./Caddyfile` and
   > `./docker/mariadb/init` — because a freshly checked-out file keeps the label of the directory
   > you cloned into (typically `user_home_t`), which a container process is not permitted to read.
   > Without it Caddy crash-loops on `open /etc/caddy/Caddyfile: permission denied`, and because
   > the runtime drops the AVC denial silently, the permission bits look fine and nothing in any
   > log names SELinux.
   >
   > `z` applies the **shared** `container_file_t` label (not the per-container `Z`), and it does so
   > **in place, permanently** — after the first `up`, `ls -Z Caddyfile` on your deploy checkout
   > reports `system_u:object_r:container_file_t:s0` rather than what git left there. That is the
   > mechanism working, not damage, but it does mutate the working tree, so expect it if you deploy
   > from a clone you also use for other things. On hosts without SELinux the runtime ignores the
   > option entirely, so there is nothing to configure either way.

4. **Create `.env.prod`** from the template and fill in real values:
   ```bash
   cp .env.prod.example .env.prod
   # edit .env.prod: GHCR_OWNER, IMAGE_TAG, ODYSSEY_DOMAIN, DB passwords,
   #                 BOOTSTRAP_ADMIN_EMAIL, BOOTSTRAP_ADMIN_PASSWORD
   ```
   - Generate strong DB passwords (e.g. `openssl rand -base64 24`).
   - `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` create the initial administrator on a
     fresh database and are **required** there — see [First-run notes](#first-run-notes).
   - **Nothing about mail goes in this file** (issue #8). The relay host, port and STARTTLS mode and
     the public link origin joined the sender identity and the relay credential in runtime settings;
     all of it is entered at **`/settings` → Email** after your first sign-in. The API no longer
     refuses to start without a relay, so **a fresh deployment starts with mail switched off** —
     read [Mail is configured after first sign-in](#mail-is-configured-after-first-sign-in) before
     you deploy, because that window has one consequence you cannot recover from yourself.
   - **No credential goes in this file.** The relay username and password, the Claude API key and
     the two derivation keys were removed from configuration entirely in issue #445 — the
     properties no longer exist, so a value set here is not read and not fallen back to. They are
     entered once by an administrator at **`/settings` → Credentials** after first start; until
     then each behaves as documented in
     [Credentials live in the encrypted secret store](#credentials-live-in-the-encrypted-secret-store).
     `.env.example` marks each retired variable in place.

5. **If the GHCR packages are private**, log Docker into GHCR once with a PAT that has
   `read:packages`:
   ```bash
   echo <PAT> | docker login ghcr.io -u <your-github-user> --password-stdin
   ```

## Deploying / upgrading

A deploy is always: set `IMAGE_TAG`, pull, up. Migrations run automatically (the API waits
for `migrations` to complete successfully before starting).

```bash
# 1. Pick the release to deploy — edit IMAGE_TAG in .env.prod (e.g. 1.4.0).

# 2. Pull the pinned images.
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.prod pull

# 3. Apply. Recreates only changed containers; migrations run before the API comes up.
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.prod up -d

# 4. Verify.
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.prod ps
curl -s https://your-domain/api/healthz   # → {"status":"ok","version":"1.4.0"}
```

`/api/healthz` returns the running version — confirm it matches the `IMAGE_TAG` you deployed.

> **Tip:** wrap steps 2–4 in a small `deploy.sh IMAGE_TAG` on the server so a release is a
> single command.

**Before an upgrade**, read [Upgrade notes](upgrade-notes.md) for every release between the running
version and the target, and take a database dump (and, if the release touches stored files, a copy of
each file volume — see [Backups](#backups)). Two rules hold for every release:

- **Schema migrations are forward-only.** Rolling the images back does not roll the database back; see
  [Rollback](#rollback).
- **A data migration is undone by restoring the backup, nothing else.** `Down()` restores schema at
  most, never rows. An upgrade note says when a release carries one, and when it needs `api` and `worker`
  stopped while the migrations job runs.

### New or revoked permission claims need no sign-out

`RoleClaimSeeder` (in the migrations job) reconciles role claims on every deploy, additions and removals
alike. A change reaches a live session **within one minute**: the security stamp is revalidated every
minute (`SecurityStampValidatorOptions.ValidationInterval`, `Odyssey.Api/Program.cs`), and revalidation
re-reads the role's claims into the cookie. The browser shows the new claims on its next page load. No
user needs to sign out and back in; every [upgrade note](upgrade-notes.md) that introduces a claim relies on this.

## Running Odyssey privately (localhost only)

The second supported deployment is a **private one on a single machine** — reachable from that
machine and nowhere else. It uses the **same `docker-compose.prod.yml`**, with a different env
file:

```bash
cp .env.localhost.example .env.localhost
# edit: GHCR_OWNER, IMAGE_TAG, DB passwords, BOOTSTRAP_ADMIN_EMAIL, BOOTSTRAP_ADMIN_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.localhost pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.localhost up -d
```

Then open **`https://odyssey.localhost`**.

**Do not confuse this with the dev stack.** A bare `docker compose up` on the base file is the
*development* stack: Development environment, demo data, four seeded accounts sharing a password
published in this repository's README. That is the thing to run if you want a dataset to click
around in. This deployment is the real application with no demo data and no default accounts,
which is why it still needs DB passwords and a bootstrap administrator.

### Only two values differ from `.env.prod`

| Variable | `.env.prod` | `.env.localhost` |
|---|---|---|
| `CADDY_BIND` | unset → `0.0.0.0:80`, `0.0.0.0:443` | `127.0.0.1:` → loopback only |
| `ODYSSEY_DOMAIN` | your public domain (Let's Encrypt) | `odyssey.localhost` (Caddy's internal CA) |

Everything else is identical, deliberately. `Production`, the runtime hardening, the unpublished
`api`/`mariadb`/`client` ports, the persisted `dataprotection_keys` volume, the bootstrap
administrator, the mail-off first run and the whole `/settings` inventory all behave exactly as
described elsewhere in this document.

`CADDY_BIND`'s trailing colon is part of the value — Compose's port syntax is
`[HOST_IP:][HOST_PORT:]CONTAINER_PORT`, so the variable has to supply its own separator. Caddy is
the **only** host-facing surface in the overlay (every other service resets `ports` to `[]`), so
that one line is enough to take the deployment off the network. The converse also holds: publishing
`client` or `api` "just to check something" undoes it, and does so unsafely — the client's nginx
honours an inbound `X-Forwarded-Proto` (`Odyssey.Client/nginx/templates/default.conf.template`), which is only sound because
Caddy is the sole path in and overwrites it.

### Why keep Caddy at all, if nothing is exposed?

Because `ASPNETCORE_ENVIRONMENT=Production` sets `CookieSecurePolicy.Always`
(`Odyssey.Api/Program.cs`), so the auth cookie is marked `Secure` and needs HTTPS.

Serving plain HTTP would *appear* to work: browsers treat `localhost`, `127.0.0.1/8` and `::1` as
secure contexts and accept `Secure` cookies there. But that exemption is keyed to the **host in the
address bar**, so it stops applying the moment anyone reaches the box as `http://192.168.1.50` —
at which point the browser silently discards the cookie and **login fails with no error message
anywhere**. Terminating TLS removes that failure mode instead of depending on it, and keeps the
private deployment a faithful rehearsal of the public one.

The alternative — running `Development` locally to dodge the cookie flag — is the wrong trade and
worth naming explicitly: it also turns on Swagger, permits demo seeding, and substitutes
`LegalOptions.DevelopmentPseudonymizationSecret` for the real pseudonymisation key
(`Odyssey.Api/Legal/LegalPseudonymizer.cs`). Use `Production` on both deployments.

### Use `odyssey.localhost`, never bare `localhost`

The `Caddyfile` sends `Strict-Transport-Security: max-age=31536000; includeSubDomains`, and
**browsers key HSTS by hostname and ignore the port.** Setting `ODYSSEY_DOMAIN=localhost` would
therefore pin *every* `http://localhost:<port>` URL in that browser to HTTPS for a year — breaking
the dev stack on :5199 and Swagger on :5188, recoverable only via `chrome://net-internals/#hsts`.

Scoped to `odyssey.localhost` the pin is harmless, browsers resolve `*.localhost` to loopback with
no hosts-file entry, and Caddy recognises the name as non-public and issues from its internal CA
rather than attempting an ACME challenge it could never satisfy. This is why a private deployment
needs **no change to the `Caddyfile`**.

### Trust Caddy's local CA

Otherwise every visit shows a certificate warning:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.localhost \
  cp caddy:/data/caddy/pki/authorities/local/root.crt /tmp/caddy-root.crt

sudo trust anchor /tmp/caddy-root.crt          # Fedora / RHEL
# sudo cp /tmp/caddy-root.crt /usr/local/share/ca-certificates/caddy-root.crt \
#   && sudo update-ca-certificates              # Debian / Ubuntu
```

Firefox uses its own trust store — either import the same file under
*Settings → Privacy & Security → Certificates → View Certificates → Authorities*, or set
`security.enterprise_roots.enabled` to `true` in `about:config`.

The CA is generated on Caddy's first start and lives in the `caddy_data` volume. `docker compose
down -v` destroys it, and the next start generates a different one — so re-run the steps above if
certificates start being rejected after a volume wipe.

### What is *not* relaxed

- **Backups still matter.** The `dataprotection_keys` volume is secret-bearing and is destroyed by
  `docker compose down -v`, permanently for anything derived from it. See [Backups](#backups) —
  "it's only local" is how the legal pseudonymisation salt gets lost.
- **The mail-off first run is sharper here, not milder.** If `BOOTSTRAP_ADMIN_PASSWORD` is lost
  before SMTP is configured, the forgot-password flow is the very thing that cannot run, and a
  single-user install means you are the one locked out. See
  [Mail is configured after first sign-in](#mail-is-configured-after-first-sign-in). If you do
  configure mail, set the client base URL to `https://odyssey.localhost`.
- **File analysis still sends documents to Anthropic** if enabled at `/settings`. Being on loopback
  does not make that local. It ships off.

## First-run notes

- **`BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` are mandatory on a fresh database.**
  The initial administrator is created by the `migrations` container from those values — registration
  order grants nobody any privilege. If they are unset (or only one is set), the migrations job exits
  non-zero and the API never comes up:

  ```
  No enabled administrator exists after seeding. Set Bootstrap:Admin:Email and
  Bootstrap:Admin:Password (BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD) on a fresh
  database and redeploy.
  ```

- The password must satisfy the app's policy (16+ characters, upper + lower + digit + symbol) and is
  a **one-time secret**: the account is created with `MustChangePassword` set, so the first sign-in
  lands on `/change-password-required` and the API refuses everything else until a new password is
  set. Then come `/accept-terms` and `/onboarding`, as for any new account.
- Seeding fires **only when the user table is completely empty**. On every later deploy the values are
  read and ignored, so redeploying can never revert a password changed in the app. It is therefore not
  a password-recovery mechanism — the seeded account is created with a confirmed email, so the
  self-service forgot-password flow covers a lost one-time password (SMTP must be configured).
- Database schema is created/updated by the `migrations` container on every deploy — there is
  no manual migration step.
- **Do not interrupt the `migrations` container while it is applying a migration.** MariaDB commits
  DDL implicitly, so a migration is not atomic: killing the job part-way leaves the tables it had
  already created behind with no `__EFMigrationsHistory` row, and every later deploy then fails on the
  same migration with the API held down behind it. The job detects this and stops with a message
  naming the repair rather than a bare `Table 'X' already exists` — the procedure is in
  [`migration-history-drift.md`](migration-history-drift.md). A migration in flight is given up to
  5 minutes to finish before a shutdown signal takes effect — `stop_grace_period: 5m` on the service
  plus a matching `HostOptions.ShutdownTimeout`, which is why `docker compose down` can pause during a
  deploy.

## Client IP: two proxy hops

The API sees the real client address only because it is told how many proxies sit in front of it.
In this overlay there are **two** — Caddy sets `X-Forwarded-For` to the client, the client container's
nginx appends Caddy's address — so the API receives `<client>, <caddy>` and
`ForwardedHeaders:ForwardLimit` defaults to `2` (issue #244). That address is what the sign-in and
password-reset rate limits partition on and what audit lines record. With a limit of 1 every request
would appear to come from Caddy: one shared rate-limit bucket that a single caller could exhaust for
everyone.

Change it only if the topology changes: add one per extra proxy (a load balancer in front of Caddy),
or use `1` if nginx is reached directly. Neither compose file passes the key through, so set it by
adding `ForwardedHeaders__ForwardLimit` to the `api` service's `environment:` map in
`docker-compose.prod.yml` (the same applies to `ForwardedHeaders__KnownNetworks__0`, `__1`, …). It
must be a whole number of at least 1; anything else fails startup.

Entries are honoured only while the hop that added them is inside `ForwardedHeaders:KnownNetworks`
(private ranges by default). In production a public client cannot get a spoofed entry past Caddy,
which overwrites an inbound `X-Forwarded-For`. The **dev** compose stack is the exception and is
accepted: nginx is published directly on `127.0.0.1:5199`, Docker's NAT makes the peer a private
gateway address, and so a local caller's own `X-Forwarded-For` entry is honoured. That is reachable
from loopback only, and one spoofable entry was already honoured there before the limit was raised.

## Upload size: three ceilings, in order

The maximum upload is enforced at three layers, and **raising it means raising all three, outermost
first**. Raising only the inner one produces a `413` from a component that never sees the app's
setting; raising only the outer ones changes nothing.

| Layer | Where | Must be |
|---|---|---|
| Reverse proxy | `Caddyfile` (`request_body max_size`) and `Odyssey.Client/nginx/templates/default.conf.template` (`client_max_body_size`) | ≥ transport ceiling + multipart envelope headroom |
| Transport ceiling | `FILE_UPLOAD_MAX_BYTES` → `FileStorage:MaxFileSizeBytes` (Kestrel + multipart limits, fixed at **startup**) | ≥ the admin-set cap |
| The cap users hit | **System Settings → Storage**, in the database | whatever policy wants, up to the ceiling |

Only the third is runtime-editable. The other two are deploy-time by necessity: Kestrel's request-body
limit is set once at startup and cannot be raised per request, and the proxy limits are static config —
which is exactly why the setting is **tighten-only**. `PUT /api/system-settings` rejects a value above
the transport ceiling with a `400` naming it, rather than accepting a number the transport would then
silently refuse.

The large-file routes — `/api/api/tasks/ics…`, the journal `…/vjournal` routes (`/api/api/journal-entries/vjournal…`
and `/api/api/journal-entries/{id}/vjournal…`), the calendar `…/ics` routes and their `/summary` routes
(`/api/api/calendars/{id}/ics…`, `/api/api/calendar-events/ics…`, `/api/api/calendar-events/{id}/ics…` and
`/api/api/recurrence-patterns/{id}/ics…`), `/api/api/photos…` and `/api/api/files…` as the browser
sends them — sit at `10241` MiB in both proxies (nginx `10241m` in its own location block, Caddy `10241MiB`
behind a path matcher; Caddy reads `MB` as 10^6 bytes) for a `10737418240`-byte (10 GB) ceiling plus 1 MiB of
envelope headroom. Those routes also stream request bodies through and allow an hour for a transfer, since a
multi-gigabyte upload or download outlives the 60 s defaults. **Every other route stays at 65 MiB with the
default timeouts**, sign-in included. The ceiling was raised from 64 MB so a task or journal export can carry
its attached files (and a journal's photos) inline and come back in through the matching import, and a calendar
export or import can stream a large calendar (issue #516); the admin upload cap still seeds at 64 MB, and it also
bounds a file a task import stores new. Lowering a cap needs no deploy-time change at all.

The `.ics` routes (task, journal and calendar) sit in their own nginx location with **`proxy_buffering off`**. The
API's export progress guard measures the bytes it finishes writing; with nginx buffering the response, a slow client
would look fast to the API and park up to `proxy_max_temp_file_size` (1 GB by default) per export on nginx's temp
disk instead. Caddy streams responses without buffering them, so it needs nothing equivalent. The photo and Files
routes keep nginx's default response buffering.

#### Export progress guard

Every bulk export (task, journal and calendar `.ics`, the vCard export and their summaries) holds one of 4 global
export permits, at most 2 per user, for as long as it streams. Since issue #516 a permit is no longer released by a
fixed 10-minute deadline but by a **progress guard**: the connection is aborted when no first byte is written within
10 minutes (planning), when a 5-minute window ends with a write waiting on the client and under 1 MiB written in it
(a floor of about 3.4 KiB/s; a response that finishes inside its window is never judged), or after 24 hours in
total. Each abort logs one warning naming the rule. Two accounts reading at the floor can still hold all four permits
for up to a day; that is an accepted residual for a mostly single-user instance.

#### Calendar `.ics` import and export

The calendar surface ships with **no event-count limit and 10240 MB** for both sizes (issue #516). Both size rows warn above 1024 MB, and so on the shipped default: the
import row because a calendar import buffers its **whole upload once** to `ASPNETCORE_TEMP` (the `import_spool` volume) before it streams it — budget that cap ×
the import concurrency limit (2) of free space there; the export row because an export that large streams for hours
while it holds an export permit. Memory does not grow with the file: the import reads one
`VEVENT` at a time (an unfolded line is at most 1 Mi characters, a `VEVENT` at most 4 Mi, the file's `VTIMEZONE`
blocks at most 1 MB in total; anything over is a `400`), reduces each to its bounded fields as it arrives, and commits
every **500 events, or fewer once their text reaches 16 Mi characters, in their own transaction**. The
`VTIMEZONE` blocks are kept once and never re-parsed per event. Only generated series occurrences count against
`CalendarIcsMaxAggregateOccurrences`, across the whole import; standalone events never do.
An import that fails part-way therefore leaves its earlier batches applied and answers with code
`calendar_ics_import_partially_applied` and the committed counts; importing the same file again is idempotent and
completes it. The API sends nothing until an import completes, so one import must finish inside the proxies'
hour-long read timeout — at the reference rate of about 3,000 events/s, roughly 10 million events; split larger
calendars. A client that disconnects stops the import at the next batch boundary.

An export over its byte cap is **aborted mid-stream** (the download fails) instead of being truncated, on every
calendar export. `X-Odyssey-Export-Rows` is the **planned** event count (#516 Non-Goal 7): a row deleted while a
long export streams is skipped, so the body then holds fewer `VEVENT`s than the header and the client treats the
download as incomplete. That is deliberate — the client cannot tell a concurrent delete from a cut-off transfer —
and re-running the export gives a consistent file. Cancelled events in an imported file are removed only when the importer asks for it
(`allowRemovals`, which needs `calendar.delete`); each batch that removes anything logs the acting user, the calendar
and the removed ids. `ImportSourceUid` is the key that makes a second import of another calendar's export update
rather than duplicate.

#### Calendar attachments (issue #521)

Events and series link files (stored in the **Journal** area) and library photos, and the calendar, event and series
`.ics` exports carry them inline — each distinct piece once, then by reference.

- A new setting, **`CalendarEventMaxLinksPerKind`** (default 50, range 1–100), at `/settings` and on
  `GET /api/import-limits`. Its maximum bounds the series-delete link copy at 200,000 rows.
- **`attachments=omit`** on the three exports and their summaries writes no `ATTACH` and needs neither `files.read` nor
  `photos.read`. An export that includes them carries **photo originals with their EXIF/GPS** and file and photo ids and
  hashes — that is the opt-out.
- The **aggregate** export (`/api/calendar-events/ics`) carries no attachments, and re-importing it keeps every link: a
  kind's links are replaced only when the file carries at least one resolvable attachment of that kind.
- An import decodes **one attachment at a time** to `ASPNETCORE_TEMP` (each under the admin upload cap, with 1.25× that
  free or a `507`), and stages new content on the store as `Pending` reservations: a batch closes once it holds 256 MiB,
  so it never holds more than 256 MiB plus one attachment. One event that alone carries the batch past 256 MiB has its
  further new content skipped and counted, since an event is never split across batches. A batch that fails leaves only
  its reservations, which the abandoned-write sweep removes within an hour.
- An attachment reference must carry its id, `X-ODYSSEY-SHA256` and `SIZE` together; a bare `odyssey-file:<id>` is
  skipped as malformed.

#### Calendar recurrence keys and time zones (issue #519)

**The import sorts the file by UID first, on temporary disk.** Since issue #519 a calendar import reads the buffered
upload twice: pass 1 checks the whole file and writes a 40-byte sort record per `VEVENT` to `ASPNETCORE_TEMP` (the
same `import_spool` volume), pass 2 merges the sorted runs and applies one UID at a time. The disk this needs is
bounded, not estimated:

- A file with more than `max(1, upload bytes / 64)` top-level `VEVENT`s is refused with
  `400 calendar_ics_import_too_many_components` before anything is applied — no real calendar averages under 64 bytes
  an event — so the records are at most **0.625×** the upload.
- Runs are deleted after each merge round, so the spill peaks at **1.25×** the upload (on top of the buffered upload
  itself). Before pass 1 the import reserves **1.5×** the upload against the free space, in a process-wide budget that
  concurrent imports share; crossing 1.25× while sorting, or `ENOSPC`, is a `507` with nothing applied. Budget
  `cap × 2.5 × import concurrency (2)` of free space on the volume for a worst case: the 1× buffer plus the 1.5×
  reservation. The spill is deleted when the request ends, whatever happened; a process that crashed mid-import leaves
  `odyssey-ics-*` files behind, and the next import sweeps any older than a day. A volume whose free space cannot be
  read is refused with the `507` rather than assumed to have room. The spill, the upload copy and the attachment spool
  of the task and journal imports all resolve `ASPNETCORE_TEMP` the same way, falling back to the OS temp directory
  only when it is unset or missing — which in the production image is a RAM-backed tmpfs, so keep it set.

Because pass 1 reads the whole file before any batch, a structural fault anywhere in a file (an unclosed or oversized
component, too many events) is now a plain `400` with nothing applied. Batches still commit separately, but in UID-hash
order, so a failure part-way leaves an arbitrary subset applied; re-running the same file is still idempotent.

**Series writes lock the series row.** Every write that touches a series' rows — a series edit, an occurrence edit or
delete, an import of that series, a series delete, the backfill — runs in one transaction whose first statement is
`SET STATEMENT innodb_lock_wait_timeout = 5 FOR SELECT … FOR UPDATE` on the series, in ascending id order. Interactive
writes retry twice (about 15 s worst case) and then answer `409 calendar-series-busy`. Every host now retries MariaDB
errors `1205` (lock wait) and `1213` (deadlock) through one shared configuration (`OdysseyDatabaseRetry`), which also
makes any plain save on the API, the worker and the migration job retry a deadlock.

**The key backfill runs in the worker.** `RecurrenceKeyBackfillJob` (`/background-jobs`, "Recurrence keys") keys every
series written before #519, one locked series at a time: a row whose start equals an occurrence is keyed (a future one
that differs from the series becomes an override), a duplicate or rescheduled row is **detached** into a standalone
event with its date and fields unchanged — it is no longer deleted with its series — and a future occurrence with no
row is recorded as deleted. What calendars show does not change; series membership does. A series that fails is logged
by id and retried on the next tick; until it is keyed, its first write keys it inline.

Raising the ceiling does **not** widen the API's other endpoints either: the API holds each request that does not
accept files (JSON writes, sign-in) to `FileStorage:DefaultMaxRequestBodyBytes` (65 MB), and the photo upload to
the admin upload cap plus 1 MiB. Only the import and upload endpoints, which set their own per-request cap, reach
further.

A task import is spooled twice while it is processed — once by the multipart reader, once as the decoded
attachments — so a 10 GB import needs roughly 20 GB of free space for it. A photo upload is spooled once by the
multipart reader. All of it goes to `ASPNETCORE_TEMP`, which the API image sets to `/var/odyssey/import-spool`,
the `import_spool` named volume in `docker-compose.yml` — not `/tmp`, which the production file makes a RAM-backed
tmpfs. The volume is scratch: nothing in it outlives a request (a decoded attachment a crashed process left behind
is swept by the next task import after a day), and it needs no backup. On an existing deployment Docker creates it
app-owned from the image on first start.

> `FileStorage:MaxFileSizeBytes` is **not** retired — it stays as the transport ceiling above, read by
> the API at startup. The admin-editable cap is a separate value that seeds at 64 MB, so if you raise
> the ceiling here, raise the cap at `/settings` too or users still hit the old number.

### A fourth, unrelated size limit: photo metadata reads

`System Settings → Photos → Metadata read size` is **not** part of the chain above — it bounds how much
of an accepted upload's stream is read to extract EXIF/IPTC/XMP before the photo is stored (issue #428;
it used to be read back out of the database), not what an upload may contain. It stops at a compiled
16 MB.

Extraction materialises a full byte array of this size per photo, so it is a per-upload memory
multiplier; the row carries an advisory above its shipped 8 MB saying so. Extraction is best-effort: a
failed read or a timeout skips it, and the photo is still stored, just without extracted metadata.

## Tunable policy lives in System Settings, not in configuration

A growing set of runtime policy is admin-editable at **`/settings`** and
backed by the database, so changing it needs no redeploy and no restart. That includes every remaining
product-visible limit that *can* take effect at runtime: the file-analysis token and vocabulary caps,
photo metadata read size and timeout, calendar window and event-duration bounds, the aggregate ICS
export/import guards, the recurrence occurrence cap, the vCard repeated-field cap, import summary
sample counts, the mail throttle's tracked-address table, and the per-account smart-tag cap.

**Three of those move in one direction only**, and the field says so:

| Setting | Direction | Why the other way is unavailable |
|---|---|---|
| Maximum generated occurrences | lower only | One calendar row is written per occurrence, so raising it is a write multiplier available to every user who can create a calendar entry — and the cost survives lowering it back. |
| Max repeated fields per contact | lower only | Each field costs a sibling query and its own save, multiplied by an import entry cap that ships unlimited. Any number above the shipped 200 would be a guess about a product of three unbounded terms. |
| Tracked recipient addresses | raise only | The per-recipient mail throttle fails **open** once its table is full, so a smaller table weakens the control instead of tightening it. |

### Credentials live in the encrypted secret store

No credential is read from configuration or the environment. The SMTP username and password, the
file-analysis API key, the Frost client id (weather), the recipient hash key and the legal pseudonymisation
secret live encrypted in the `SystemSettingSecrets` table and are entered at **`/settings` → Credentials**
by an administrator holding `system-settings.security.update`. Each write is audited, unconditionally;
no value is ever shown again. Nothing carries a value in from the environment, and nothing should: that
would need the plaintext to stay there. The keys are `SecretSettingKeys` (`Odyssey.Dtos/Application/`).

A fresh deployment starts with none of them set, and each consumer behaves as follows until one is entered:

| Credential | While it is unset | Kind |
|---|---|---|
| File-analysis API key | Every analysis job fails before sending and is recorded as a credential failure. Nothing is transferred. A gateway that authenticates by network position still needs *a* value stored. | rotatable |
| SMTP username + password | With *both* unset the relay is used unauthenticated; with only one stored, or either unreadable, the send is logged and skipped rather than attempted with half a credential. | rotatable |
| Frost client id | Weather observations are not fetched; past days show the forecast only. | rotatable |
| Recipient hash key | Healthy: the send throttle generates a per-process key, so its recipient digests correlate within one process and not across a restart. | derivation |
| Legal pseudonymisation secret | Account deletion cannot pseudonymise consent records. Outside Production a fixed development value is substituted; in Production the deletion fails inside its own transaction, leaving the acceptance rows intact. | derivation |

- **A derivation key cannot be re-issued.** If consent rows were already pseudonymised under the legal
  secret, a different value leaves those rows permanently un-re-derivable — the property GDPR Art. 7(1)
  consent attribution depends on. Losing the key ring loses it the same way.
- **Nothing at startup requires a credential.** A value entered through the UI cannot be a precondition
  for the UI coming up, so a missing one fails at its first use, with the remedy in the message.
- **An unreadable row never falls back to anything.** It means the Data Protection key ring changed or
  was lost: restore it, or clear the row and enter the credential again. `/settings` shows such a row in
  coral, and the page header raises a **Credentials** signal naming what is broken.
- **The keys volume is load-bearing for these credentials as well as for sessions**, so the database
  backup alone is not enough — see [The Data Protection keys volume](#the-data-protection-keys-volume).

### Mail is configured after first sign-in

The SMTP host, port and STARTTLS mode and the public link origin (`Email:ClientBaseUrl`) are settings
(issue #8), edited at **`/settings` → Email**, gated on `system-settings.security.update`, and every change
binds on the **next send** — no restart, no cache wait. Nothing reads `Email:` from configuration or the
environment, so a value set there is not a fallback.

**Nothing validates mail at startup.** A value entered through the settings UI cannot be a precondition
for that UI coming up, so a missing relay surfaces at the first send, which logs and skips — the same
trade the credentials above make.

The consequence, and it is the part worth planning around:

> A new deployment sends **no mail at all** until an administrator sets an SMTP host. You sign in with
> the one-time `BOOTSTRAP_ADMIN_PASSWORD` and then configure SMTP. **If that password is lost during
> that window there is no self-service recovery** — the forgot-password flow needs working mail — and
> the account must be repaired directly in the database.

So configure mail immediately after your first sign-in, before blanking
`BOOTSTRAP_ADMIN_PASSWORD`, and then send yourself a password reset. Nothing short of a real send
checks the whole path, relay credential included.

While no host is set, the System settings page reports **"Transactional mail is not configured"** in
its page header, at Information severity, with a Fix action jumping to the row. That covers an
administrator who is looking at the page; it does not cover an operator who deploys and never visits
`/settings`, which is why this section exists.

**Changing the SMTP host or port, or turning STARTTLS off, clears the stored SMTP username and password** —
in the same database transaction as the change itself, so the two land together or neither does. The
reason: the sender connects and *then* authenticates,
so whatever relay is set receives the stored credential, and a credential entered for an encrypted
transport must not be replayed over a cleartext one where passive network position alone is enough to
read it. The **port** counts as part of the relay for this purpose — a different port is a different
listener, which receives the credential just as readily as a different host would. Clearing it closes both structurally rather than by detection. The UI confirms before saving
and names what will be cleared; re-enter the credential at **`/settings` → Credentials** afterwards.
Until you do, mail is sent unauthenticated and any relay requiring a login will reject it.

`Email:ClientBaseUrl` carries no such structural control, because no credential is involved. Whoever
can change it receives a password-reset token for any address they know — **including another
administrator's**. Accounts with two-factor authentication stay protected by their second factor,
which makes 2FA enrolment a meaningful control for administrator accounts specifically. What guards
the rest is the security claim, an audit line recording old and new host, https-only validation (with
a loopback exemption so the dev stack keeps working over `http://localhost`), and a hint on the field
when the saved origin differs from the one you are browsing from. That residual is stated and
accepted; see issue #8 §10.2.

### File analysis

Statement analysis sends a user's uploaded statement to a third-party model provider (Anthropic by
default) and, for the match step, the household's contact and tag vocabulary. It is **off** until an
administrator holding `system-settings.security.update` turns it on at **`/settings` → File analysis**,
where the switch, the model and the base URL live (shipped defaults: off, `claude-sonnet-5`,
`https://api.anthropic.com`). Every change to those three is audited. Extraction runs in the **worker**
as a queued job; the match step runs in the **api**.

- **The switch binds on the next request.** It is read live on every file-analysis call, not from the
  30-second settings cache, so an operator told to stop transfers can stop them.
- **Repointing the base URL sends the stored API key to the new host.** Repoint it only to a host you
  control or trust. It must be an absolute `https://` address with **no path** (the provider appends
  `/v1/messages`), and no credentials, query or fragment. A stored value that breaks those rules, a restored
  one included, makes analysis refuse rather than fall back to `api.anthropic.com`.
- **Redirects are not followed.** A `3xx` from the configured host is recorded as a provider error;
  configure a gateway at its final address.
- **The circuit breaker is per typed client, not per host.** After a repoint, a breaker opened by the
  previous host stays open for the rest of its sampling window (`2 × FileAnalysis:TimeoutSeconds`, 240 s at
  the shipped 120 s). Wait it out, or restart the worker (and the API, for the match step).
- **`FileAnalysis:TimeoutSeconds` stays in configuration**, must be 10–140, and is validated at startup in
  both the API and the worker; keep the two equal (the shipped `appsettings.json` files are).
- **A statement above 32 MiB is refused with `422`** (`file_too_large_for_analysis`) before anything is
  queued.
- **Nothing is re-sent automatically.** A worker that dies mid-call leaves the job `Failed / interrupted`,
  reported to the user as "possibly sent"; a job no worker claims within 15 minutes fails as `not_started`,
  "not sent". The run log at `/background-jobs` has a **File analysis** entry with no *Run now*.
- **What the match step sends**: contact names and aliases, and tag names and descriptions — never alias
  labels, notes or organisation numbers. Each request is bounded at 200 000 characters: over it, aliases are
  left out for that run, then tag descriptions; names are never shed, so a household whose names alone are
  too large gets a `Failed` match. Optional administrator guidance (`FileAnalysisExtractionPromptAddition`,
  `FileAnalysisMatchPromptAddition`) is added to the prompts; a change is logged with the actor, length and
  digest, never the text, and turning either on or off makes users consent again.
- **Per-user limits** are deploy-time configuration: `RateLimiting:FileAnalysisAnalyze` (default 10 per
  hour) and `RateLimiting:FileAnalysisMatch` (default 30 per hour).

## Backups

Every named volume, and whether it needs a backup:

| Volume | Back up? | Why |
|---|---|---|
| `mariadb_data` | **Yes** | All application data except file bytes. |
| `files_primary` | **Yes**, after the dump | Every user file and photo (see below). |
| `dataprotection_keys` | **Yes**, separately | The key ring that decrypts stored credentials (see below). |
| `caddy_data` (prod) | Recommended | Caddy's TLS certificates and ACME account, and the local CA root a private deployment's clients trust; losing it means re-issuance, or re-trusting a new CA. |
| `caddy_config` (prod) | No | Caddy's autosaved runtime config, regenerated from `./Caddyfile` on start. |
| `import_spool` | No | The API's upload spool (`ASPNETCORE_TEMP`); scratch that outlives no request. |

- `mariadb_data` — all application data. A nightly logical dump is recommended:
  ```bash
  docker exec odyssey-mariadb sh -c \
    'exec mariadb-dump -uroot -p"$MARIADB_ROOT_PASSWORD" --all-databases' \
    > odyssey-$(date +%F).sql
  ```
- `dataprotection_keys` — **the Data Protection key ring, and it is secret-bearing.** It has always
  held the auth-cookie and antiforgery signing keys; since issue #444 it is also what decrypts any
  credential an administrator stores under **System settings → Credentials**. The `api` mounts it
  read-write and the `worker` read-only. See the section below.
- `files_primary` (Docker names it `odyssey_files_primary`) — the `primary` **file volume** (issue
  #346). **It holds every user file and photo**: the shipped compose files bind all six storage features
  to it (photo originals and thumbnails, finance, journal and contact-avatar files, and the news reader's
  fetched articles), user files under their original names. The database holds none of those bytes. Back it up
  **after** the database dump, within the deletion grace period — the order matters; see
  [File storage → Backup and restore](#backup-and-restore).

### The Data Protection keys volume

**Back it up, and back it up *separately* from the database dump.** Both directions of that sentence
are load-bearing, and each is a different failure:

- **Together is a disclosure risk.** The keys and the ciphertext they protect must not land in the
  same archive, or a single misdirected backup yields both halves — which is precisely the
  database-only exposure that encrypting the credentials was meant to bound.
- **Not at all is an unrecoverable loss.** `docker compose down -v` destroys this volume. For a
  rotatable credential (an SMTP password, a provider API key) that is an outage: re-issue it at the
  provider and paste it again. For a **derivation key** it is permanent — data already derived from it
  can never be re-derived.

Losing the volume without losing the database leaves stored credentials readable by nobody: the
Credentials rows report *"Set, but this server cannot decrypt it"*, and the features that use them
behave as if the credential were unset.

**Incident response.** If the volume, or any backup of it, is suspected disclosed, treat every stored
credential as compromised and **rotate it at the provider** — re-encrypting under a new key ring is
not sufficient, because the plaintext is what leaked. If a derivation key was among them, run a GDPR
Art. 33 breach assessment: disclosure of `Legal:PseudonymizationSecret` permits re-identification of
pseudonymised acceptance records (Art. 4(5)), and the same reasoning applies to
`Email:RecipientHashKey` (ISO 27001 A.5.24, A.5.26).

**What this does and does not protect against.** Encryption at rest here bounds **database-only**
exposure — a stolen dump, a misdirected backup, a read-only SQL injection, a support engineer with a
database client. It does **not** protect against an attacker who has the application host, because
that attacker has the key ring and the process memory. The access boundary is unchanged by this
feature; the prize behind it is larger.

### Two containers hold the key ring

Since issue #348 the **worker** mounts the volume too, **read-only**, because background jobs read the
encrypted secret store: the Frost client id (weather) and, since issue #444 moved statement analysis into the
worker, the file-analysis API key. This was a deliberate choice over the alternatives: keeping secret-reading jobs in the API, or an internal endpoint
through which the worker asks the API for one secret at a time.

What it changes and what it does not:

- **A compromise of either container exposes every stored credential.** The incident response above
  applies to the worker exactly as to the API: rotate every credential at its provider.
- **The worker never writes the ring.** The mount is `:ro`, and automatic key generation is turned off
  in the worker (`WorkerHost`), so the API remains the only process that creates, rotates or revokes a
  key. A worker started against an empty volume refuses to protect anything rather than inventing a key.
- **Both must use the same path and the same application name** (`Odyssey`). A worker without
  `DataProtection__KeysPath` logs a warning at startup, and every secret then reads as *cannot be
  decrypted*: weather observations stop and log an error hourly while forecasts continue, and statement analysis
  fails.
- **The migrations job still holds no key ring.** It protects nothing.

**The key ring itself is stored unencrypted, and the API says so on every boot.** Expect this line,
and do not treat it as a misconfiguration:

```
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {...} may be persisted to storage in unencrypted form.
```

It means the XML key files on the `dataprotection_keys` volume are not themselves encrypted, which is
correct for this deployment and follows from the paragraph above: the only things ASP.NET Core could
encrypt them *with* — DPAPI, or a certificate — are unavailable or circular here. DPAPI is
Windows-only. A certificate would have to be readable by the API container at startup, so it would
live in the environment or in a mounted file on the same host, which is where the attacker who has
the host already is; it would move the secret rather than protect it, and it would reintroduce
exactly the "credential in the environment" arrangement issue #445 removed. The boundary that does
the work is filesystem access to the volume — so back it up separately, keep it off shared storage,
and read the incident-response paragraph above.

Do not confuse it with Odyssey's own startup warning, which is a different and more serious
condition: `Data Protection has no explicitly configured durable key repository` means the ring is
**ephemeral** — nothing is being persisted at all, every restart logs everyone out, and writes to
encrypted secret settings are refused with `503`. That one needs fixing; the framework's
`No XML encryptor configured` does not.

**Only the `api` and the `worker` mount it** — the worker read-only, since issue #348 (see
[Two containers hold the key ring](#two-containers-hold-the-key-ring)) — and the short list is the
point: every container holding the ring is one more that can decrypt every stored credential. The `migrations` job mounted it too for a while, so
that a configuration-adoption step could protect a value under keys the API could read — a value
protected under the job's own ephemeral ring would have produced a row the API could never decrypt.
That step no longer exists and nothing in the job protects anything, so the mount was removed along
with it. The API creates the ring on first start.

If you ever add a mount back, add it together with the step that needs it, pointing at this same
volume and naming the same `DataProtectionKeyRing.ApplicationName` (`Odyssey.Core/SystemSettings/`), as the
API and the worker do — two services deriving different keys from the same files is the failure this
arrangement exists to avoid.

**The API's mount must be read-write.** (The worker's is read-only on purpose: it never creates a key.)
A read-only mount on the API does not fail loudly: Data Protection falls back
to an in-memory key ring with a log line, which is the same silently-ephemeral failure by another
route.

#### A keys volume the API cannot write needs a one-time `chown`

The API image creates `/var/odyssey/dataprotection-keys` owned by `app` at build time, and the API
**asserts at startup that the directory is writable**, failing to start if it is not. Docker copies
image directory ownership into a named volume **only when the volume is empty**, so a
`dataprotection_keys` volume created before the image owned that directory may still be `root:root 0755` — in which case
that installation has been running an ephemeral key ring all along (forcing a re-login on every
restart) and the API now stops instead of continuing silently. That is the intended posture once
credentials depend on the ring. The remediation is one line:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.prod \
  run --rm --user root api chown -R app:app /var/odyssey/dataprotection-keys
```

Note the deliberate split: an **unconfigured** `DataProtection__KeysPath` only logs a warning (a bare
`dotnet run` and every CI host sit there, and writes to Credentials are refused with `503` instead),
while a **configured but unwritable** path fails startup — the first is a legitimate development
default, the second is always a misconfiguration whose only silent outcome is data loss.

## `ASPNETCORE_ENVIRONMENT`: use `Production`, never `Testing`

The overlay sets `ASPNETCORE_ENVIRONMENT=Production`, and that is the only value a deployment
should carry. `Staging` is a reasonable second for a pre-production host: it behaves like
Production for everything below.

**`Testing` is not a staging environment — it is the name the in-process test host runs under,**
and four separate places key off it:

| What | Where | Effect |
|---|---|---|
| Antiforgery (CSRF) enforcement | `Odyssey.Api/Program.cs` | **Skipped** on the Identity endpoints and every controller |
| Database provider | `Odyssey.Api/DatabaseExtension.cs`, `Odyssey.MigrationService/Program.cs` | Switched to in-memory — nothing is persisted |
| Demo-data seeding | `Odyssey.TestData/DemoDataGate.cs`, the one gate for both seeding hosts (the migrations job's `DemoDataSeeder` and the worker's `DemoFileSeeder`) | Allowed, with the published shared password |
| Password-reset links | `Odyssey.Api/Email/SmtpEmailSender.cs` | Written to the log instead of being emailed |

None of that is an authorization bypass — every controller endpoint still requires an
authenticated user — but it is a materially weaker posture reached by typing one word, and
"Testing" is a plausible thing to type meaning "a test deployment".

**The API refuses to start under it.** `TestingEnvironmentGuard` checks the running server: only
the in-process test host (`Microsoft.AspNetCore.TestHost.TestServer`) may run as `Testing`, so a
container that tries answers with a startup exception naming the settings above rather than
serving traffic. The migrations job has no server to inspect and is not guarded — set it there
and it migrates an in-memory database, after which the API never comes up.

### The migrations job reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`

The API is an ASP.NET Core host and reads `ASPNETCORE_ENVIRONMENT`. The migrations job is a plain
console host (`Host.CreateApplicationBuilder`), which reads **`DOTNET_ENVIRONMENT`** and ignores the
`ASPNETCORE_` name entirely, defaulting to `Production` when neither is set. Both compose files
therefore set **both** names on that service, and any hand-written manifest — `docker run`, a
Kubernetes Deployment, a systemd unit — has to do the same or the migrations job will silently
disagree with the API about which environment it is in.

The default is fail-safe (an unset variable means `Production`, the strictest posture), so a manifest
that sets neither is not dangerous — it just never seeds demo data. Setting only the `ASPNETCORE_`
name has the same effect, which is what previously made `SEED_DEMO_DATA=true` do nothing in the dev
stack.

## Rollback

Set `IMAGE_TAG` back to the previous version and re-run pull + up. Rolling back to an image whose
file-storage catalogue lacks a feature that has stored files logs a Critical and keeps going; see
[File storage → Rolling back and removing a feature](#rolling-back-and-removing-a-feature). Note that **schema
migrations are forward-only** — if the newer release added a migration, rolling the images
back does not roll the database back. Take a DB backup before deploying a release that
includes migrations.

The migration history was squashed to a single `InitialCreate` more than once during development,
most recently when the identity context was merged into `OdysseyContext`; a database built before
`20260829095318_InitialCreate` has no upgrade path and must be recreated. **That is over.** A
production database with real data has run since `v0.35.0`, so from then on every schema change is an
additive migration and the history is never squashed again.

**A data migration is restored from backup, not rolled back.** `Down()` reverses schema at most; a
migration that moves or rewrites rows (for example `RetirePropertyAndVehicleAccountTypes`, which moves
Property- and Vehicle-type accounts onto property records and whose `Down()` drops only a constraint)
cannot be reversed by any EF command. Take the backup above before upgrading across one, and keep it
until the upgraded instance has been checked.

## Who can read contact personal data

**Every role that can see the Contacts page can read every contact field, and can download all of
them in one request.** `Guest` — the lowest role — holds `contacts.read`, which is the claim gating
both `GET /api/contacts` and `GET /api/contacts/vcard`.

That matters more than it used to. Issue #48 added **aliases**, each with a free-text label, plus a
**middle name**, a **date of death**, and an organization's **establishment** and **dissolution**
dates. Two of those are special-category-adjacent: an alias is frequently a *maiden or former name*,
which in combination reveals marital or gender-transition history, and a date of death is data about a
deceased person that is often health-adjacent. The free-text label sharpens this rather than softening
it — "maiden name" as an explicit label states outright what an unlabelled alias would only imply, and
unlike a free-text note it is **structured and machine-extractable**: "every contact with a label
containing 'maiden'" is a one-line script against this shape.

**This is an accepted, deliberate posture, not an oversight.** It is right for a single-household
deployment where every role holder is a member of that household. The rejected alternative was a
separate `contacts.sensitive.read` claim: it would make the contact read DTO claim-conditional and need
a `RolePermissions` change, for a boundary that does not exist in that threat model.

**If you are deploying Odyssey somewhere the roles do not map to one trusted household, revisit this
before anything else.** Until a finer claim exists, the only control is role assignment: do not grant
`Guest` — or any role — to someone who should not read the whole contact corpus.

Per-request audit lines are written for both halves, and carry **no values**: a contact change records
the actor, the contact id and the action (`alias.created` / `.updated` / `.deleted`,
`dateOfDeath.set` / `.cleared`), and a vCard export records the actor, the row count and whether
filters were applied.

### The runtime image needs ICU

The API image installs `icu-libs` and pins `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`. Contact
alias uniqueness is checked **accent-insensitively**, to agree with the database's own
`utf8mb4_*_ci` unique index; that comparison is ICU-backed and silently stops folding accents under
invariant globalization, which is the alpine runtime's default. The API asserts this at startup and
refuses to serve rather than accept an alias the index will then reject. If you build your own image,
keep both.

Since the news reader (issue #470) the **worker** image installs ICU and pins the variable too: both hosts compute a
feed's URL key, which maps an IDN host to punycode through ICU, and they must agree. The worker asserts it at startup
with the same guard.

## User profile pictures

A signed-in user can attach, replace and remove one picture of themselves at `/account`; it stands in for
their initials on the account page and in the administrator's user list. Three things are an operator's
concern.

- **`profile-images.read` is the lever for who sees colleagues' pictures.** Every role holds it out of the
  box, so its reach is "any authenticated caller"; revoking it from a role (Guest, say) narrows that, and
  reaches live sessions within a minute ([no sign-out needed](#new-or-revoked-permission-claims-need-no-sign-out)).
- **Disabling an account hides its picture from everyone, administrators included.** There is no
  administrator write path, so disabling (or deleting) the account is the remedy for an abusive upload —
  and **evidence must be captured before disabling**, since afterwards only direct database access reaches
  the row. Only an *administrative* disable does this: a user locked out by mistyped passwords keeps their
  picture, deliberately, so the endpoint cannot confirm a failed-login lockout.
- **Three `RateLimiting:*` sections** stay in deploy-time configuration: `ProfileImageWrite`,
  `ProfileImageDelete` and `ProfileImageRead`. Write and delete are separate budgets so an exhausted upload
  budget never blocks removal, a GDPR Art. 17 control. Keep the read budget generous: a rejected read is
  invisible (the avatar falls back to the monogram), so a tight limit is a silent defect.

## What leaves the deployment

Every outbound destination the shipped build can contact, and who contacts it. Each host is a
compile-time constant or a shipped default; the type that holds it is named in parentheses. The sections
after this one give each flow in detail. Neither compose file restricts container egress, so a firewall that does must
open exactly what you keep below — and blocking a host is always a valid way to restrict a row: the feature
then fails closed (a `503`, a failed run or a skipped send), and nothing falls back to another host.

| Destination host(s) | Called by | What is sent | How to turn it off or restrict it |
|---|---|---|---|
| `ws.geonorge.no` (Norway) or `geocoding-api.open-meteo.com` (worldwide) — never both for one search (`KartverketLocationSearch`, `OpenMeteoLocationSearch`) | **api** | The address-search text a user typed (at least 2 characters for Norway, 3 worldwide); no user id, name, stored location or API key | Only `locations.create` holders can search: revoke it to stop searches. Per-user and instance-wide limits are `RateLimiting:LocationSearch` / `LocationSearchGlobal`. See [Household locations](#household-locations-what-leaves-the-deployment-and-how-to-erase-one). |
| `tiles.openfreemap.org` — the map style, tiles, glyphs and sprite (`MAP_ALLOWED_ORIGINS` default) | **browser** | The viewer's IP address and user agent, the deployment's hostname (`Origin`), and the tile coordinates around the location shown; no cookie, name or address | Point both `MAP_STYLE_URL_*` at the shipped blank styles and set `MAP_ALLOWED_ORIGINS=` empty; the CSP then admits nothing else. See [Switching it off](#switching-it-off). |
| `api.met.no` (Locationforecast) (`MetNorwayForecastSource`) | **worker** (schedule); **api** (on-demand reads and the Admin refresh) | A weather point's latitude and longitude rounded to 3 decimals (about 100 m), its altitude, and the administrator-set contact in the `User-Agent` | Nothing is fetched while **`/settings` → Weather → Contact for MET Norway** is empty (the shipped default). Per point: turn its forecast off, or delete it. Only an Admin creates points. See [Weather](#weather-what-leaves-the-deployment-and-how-to-erase-it). |
| `frost.met.no` (observations) (`FrostObservationSource`) | **worker**; **api** (as above) | As above, plus the Frost client id | Leave the Frost client id unset at **`/settings` → Credentials**, or turn a point's historical data off. |
| `data.norges-bank.no` (`NorgesBankClient`) | **worker** (the sync); **api** (the published-currency list, on an Admin's *Refresh status* or *Start sync*) | Currency codes and dates only; the enabled set shows which currencies the household uses | Off until an Admin starts syncing a currency at `/currencies`; stop each currency's sync to stop it again. See [Exchange-rate sync](#exchange-rate-sync-what-leaves-the-deployment). |
| The file-analysis base URL — `api.anthropic.com` by default (`SystemSettingsDefaults.FileAnalysisBaseUrl`) | **worker** (extraction); **api** (matching) | The statement being analysed, the stored API key, and for matching the contact names and aliases and the tag names and descriptions, plus any administrator prompt guidance | Off by default: **`/settings` → File analysis** (binds on the next request). Narrow who can start one by revoking `file-analysis.create`. See [File analysis](#file-analysis). |
| Any feed host users subscribe to, and the image hosts those feeds reference (`NewsEgress`) | **worker** only | Anonymous `GET`s with the fixed `User-Agent` `OdysseyNewsReader/1.0`; no cookie. The host learns the deployment's public IP and the feed URL, including any token in it | Nothing is fetched while no feed exists; revoke `news-feeds.create` to stop new subscriptions. Private and loopback hosts are refused unless listed in `NewsPrivateHostAllowList`. See [News reader](#news-reader-feeds-what-leaves-the-deployment-and-the-workers-network). |
| The SMTP relay set at **`/settings` → Email** — empty by default (`SystemSettingsDefaults.EmailSmtpHost`) | **api** | Transactional mail (account confirmation, password-reset links) with its recipients' addresses, and the stored SMTP username and password | No mail is sent while the host is empty. Changing host or port, or turning STARTTLS off, clears the stored credential. See [Mail is configured after first sign-in](#mail-is-configured-after-first-sign-in). |
| Caddy's ACME issuers (Let's Encrypt by default) | **caddy** (public deployment only) | Your domain name and Caddy's ACME account, for certificate issuance and renewal | A private deployment uses Caddy's internal CA for `odyssey.localhost` and contacts no issuer. |

Image pulls from GHCR are made by the Docker host at deploy time, not by a container. Nothing else is
contacted: the client's Content Security Policy admits only its own origin plus `MAP_ALLOWED_ORIGINS`, and
no instrument-price source ships in this build (the sync framework registers none).

## Household locations: what leaves the deployment, and how to erase one

Issue #350 added **locations** — the household's places, each with a name, an address line,
coordinates to about 1 m, a time zone and a free-text description. Three things about them are an
operator's concern.

### Claims, image and limits

- **Claims.** `locations.read` is held by every role, Guest included, and **sees every address in the
  app** — contact and property addresses included, since every place and address is a location.
  `locations.create`, `locations.update` and `locations.delete` are Admin, Owner and User.
- **Address search sends text to a third party** (below), and only `locations.create` holders can search.
- **The runtime image needs `tzdata`.** The API Dockerfile installs it, and the API refuses to start
  if `Europe/Oslo`, `Europe/Kyiv`, `America/New_York` or `Etc/UTC` does not resolve. If you build your
  own image, keep it.
- Two deploy-time limits: `RateLimiting:LocationSearch` (per user, default 30 per 60 s) and
  `RateLimiting:LocationSearchGlobal` (whole instance, default 120 per 60 s).
- **An event or entry at a place linked to a weather point uses that point**, with no fallback to the
  caller's preference or the household default even when the point's forecast is off; a place with no
  link falls through to the preference and then the household default.

### The geocoding data flow

Saving, reading and editing a location never leaves the deployment. **Only the address search does**,
and only when a user types into it:

| Region chosen | Receives the search text | Who |
|---|---|---|
| Norway | `ws.geonorge.no` (two requests: addresses and place names) | Kartverket, the Norwegian Mapping Authority (a state agency) |
| Worldwide | `geocoding-api.open-meteo.com` | Open-Meteo, Switzerland (EU adequacy decision) |

Never both. No user id, name or stored location is sent, and no API key is used. Both hosts are
compile-time constants and redirects are not followed. Searches need at least 2 characters (Norway) or
3 (worldwide), and the client waits until typing pauses, so prefixes are not sent one keystroke at a
time. Results from both providers are CC BY 4.0, and Odyssey shows the credit wherever their data
appears.

**Logging.** The search text travels in a `POST` body, so it is in no URL, and the API never logs it.
The outbound clients have their HTTP loggers removed, because their URLs do carry it. The bundled
nginx turns its access log off for the location API (`/api/api/locations…`, the path the browser
calls); the `/locations` page itself is an ordinary SPA route and is logged like any other. **Any other proxy in front of
Odyssey** — Caddy in the production overlay, or your own — logs only the URL, which carries no search
text; if yours is configured to log request bodies, exclude `/api/api/locations` from it.

**Privacy notice.** Odyssey ships no privacy notice of its own. If you publish one for your household,
it should say: locations (addresses, coordinates and descriptions) are visible to every household
member including guests; address searches are sent to Kartverket or Open-Meteo as described above;
viewing a location's map sends the viewer's IP address and the map tiles around that location to the
tile host (see [Location map](#location-map-what-leaves-the-deployment-and-how-to-switch-it-off));
and locations are kept until someone overwrites them (next section).

### Erasing a location (data-subject requests)

Since issue #380 an **unused** location can be deleted outright under `locations.delete` — a real
`DELETE`, the erasure path. A location something still uses (a contact address, a property, an event,
a recurring series, a journal entry, or a merge forwarder) refuses with a `409`. Weather does not: since
issue #387 a place only *links* to a weather point, and the link goes with the place.

1. As **Admin**, find the row on the Locations page. **If a weather point was created from this place**
   (or converted from its old per-place weather, under the same id), that point holds its own copy of the
   rounded coordinates and its name may be the place's — erase or rename it as in
   [Weather](#weather-what-leaves-the-deployment-and-how-to-erase-it).
2. **If it is unused** (the *Not used* filter, `?referenced=false`), **delete** it.
   **Deleting a contact or a property never deletes its address**: the address stays behind as an unused
   location, readable by every `locations.read` holder (Guest included). When a request follows the
   deletion of a contact or a property, open the Locations page, choose the **Not used** filter to find
   the addresses those records left, and delete each one that belonged to the data subject.
3. **If it is used**, either overwrite it in place — replace the name with something neutral (it must
   stay unique, e.g. `Erased 2026-10-23`), clear the address fields, the address text and the
   description, and clear the coordinates; an update keeps no previous value — or **merge** it into
   another place and then delete the merged-away forwarder. A forwarder must be deleted before the place
   it forwards to.
4. The log records who deleted, merged or changed it and which fields changed — never the values.
5. Copies outside the live row are **not** purged: database backups, and any Admin data export downloaded
   before that feature was removed, age out under your existing export and backup retention.
6. Record the request, the date and the backup retention that still applies.

Deleting a user leaves every location they created; only the attribution is cleared.

## Location map: what leaves the deployment, and how to switch it off

Issue #370 adds an interactive map with a pin to each location's expanded record. The **browser**
draws it with MapLibre from a third-party basemap, by default the hosted styles of
[OpenFreeMap](https://openfreemap.org/). The API only hands the browser the style URLs and the
attribution (`GET /api/map-styles`, gated on `locations.read`); it never fetches a tile itself.

### Configuration

Five deploy-time variables. They are **not** System settings, deliberately: the browser can only reach a
host the client container's Content Security Policy allows, and nginx renders that CSP from
`MAP_ALLOWED_ORIGINS` when the container starts. An admin-editable style URL would either be silently
blocked or force a CSP wide enough to defeat it.

| Variable | Read by | Default | Rule |
|---|---|---|---|
| `MAP_STYLE_URL_LIGHT` | api (`Map__StyleUrlLight`) | `https://tiles.openfreemap.org/styles/liberty` | absolute `https` with no query, fragment or userinfo, whose origin is listed in `MAP_ALLOWED_ORIGINS`; or a root-relative path such as `/maps/blank-light.json` |
| `MAP_STYLE_URL_DARK` | api (`Map__StyleUrlDark`) | `/maps/odyssey-dark.json` | as above |
| `MAP_ATTRIBUTION` | api (`Map__Attribution`) | `OpenFreeMap © OpenMapTiles Data from OpenStreetMap` | plain text, no `<`, `>` or `&`, ≤ 300 characters |
| `MAP_ATTRIBUTION_URL` | api (`Map__AttributionUrl`) | `https://www.openstreetmap.org/copyright` | absolute `https`, no fragment or userinfo |
| `MAP_ALLOWED_ORIGINS` | api (`Map__AllowedOrigins`) **and** client | `https://tiles.openfreemap.org` | lowercase `https://host[:port]` origins separated by single spaces, at most 8, no path or trailing `/`; empty is the off-switch |

Leave them unset to use the defaults. The production overlay sets none of them, so an unset variable
takes the base compose file's single-dash default — the same value the client image's `ENV` and the
API's `appsettings.json` carry, which a test keeps equal. A malformed value stops the
container rather than degrading: the API refuses to start (`map-config-invalid`, an
`OptionsValidationException`), and the client exits before nginx starts (`map-csp-invalid`). Both
messages name the key and the rule, never the value.

Rules that are easy to get wrong:

- **Set `MAP_ALLOWED_ORIGINS` for both containers.** The base compose file feeds one variable to both, so
  this only bites a hand-written deployment. If they differ, the browser blocks the map and it shows as
  unavailable.
- **The compose default is single-dash (`${MAP_ALLOWED_ORIGINS-…}`).** It applies only when the variable
  is *unset*, so an explicit `MAP_ALLOWED_ORIGINS=` (the off-switch) survives. Do not "fix" it to `:-`,
  which would silently restore the third-party host.
- **List every host the style references.** A style JSON names its tiles, glyphs and sprite, possibly on
  other hosts. The API checks only the style URLs' own origins — it never fetches the style — and the
  browser's CSP blocks any unlisted host, so a missing one is a blank map, never a silent leak. The
  OpenFreeMap defaults keep everything on `tiles.openfreemap.org`.
- **Dedicated tile hosts only.** The listed origins are admitted to `connect-src` and `img-src` for the
  **whole app**, not just the map. Never list a multi-tenant host — a general CDN, an object-storage
  bucket, `*.github.io` — where someone else could receive data an injected script sent there.
- **The shipped basemaps are OpenFreeMap `liberty` (light) and Odyssey Dark (dark).** Odyssey Dark,
  `/maps/odyssey-dark.json` in the client image, is OpenFreeMap's `fiord` with two changes: its
  buildings are drawn visibly, and its labels are lifted to at least 4.5:1. Upstream `fiord` paints
  buildings at about 16% opacity, navy on navy, so houses could not be told from the ground. The style
  file is served by the deployment itself, so its own URL needs no `MAP_ALLOWED_ORIGINS` entry, but its
  tiles, glyphs and sprites still come from `tiles.openfreemap.org`, so that origin does. The API
  refuses to start if `MAP_STYLE_URL_DARK` (or `_LIGHT`) names it while `tiles.openfreemap.org` is
  not listed. All five OpenFreeMap styles (`liberty`,
  `bright`, `positron`, `dark`, `fiord`) live on `tiles.openfreemap.org`, so switching between them
  needs no change to `MAP_ALLOWED_ORIGINS` — set `MAP_STYLE_URL_LIGHT` / `MAP_STYLE_URL_DARK` and
  restart the `api` container.
- **Change the attribution when you change the tile source.** It *replaces* the style's own credits: the
  client turns MapLibre's attribution control off and renders this text, so no string a remote style
  serves reaches the page. The licence obligations of the new source are yours to meet with it.

### The data flow

When someone expands a location with its map visible, their **browser** requests the style, tiles,
glyphs and sprite directly from the tile host. That host learns:

- the viewer's IP address and user agent;
- the deployment's hostname, from the CORS `Origin` header (`Referrer-Policy: no-referrer` hides the
  page path only, not the hostname);
- the tile coordinates around the location — at street zoom enough to identify a home. That precision is
  bounded by the tile **source's** own maximum zoom, not by how far the user zooms: the browser requests
  nothing finer than that maximum and enlarges the last level locally. OpenFreeMap's is 14, so its
  host learns the same whether the map shows zoom 14 or 24 (the client's ceiling, MapLibre's own). A
  self-configured source with finer tiles discloses correspondingly more. Either way it is much finer
  than the ~100 m position weather sends to MET Norway, and it comes from the browser, not the server.

It does **not** learn the location's name, address, description or id, the Odyssey path, or any cookie
(tile requests are sent with `credentials: 'omit'`). The map loads without an in-app notice — a product
decision recorded in issue #370 §7.5 — so the disclosure belongs in your privacy notice (see
[Household locations](#household-locations-what-leaves-the-deployment-and-how-to-erase-one)).

The default recipient, as stated on openfreemap.org when this was written (November 2026 — re-check it):

| | |
|---|---|
| Operator | OpenFreeMap — Hyperknot Software Kft., Hungary (volunteer-run project by Zsolt Ero) |
| Jurisdiction | Hungarian law (its terms of service); server location not stated |
| Retention | access logs without IP addresses; error logs that may hold the IP and requested URL, deleted after 7 days; IP logging during a security incident, deleted after at most 30 days. It states no cookies or tracking, and that it may use Cloudflare as a CDN |
| DPA | **none offered** |
| Service guarantee | **none**: no SLA, and the service may be discontinued without notice |

### Switching it off

Point **both** styles at the blank styles the client ships and admit no third-party origin. Leaving
either style at its default fails the API at start-up, naming the key, the dark one included: it is
served by the deployment but its tiles are not.

```bash
MAP_STYLE_URL_LIGHT=/maps/blank-light.json
MAP_STYLE_URL_DARK=/maps/blank-dark.json
MAP_ALLOWED_ORIGINS=
```

The CSP then admits nothing but the deployment itself, nothing leaves it, and the map shows a plain
background with the pin. This is configuration, not a feature flag: access is still governed by
`locations.read`. A self-hosted basemap (a same-origin PMTiles style, say) later replaces the blank
styles the same way, with `MAP_ALLOWED_ORIGINS` left empty.

### The client's site config is rendered at start

The client image's nginx site config is now a **template**
(`Odyssey.Client/nginx/templates/default.conf.template`). The image's stock envsubst step renders it into
`/etc/nginx/conf.d/default.conf` at every start, substituting only `MAP_ALLOWED_ORIGINS`. Two start
checks bracket it: `15-validate-map-origins.sh` rejects a bad value before the render, and
`25-assert-rendered-config.sh` refuses to let nginx start unless the rendered file exists, sets the
`Content-Security-Policy` header, has the `/api/` proxy, and holds no unsubstituted placeholder. The
base image's welcome config is deleted at build time, so there is no fallback to serve.

**Read-only containers need a tmpfs on `/etc/nginx/conf.d`.** The production overlay runs the client
with `read_only: true`, so it mounts `/etc/nginx/conf.d:uid=101,gid=101` as a tmpfs for the render.
Without it the container exits instead of serving: on a read-only root filesystem the stock envsubst
step itself fails, and on a directory the nginx user merely cannot write it logs an error and carries
on, which `25-assert-rendered-config.sh` catches as `map-config-unrendered: the rendered site config is
missing`. Either way nginx never starts without its CSP. If you run your own orchestration with a
read-only root, give it the same mount.

## Weather: what leaves the deployment, and how to erase it

Issue #348 added **weather** for the household's locations, from MET Norway: the forecast from
Locationforecast (`api.met.no`) and, optionally, measured days from Frost (`frost.met.no`).

### Setting it up

- **Nothing is fetched until an administrator sets it up.** MET's terms require a contact in the
  `User-Agent`, so set **`/settings` → Weather → Contact for MET Norway** (an email address or an
  `https://` URL). Until then every weather read reports *not configured* and no request leaves the
  deployment. Then create weather points and link places to them (below).
- **Observations are optional.** They need a Frost client id (free, from `frost.met.no`), entered in
  **`/settings` → Credentials** (its Weather row) as a secret. Without it, past days show the forecast only. An id that
  cannot be decrypted (a lost Data Protection key ring) stops observations and logs an error hourly.
- **Settings**: the contact, the refresh interval (default 1440 min, i.e. once a day; 30–1440), the cap on
  scheduled points (default 5, 1–20) and the household default point.
- **Claims.** `weather.read` and `weather.preferences.update` are held by every role, Guest included;
  `weather.points.read`, `weather.points.update` and `weather.points.delete` are Admin only.
- Deploy-time limits: `RateLimiting:WeatherRead` (per user, default 120 per 60 s) and
  `RateLimiting:WeatherRefresh` (per user, default 6 per 300 s, covering refresh, history purge and
  point delete).
- **The `worker` runs the schedule**: once a minute, at most one MET request per second. Every replica
  runs it, and a claim on each point's schedule keeps two from fetching the same one. The API still
  fetches on demand and for the Admin refresh.
- **The worker mounts `dataprotection_keys` read-only** so it can read the Frost client id; see
  [Two containers hold the key ring](#two-containers-hold-the-key-ring).

### Weather points (issue #387)

Weather is kept per **weather point** — a named position fetched once — and any number of places
**link** to one point, so two streets in one city share one forecast and one history.

- **Only an Admin creates or moves a point.** **Linking** a place to a point needs `locations.update` and
  `weather.read` (Admin, Owner, User) and sends nothing to MET.
- Points are `/api/weather/points`, and a place's link is `PUT /api/locations/{id}/weather-point`.

### The data flow

Only an **Admin** can cause coordinates to be sent. Creating a weather point — from typed coordinates or
by copying a reviewed place's position — freezes its position, rounded to **3 decimals (≈ 100 m)**, and
its time zone into a *site*; every request uses that site. Moving a point starts a new site; moving a
linked place changes nothing about its point. A read for a point nobody schedules may fetch on demand,
but still only for its frozen site.

What MET receives: the rounded latitude and longitude, the altitude, the contact address in the
`User-Agent` and, for Frost, the client id. No user id, place name or address is sent. The outbound
HTTP clients log nothing, and audit lines carry point and place ids, counts and the actor — never a
name or coordinates. Distances shown to users are whole kilometres.

Weather is shown with MET's attribution (*Weather data from MET Norway*, NLOD 2.0 / CC BY 4.0) on
every surface that displays it.

### Erasing a weather point

1. As **Admin**, turn **both** the point's forecast and its historical data off. A household default
   cannot have its forecast turned off, nor be deleted: choose another default in `/settings` first.
2. **Delete the point** (`DELETE /api/weather/points/{id}`). This removes every weather row of the point
   — the sites, every forecast and observation, the day summaries, the stations and the schedule — and
   unlinks every place that used it; the places themselves are untouched. **Deleting only the measured
   history** (`DELETE /api/weather/points/{id}/history`) keeps the sites and forecast runs, which hold
   the coordinates. That is not an erasure.
3. A large history is deleted in batches. If the request is interrupted, the point shows *deleting* and
   stays blocked; repeat the delete to finish it. An interrupted full delete can only be finished by the
   full delete.
4. Then erase the place itself as in the previous section, if the request covers it.
5. Copies outside the live rows age out with your backup retention.

## Exchange-rate sync: what leaves the deployment

Issue #349 added a scheduled sync of exchange rates from **Norges Bank** (`data.norges-bank.no`).
It is off until an Admin starts syncing a currency on the *Currencies* page (`/currencies`), which also
shows the sync status, *Run now* and the run history; *System settings* keeps only the start date and
the interval.

### How fetched rates behave

- **Every pair converts through NOK when it has no direct rate** (`A → NOK → B`), and when both exist the
  one dated later wins — the direct rate keeps the tie on the same UTC date. So a hand-entered `USD → EUR`
  stops being used once newer synced USD and EUR rates exist.
- **Fetched rates are stored only in the published `X → NOK` direction**, and readers use `1 / Rate` for
  `NOK → X`. Manual rates are never inverted: converting the other way needs a manual row in that direction,
  or a hop through NOK.
- **A date-only manual rate loses to the same day's fetched rate.** Fetched rates are stamped at 16:00
  Norwegian time (14:00 or 15:00 UTC); a manual rate entered for a date is stamped 00:00. To override a
  fetched day, enter a manual rate with a later time.
- **A future-dated rate counts only from its date** and lists with the status *Upcoming*.
- **Fetched rates cannot be edited or deleted one by one.** `PUT` and `DELETE /api/exchange-rates/{id}`
  on one are a `422`, whoever asks: the sync never refetches a day removed from inside what it already
  holds, so a single delete would leave a gap that conversions silently bridge with an older rate.
  Fetched rates leave per currency, through *Delete fetched rates* once its sync is off (Admin). Owner
  keeps editing and deleting manual rates.
- **The sync runs in the `worker`.** Without a running worker nothing is fetched: *Run now* records a
  `Running` run that no process takes, and the next run or purge started after two minutes marks it
  *Abandoned* and proceeds, logging a warning that asks whether the worker runs. The API reaches Norges
  Bank too, for the list of published currencies, so both containers need egress to
  `data.norges-bank.no`.
- **Both runtime images need `tzdata`** for `Europe/Oslo`, and both Dockerfiles install it. Without it the
  sync runs fail with *Time zone unavailable*; the hosts still start.
- **A warning to know about:** `Exchange-rate sync stored N rounded XXX rate(s) between … and …` means
  Norges Bank published a value needing more than eight decimals. The stored rate is rounded, so its
  reciprocal is off in the last place.

### The data flow

The worker's sync job wakes every 15 seconds. It first takes any manual run the API started — exactly one
worker replica can claim it — and otherwise starts a run when one is due (after the configured interval,
or 5 minutes later while history is still being backfilled). A run sends
Norges Bank only **currency codes and dates** — no user, account, amount or other household data, and
no API key. The enabled set does reveal which currencies the household cares about. The host is a
compile-time constant, redirects are not followed, responses are capped at 10 MB, and each run makes at
most 10 requests at least 1 s apart. Rates are published under NLOD 2.0; Odyssey shows the credit
*Source: Norges Bank (NLOD 2.0)* wherever fetched rates appear.

The run log — since issue #409 the exchange-rate sync's rows of the shared `JobRuns` table (see
[Background jobs: the run log](#background-jobs-the-run-log)), kept for `JobRunRetentionDays` — records each
run's trigger, status, error kind, span, currencies and row counts. Per-currency changes and purges are also written to the application
log under the System settings audit category (`Odyssey.Api.SystemSettings.SystemSettingsService`), and
manual runs under `Odyssey.Core.Finance.ExchangeRateSync.ExchangeRateSyncService`, each with the acting
user's id.

## Instrument prices: what leaves the deployment, and how to stop it

Issue #446 added a watchlist of instruments with manual prices and a sync framework; issue #447 adds the first
price source, **Tiingo** (`api.tiingo.com`): end-of-day closes for US-listed stocks, ETFs and mutual funds, on
Tiingo's free *Starter* plan. Nothing is sent until an Admin **both** pastes a Tiingo token (*System settings* →
*Instrument prices* → *Tiingo API token*) **and** enables a Tiingo binding on an instrument (*Instruments* → the
instrument's price sources).

### What leaves the deployment

- **From the worker only**, over HTTPS to `api.tiingo.com` (a compile-time constant; no setting names it, redirects
  are not followed): the **source symbol** of each enabled Tiingo binding (for example `AAPL`, `BRK-B`), the
  **date range** asked for, and the **token** in an `Authorization: Token …` header. Nothing else — no user,
  account, holding, amount or instrument name. The API container never calls Tiingo.
- **The followed list is personal data where the household is identifiable.** Together with the operator's Tiingo
  account and the server's IP address, the set of symbols discloses a household's investment interests. Tiingo
  most likely receives it as an **independent controller** in the US (a third country), not as a processor for
  the operator.
- **Tiingo's privacy policy and EU-US Data Privacy Framework status: verify before enabling.** Read Tiingo's
  privacy policy (linked from the footer of tiingo.com) and check whether Tiingo appears on the Data Privacy
  Framework list (dataprivacyframework.gov). *These two facts were not confirmed when this section was written —
  tiingo.com renders its legal pages client-side and they could not be read from the build environment.* Whether
  the GDPR household exemption covers a given deployment depends on that deployment, not on Odyssey.
- **One token per deployment.** Tiingo counts its limits **per account**, so a token shared between deployments
  (or with another tool) makes each one run into limits the other spent.

### Limits

Tiingo's free plan allows 50 requests an hour, 1,000 a day (reset at midnight US Eastern) and 500 **unique symbols
a month**. Odyssey stays well inside the first two on its own, across all worker replicas:

| Cap | Value | Why |
|---|---|---|
| Per UTC hour | 25 | half of 50, so two adjacent clock hours stay within 50 even if Tiingo's window is rolling |
| Per run | 25 | equal to the hourly cap |
| Per Oslo day | 600 | 25 × 25 h (the US fall-back day) = 625 < 1,000, so any Tiingo day is bounded by the hourly cap |
| Spacing | 2 s | between consecutive requests of one worker process, retries included |
| Days per request | 1,461 | four years per price request |

A first fetch of a symbol costs two requests (its venue, then its prices); later spans cost one for a week. A run
spends at most half its budget on bringing bindings up to date and the rest on backfilling history, so **a large
watchlist fills over several hours**: about 6 new symbols an hour on the first day, all of 50 current within about
5 h of the first run after the US close (~23:30 Oslo time) once their venues are known. A run that reaches a cap
**defers** the rest — they are not failures, nothing is written on them, and the next run starts at the next hour.

The 500-unique-symbols-a-month limit is Tiingo's, and Odyssey does not count it; a symbol Tiingo refuses for it
records a per-binding failure and is retried the next day.

### Symbols and currencies

- **Symbol format is Tiingo's**: share classes use a dash (`BRK-B`, not `BRK.B`). A symbol Odyssey cannot bind
  (outside `[A-Za-z0-9._-]`) cannot be used.
- **Tiingo states no currency**, so Odyssey derives it from the **listing venue** Tiingo states, through a fixed
  map: `NYSE`, `NASDAQ`, `NYSE ARCA`, `NYSE MKT`, `AMEX`, `BATS` and `NMFQS` (mutual funds) → USD. Any other venue
  — OTC markets, Shanghai and Shenzhen (whose tickers quote in more than one currency) — is refused for that
  binding, as are the five tickers Tiingo lists on a US venue but prices in AUD (`AYA`, `CSF`, `FDV`, `MPG`, `NYM`).
  A batch whose derived currency differs from the instrument's is rejected whole.
- **Not detected:** a source symbol that points at a **different instrument in the same currency** (say `VTI`
  bound to an instrument meant to be `VOO`) fetches that other instrument's prices. Check the symbol when binding.
- **Also not detected:** a ticker **listed after 2026-11-11** on a mapped US venue but priced by Tiingo in another
  currency. The AUD exception list is a snapshot of `supported_tickers.zip` on that date; such a ticker bound to a USD
  instrument would store its closes as USD. Check Tiingo's `priceCurrency` for an unusual listing before binding it.
- A fund's close is its **NAV**. Only the unadjusted close is stored; adjusted prices, dividends and splits are
  ignored.

### Entering the token

Paste the **bare token** from your Tiingo account page — **without** the word `Token` in front. Odyssey adds the
`Token ` prefix itself; a pasted prefix becomes `Token Token …`, which Tiingo refuses, and the source then records a
credential problem on every binding (it fails closed and nothing is fetched). A replaced token takes effect on the
next request without a restart. An unset token means "not configured" (nothing is sent, nothing fails); a token
the deployment cannot decrypt (a lost Data Protection key) fails closed, records *Configuration* on every Tiingo
binding and fails the run.

### Stopping it

- **Stop sending:** disable the Tiingo bindings, or clear the token (*Clear* on its row). Either takes effect on
  the next run.
- **Remove what was fetched:** *Delete prices* on the instrument (*Instruments*), choosing fetched prices, removes
  Tiingo's rows as a purge run; manual prices can be kept.

### Licence (Tiingo's terms) — the operator's to satisfy

Tiingo's free plan is licensed for **internal, personal use**: its terms say the data may be used "for your own
personal use" and not displayed or shared "with another person or organization". Odyssey shows prices to **every
household member** who holds the instrument-prices read claim, and does **not** restrict that display to the token
owner. Each operator supplies their own token — the project ships none — and **satisfying Tiingo's licence for
their household is the operator's responsibility**: confirm with Tiingo that showing prices to the members of your
own household is permitted, or limit who holds the read claim. *The product decision recorded on issue #447
(§7.5) is authoritative where it differs from this paragraph.* Odyssey shows the credit
*End-of-day prices from Tiingo (tiingo.com).* wherever Tiingo prices appear.

### Release note (issue #447)

- **One additive migration** (`AddInstrumentPriceHourlyCap`): two columns on `InstrumentPriceSourceStates` (the
  hourly request counter) and one counter column on `JobRuns` (`InstrumentsDeferred`). Schema only; `Down()` drops
  them. It also drops and re-adds `CK_JobRuns_CountersNonNegative` to cover the new column, which re-validates
  that CHECK over every `JobRuns` row and holds the table for that long (brief at the run log's retained size; the
  worker's jobs wait on it).
- **The sync's forward watermark now records the newest date the source returned** (#468) rather than the date it
  asked up to, so a close published after a run is fetched by the next one. Days more than five days old count as
  fetched whether or not they came back.
- **`MinRequestInterval` is now enforced** (#469), per source and per worker process.
- **A binding whose last attempt failed as unsupported** (unknown symbol, unmapped venue, currency mismatch) is
  not retried until the next Oslo day; editing it (symbol, enabled flag, start date or its place in the source
  order) clears that at once.
- **No new claims, no new setting, no feature switch.** The token is a new secret with no default.

## File storage (filesystem volumes)

Issue #346 added a **filesystem object store**: files kept on one or more deploy-time *volumes* instead of
as `LONGBLOB` rows in MariaDB. **Every file and photo is on it now**; the database holds no file bytes. The
production build has seven *storage features*, each bound to a volume: `PhotoOriginals`, `PhotoThumbnails`,
`FinanceFiles`, `JournalFiles`, `ContactAvatars`, `LibraryFiles` and `NewsArticles` (see
[Photo thumbnails](#photo-thumbnails), [Photo originals](#photo-originals),
[Files on the object store](#files-on-the-object-store) and
[News reader](#news-reader-feeds-what-leaves-the-deployment-and-the-workers-network)). For an operator, two
containers mount a named volume, verify it at startup, and refuse to start on a misconfiguration.

The shipped compose files mount one named volume, `files_primary`, read-write into **`api` and `worker`**
at `/var/odyssey/files/primary`, with `FileStorage__Volumes__primary__RootPath` set on both. The API
**initialises** a new volume on its first start — it writes `.odyssey-volume.json` and records the volume
in the `FileStorageVolumes` table — and the worker waits up to 2 minutes for that, then verifies it. The
**Storage health** panel and the deletion grace period are covered below; the panel needs the Admin-only
claim `file-storage.read`.

### The image owns the mount point

Both the API and the worker image create `/var/odyssey/files/primary` owned by `app` (UID 1654) with
mode `0750`. Docker copies that ownership into a **fresh, empty** named volume the first time it is
mounted, so the default setup needs nothing. Files are written `0640` and folders `0750`, so nothing on a
volume is readable by other users.

A **bind mount** (a host directory instead of a named volume) is not initialised by Docker. Create it
yourself, owned by UID 1654, before the first start:

```bash
sudo mkdir -p /srv/odyssey/files/fastssd
```

```bash
sudo chown 1654:1654 /srv/odyssey/files/fastssd && sudo chmod 0750 /srv/odyssey/files/fastssd
```

Mount it `noexec,nosuid,nodev` where the filesystem allows. Supported backings are **local** filesystems
with POSIX rename and hard links — ext4, XFS, btrfs. NFS, SMB and CIFS are not supported. A fresh ext4
filesystem's `lost+found` is fine: it is on the list of entries an "empty" root may hold.

### Configuration

Volumes and bindings are **deploy-time configuration, never settings**: an editable path would be an
arbitrary filesystem read/write primitive. Set them as environment variables, **identically on `api` and
`worker`** — the worker erases what the API stores, so a volume configured on one and not the other is a
defect (`Odyssey.Worker.Tests` compares the two compose blocks).

| Variable | Meaning |
|---|---|
| `FileStorage__Volumes__<name>__RootPath` | An absolute path to an **existing** directory. `<name>` is letters and digits, starting with a letter (`primary`, `fastssd`). |
| `FileStorage__Bindings__<Feature>` | Which volume a storage feature's **new** files go to (`FileStorage__Bindings__PhotoThumbnails=primary`). The shipped compose files bind all six features to `primary` (see `FileStorage__Bindings__*` in `docker-compose.yml`). A host with volumes and an unbound feature refuses to start (check B1). |

Several features may share one volume: each writes under its own folder. Volume names and feature keys
are matched case-insensitively. Inside a volume, files sit under their sanitized original names
(`photo-thumbnails/3f/a2/<photo id>/IMG_2041_256.webp`), so a volume is browsable on disk.

**Adding a volume on another disk.** Put it in your own override file, on both services, and bind the
feature to it:

```yaml
# docker-compose.override.yml (or a file you pass with -f after the prod overlay)
services:
  api:
    environment:
      FileStorage__Volumes__fastssd__RootPath: /var/odyssey/files/fastssd
      FileStorage__Bindings__PhotoThumbnails: fastssd
    volumes:
      - /srv/odyssey/files/fastssd:/var/odyssey/files/fastssd
  worker:
    environment:
      FileStorage__Volumes__fastssd__RootPath: /var/odyssey/files/fastssd
      FileStorage__Bindings__PhotoThumbnails: fastssd
    volumes:
      - /srv/odyssey/files/fastssd:/var/odyssey/files/fastssd
```

**Rebinding a feature** to another volume affects **new** files only. Existing files stay where they are
and are still read from the old volume, which must therefore stay configured while it holds any. Moving
files between volumes is not supported.

**Removing a volume.** A volume that holds no live files can simply be removed from configuration; the API
logs a Warning and lists it as *Not configured* on the Storage health panel. A volume that still holds
files cannot: the API refuses to start with `MissingVolume`.

**Renaming a volume** in configuration declares a *new* volume. Don't: if the old name held files, startup
stops with `MissingVolume`, and the renamed root carries the old name's marker (`WrongVolume`).

### Startup checks

The API verifies every volume before it listens; **any failure stops the API container**. The worker runs
the same checks (verify-only) before its file clean-up job's first pass; **a failure there stops that job
only** — weather and exchange-rate sync keep running — and the worker re-runs the checks every **10
minutes**, resuming clean-up on its own once they pass. Either way the log line names the check, the
configuration key and the remedy:

```
File storage startup check VolumeLost failed: Volume 'primary' is empty and has no marker, but the
database says it holds files. Key: FileStorage:Volumes:primary:RootPath. Remedy: …
```

With **no volume configured, no storage feature in the build and nothing stored**, the checks are a
no-op and the store is simply unavailable (the panel says "No file volumes are configured").

| Check | Means | Fix |
|---|---|---|
| `InvalidName` | A volume name or a binding is not letters and digits | Rename it. |
| `InvalidRootPath` | `RootPath` is empty or relative | Use an absolute path. |
| `RootNotFound` | The root does not exist. **The API never creates a root**, so a mistyped or unmounted path cannot silently become a directory on the container's ephemeral layer | Mount the volume, or create the directory (owned by UID 1654). |
| `OverlappingVolumes` | Two volumes are the same directory, or one is inside the other (after resolving links) | Give each volume its own directory. |
| `ForbiddenLocation` | A root is `/`, or overlaps the Data Protection keys directory, the application directory, the content root or `/tmp` | Use a dedicated directory. |
| `UnboundFeature` / `UnknownFeature` / `UnknownVolume` | A storage feature has no binding; a binding names a feature this version lacks (a typo); a binding names an unconfigured volume | Correct `FileStorage__Bindings__*`. |
| `NotWritable` | The probe write in the root failed | `chown 1654:1654` and `chmod 0750` the root. |
| `WrongVolume` | The root's marker belongs to another volume (another name, or another id under this name), or is unreadable | You mounted the wrong disk. Mount the right one, or restore the marker from this volume's backup. |
| `UnknownMarker` | The root carries a marker this database has never seen, and either the root still holds files or the database already has volumes | Its files belong to another database. Point the volume at an empty directory, or restore the matching database. |
| `VolumeLost` | The root is empty and unmarked, but the database says it held files — what `docker compose down -v`, a mistyped bind mount or an unmounted disk all look like | Mount the right disk or restore the volume from backup. **The API never re-initialises it**, which would hide the loss. If the files are truly gone and regenerable, see [Backup and restore](#backup-and-restore). |
| `UnrecognisedContent` | The root has no marker but is not empty | Point the volume at an empty directory. |
| `MissingVolume` | A volume that holds files was removed from configuration | Configure it again. |
| `NotInitialised` (worker only) | The API did not initialise a new volume within 2 minutes | Start the API. The worker re-checks every 10 minutes and resumes clean-up on its own; restart it to resume at once. |

**In the worker, a failed check is reported, not fatal.** The container keeps running, so look at the
Storage health panel or the log rather than `docker compose ps`: the panel shows *File clean-up is
stopped: the worker's storage check failed*, the check's name and when; the log has the remedy:

```
docker compose logs worker | grep "File storage startup check"
```

Each worker process records **its own** verdict in `FileStorageWorkerVerdicts` (a row per process: the
check's name, when it first failed, when it last re-checked) and deletes it once its checks pass, so with
several replicas one healthy worker never hides another's failure. A row not re-checked for **30 minutes**
(three re-checks) belongs to a worker that stopped and is ignored, then swept. If a pass has completed
since the failure, another worker is still doing the clean-up and the panel shows a **warning** — *N
workers can't use file storage* — rather than *stopped*. Give every replica identical mounts either way.

If the checks **cannot run at all** — the database is unreachable when the worker starts — the job waits
and re-runs them every 10 minutes; nothing is recorded (the database is what failed), and the panel's
*clean-up hasn't completed* warning covers it after 3 hours.

**After a database reset** (a fresh database beside surviving volumes), an **empty** marked volume is
adopted with a Warning: its marker's id is recorded again. A marked volume that still holds files is
`UnknownMarker`, by design: those files belong to a database that no longer exists.

A rollback past a feature (`B4`) does **not** stop startup; see below.

### Running, and when a disk goes away

A volume that fails **after** startup — an unmount, a read-only remount, I/O errors, a full disk — does not
stop the process. The volume becomes **Degraded**: its reads and writes return `503` with `Retry-After: 60`
(a full disk returns `507`), the Storage health panel says so, and the condition is re-checked at most every
15 seconds and clears on its own once the disk answers. A single missing file is not a degraded volume: that
file alone returns an integrity error. The next restart re-runs the startup checks. `/healthz` is unchanged.

The **Storage health** panel at **System settings → Storage** (claim `file-storage.read`) shows, per
volume: its state, free space, files per feature, files waiting to be erased (and how many of those are
**overdue**), integrity faults, and the disk check; and, for the instance, whether file clean-up is on
schedule, stalled, or stopped by a failed worker check. Anything needing attention is listed above the
tiles and in the page header's signal.

### Deletion, erasure and the grace period

Deleting something that owns stored files makes them unreadable at once. Their bytes leave the disk on
the worker's **hourly maintenance job**:

- **Ordinary deletes** wait for the **deletion grace period** (default 24 h), so a backup that copies the
  volume after the database dump still contains every file the dump references. Removal happens within
  grace + 2 hours: ≤ 26 h at the default, ≤ 170 h at the maximum.
- **Personal data** — features declared *Immediate*, and any delete driven by erasure such as an account
  deletion — skips the grace and is removed within **2 hours**. Since issue #428 no shipped feature is
  *Immediate*: photo originals and thumbnails both wait for the grace, so a deleted photo's bytes stay on
  disk for up to grace + 2 hours. For a data subject's erasure request that cannot wait, see
  [Photo originals → Erasure](#erasure-of-a-photo-original).
- A stored grace that cannot be used (unparseable, `0`, out of range) makes the job use **168 h** and log a
  Warning; the settings page shows the row as faulted. Fix the value at `/settings`.

The maintenance job runs in every worker replica; a lease row (`FileStorageMaintenanceLeases`) elects one
runner per hour, and a crashed runner's lease expires within 5 minutes. **Erasure depends on the worker.**
If the panel says *File clean-up hasn't completed in over 3 hours*, the worker is down or crash-looping:
check `docker compose ps worker` and its logs. If it says *File clean-up is stopped*, the worker is up but
its storage check failed; see [Startup checks](#startup-checks).

**Overdue erasure.** A file still on disk more than **2 hours** after its erase time — the grace above, or
none for personal data — is counted as *overdue* on its volume, and the panel raises it as an error: personal
data is being kept longer than the grace allows. The count uses the same grace the job erases by (168 h
while a stored grace is faulted), so it never flags a file the job is deliberately holding. A count that
stays up with clean-up on schedule means the deletes themselves fail — usually the worker cannot write to
the volume (`chown 1654:1654` the root); the worker's log names each failing object by id, never by name.

Each pass also: sweeps abandoned writes (an upload whose request failed) after an hour; re-hashes up to 200
files (512 MiB, 20 s) against their recorded SHA-256 and flags a file whose mismatch reproduces — a flagged
file returns an error rather than wrong content, and is cleared if it matches again after a restore; and
walks up to 256 folders of the disk looking for files the database does not know.

### The disk check: quarantine and its breaker

A file on a volume with no database row (left behind by a crash, or a restore that did not match) is first
**sighted**, and only **quarantined** in the *next* pass and at least 24 hours later: it is moved to
`.quarantine/<date>/<id>/…` on the same volume and deleted 7 days after that. A full pass over a large
volume takes days; the progress is stored, so a restart resumes it.

**The breaker.** If one pass finds more unknown files than max(100, 1 % of the volume's files), the volume's
clean-up is **halted** with a Critical log, and nothing is quarantined until a later pass finds fewer. That
is the signature of a database restored without its volume (or the wrong database). The panel reports
*The disk check … has paused clean-up*. Restore the matching volume (or database); the halt clears on its
own.

Files the store could not have written (a name that breaks its rules, a file placed by hand) are left alone
and counted. Nothing outside the feature folders, `.tmp` and `.quarantine` is ever touched.

### Backup and restore

1. **Order matters.** Dump the database **first**, then copy each file volume, finishing within the
   deletion grace period. Every file the dump says exists is then in the copy, with one deliberate
   exception: an *Immediate* erasure, which is not preserved. Extra files in the copy are harmless — the disk
   check tidies them.
   ```bash
   docker run --rm -v odyssey_files_primary:/v:ro -v "$PWD":/backup alpine \
     tar -C /v -czf /backup/files-primary-$(date +%F).tar.gz .
   ```
2. **Restore both together.** Never restore the database alone over a live volume: an older dump makes newer
   files look orphaned, and the breaker halts rather than quarantine them — and clears once the matching
   volume is back.
3. **A volume of regenerable files only** (thumbnails) may be left out of backups. Restored without it, the
   root comes back empty while the database says it held files, so the API stops with `VolumeLost` by
   design; the feature's own documentation gives its reset procedure.
4. **After a restore** the sweeps and the disk check tidy up whatever was in flight at backup time, and the
   integrity scrub flags any genuinely missing file.

**A volume backup holds file names and contents.** File names are personal data
(`Passport_Ola_Nordmann.jpg`), on disk and in `StoredObjects.RelativePath`; they never appear in logs, error
bodies or the status endpoint. **Bytes are not encrypted by the application**, the same posture as the
database: encrypt the host disk (LUKS / dm-crypt), and treat a copied volume as
directly browsable.

### Rolling back and removing a feature

Rolling back to an image that lacks a storage feature which has written files logs a **Critical** at
startup and lists the feature on the panel. It does not stop: that feature's pending deletions still
complete (the database's own foreign key refuses any still referenced), its abandoned writes are swept,
and its committed files are kept, unserved, until the feature returns.

**Removing a feature on purpose takes two releases.** First ship a release that keeps the feature in the
catalogue and releases all its files; remove it only once **no `StoredObjects` row with its key remains, in
any state** (the panel's per-feature counts show when).

### Scale-out

The deployment model is **one API container**. Worker replicas are safe: the maintenance lease and the
features' own row claims coordinate them. Each process keeps its own *Degraded* flag; the panel shows the
API's view, and a volume degraded only in the worker shows in the worker's log.

## Background jobs: the run log

Issue #409 records the runs of every catalogued worker job in one table, `JobRuns`, and each worker process's
last tick per job in `JobHeartbeats`. The catalogue, with each job's claims, is `Odyssey.Core/Jobs/JobCatalogue.cs`:
exchange-rate sync, weather, file-storage maintenance, photo thumbnails, statement analysis (issue #444),
instrument-price sync (#446), the news fetch (#470) and the recurrence key backfill (#519).
**`/background-jobs`** reads both; everything there is under `system-settings.read` (Admin), and a job's own
counters additionally need that job's read claim (`weather.points.read`, `file-storage.read`, `photos.read`,
`file-analysis.read`, `news-feeds.update`, `calendar.read`; the two syncs need none).

### What the worker records

| Job | One run row per | Held rows |
|---|---|---|
| Exchange-rate sync, instrument-price sync | run (the exchange-rate sync also per purge) | — |
| File-storage maintenance | pass — scheduled hourly, or manual, picked up by a **30 s request poll** | while *this* worker's storage checks fail (`HeldByCheck` names the check, never a path) |
| Weather, photo thumbnails, statement analysis, news fetch, recurrence key backfill | **work burst**: consecutive ticks with work share one row; an idle tick, or the burst reaching 1 h, finishes it | thumbnails: while the worker is Paused, Tripped, PolicyFailed or StorageUnavailable; news: while its breaker is tripped or storage is unavailable |

Every job writes its heartbeat first on every tick (at most once a minute per process), before any early
return, so an idle, unconfigured or held job is never mistaken for a dead one. A job with no heartbeat for
5 minutes reads **Not ticking** on the page.

`JobRunMaintenanceJob` (worker, every 5 minutes) abandons a run whose process has gone (after its job's stale
window — it never touches a file-storage pass while the maintenance lease is live), closes a held period whose
process has stopped heartbeating, and once an hour deletes finished runs older than **`JobRunRetentionDays`**
(`/settings` → Data, 14–730 days; a degraded read keeps the larger value — never prunes more on a fault).
`Running` rows and open held periods are never pruned. The newest-500 rule of the old sync log is gone.

### Manual runs

`POST /api/background-jobs/{job}/runs` needs `system-settings.update` **and** the job's own claim
(`weather.points.update`, `file-storage.read` for file-storage maintenance, `photos.update` for thumbnails,
`news-feeds.update` for the news fetch, `calendar.update` for the recurrence key backfill; the two syncs need
none beyond the first). Statement analysis has **no manual run** (`409 job-run.manual-run-unsupported`): every
job it runs is one a user started. The file-storage manual pass is gated on `file-storage.read` by decision — no file-storage write claim exists — and
uses the same deletion grace and lease as a scheduled pass. A thumbnail manual run drains pending jobs and
re-queues each `Failed` job **once in its lifetime** (`ManualRequeues`); `Unsupported` jobs, crashes and
timeouts included, are never re-queued.

Each job has a 60 s cooldown, and a per-user limiter sits in front of all of them:
`RateLimiting:BackgroundJobsRun` (default 5 requests per user per minute). Every accepted manual run is written
to the application log at Information under the System settings audit category
(`Odyssey.Api.SystemSettings.SystemSettingsService`) with the acting user's id, the job and the run id —
independent of the row, which is pruned and whose requester is nulled when the account is deleted.

Under `ASPNETCORE_ENVIRONMENT=Testing` only the exchange-rate sync and the news fetch execute a manual run
inline; the others stay unclaimed (no test may call MET or load ImageMagick).

## Photo thumbnails

Issue #362 renders two WebP thumbnails per photo — **Small (200 px)** and **Medium (400 px)** on the long
side — in **`Odyssey.Worker`**, and stores them on the `PhotoThumbnails` file-storage feature. Grids,
album covers, pickers and the journal gallery show them (issue #365); the viewer, "Open original", the
lightbox and the edit banner always show the original. Thumbnails are **derived data**: every one can be
regenerated from its photo, so none is exported, and a volume holding only thumbnails may be left out of
backups.

### Requirements

- **ImageMagick (Magick.NET) is a native dependency of the worker image only.** The API never loads it;
  `Odyssey.Worker.Tests` fails if any other project references it.
- **Worker container limits**, in both compose files: `mem_limit: 1g`, `pids_limit: 256`, and `/tmp` as a
  64 MB `tmpfs`. ImageMagick itself is capped at 320 MB of pixel cache with disk spill off. Keep them if
  you write your own compose file.
- **`FileStorage__Bindings__PhotoThumbnails`** must be set on `api` and `worker`, identically.
- **No claim or setting of its own.** Thumbnails are served by `GET /api/photos/{id}/thumbnail` under
  `photos.read`. The worker state on `GET /api/photos/stats` is filled only for `system-settings.read`.
- **New photos are queued ahead of the background backlog**; until a photo's two thumbnails exist the
  client shows the original.
- **Renaming a photo re-renders its thumbnails**, because the stored files carry the name.

### What the worker does, and what the panel says

The job runs every 15 seconds in each worker replica. Replicas coordinate through the job rows (a claim,
a lease renewed every 60 s, a 15-minute deadline), never through process state. Each worker writes a
heartbeat; **System settings → Storage health → Background work → Thumbnail worker** shows the most severe
state among the workers heard from in the last 3 minutes.

| State | Meaning | What to do |
|---|---|---|
| Running | Normal. The row's second line counts photos waiting, failed and unsupported. | Nothing. A few *unsupported* are normal (a format or size the decoder refuses). |
| Paused | One decode hung past the 30 s watchdog. Native code cannot be cancelled, so claiming stops until that thread ends. After 10 minutes the worker exits (code 70) so the container restarts, at most once an hour per worker. | Nothing, if the worker runs under Compose: `restart: unless-stopped` brings it back. **Outside Compose, the process supervisor must restart an exited worker**, or it stays down. |
| Stopped after crashes | 3 crashes or hangs within an hour trip the breaker. Claiming resumes an hour after the latest; the panel names the time. Other jobs keep running. | Read the worker log for the incident lines. Repeated trips across days point at one bad file or a decoder regression: patch (below). |
| Decoder self-test failed | At startup the worker checks the ImageMagick policy refuses MVG, SVG and `text:@file` and still decodes JPEG, PNG, GIF and WebP. Any surprise disables thumbnails in that process. | Redeploy from a correct image. Never edit `policy.xml` in a running container. |
| Waiting for storage | The `PhotoThumbnails` volume is degraded or missing in the worker. | Same as a degraded volume (above). Claiming resumes within one tick of the volume answering. |
| Not running | No worker has written a heartbeat in 3 minutes. Every background job is stopped — thumbnails and file clean-up among them. | Start the worker container. Photos keep showing their originals. |

### Patch policy for ImageMagick

ImageMagick and libwebp parse untrusted bytes in native code, so they are patched on a clock:

- **Critical or actively exploited ImageMagick or libwebp CVEs: within 72 hours.** CVE-2023-4863 (libwebp) is
  the precedent; WebP is both an input and the output here.
- **Other Magick.NET security releases: within 14 days.**
- **The clock starts** at the first public upstream advisory — an ImageMagick or libwebp GHSA or CVE, or a
  Magick.NET security release note, whichever comes first. NuGet advisory data can lag those, so it is not
  the start.
- **The repository owner watches** `dlemstra/Magick.NET` releases and security advisories. That watch is what
  starts the clock for a native ImageMagick or libwebp CVE: NuGet advisory data, and so `NuGetAudit`, can lag the
  upstream advisory.

What enforces it:

- **`NuGetAudit`** (`NuGetAuditMode=all`, `NuGetAuditLevel=moderate`) in `Directory.Build.props`, solution-wide,
  test projects included; `TreatWarningsAsErrors` makes an advisory fail the restore. A triaged advisory is
  silenced with `NuGetAuditSuppress` and a dated comment, never `<NoWarn>`. This is **fail-closed**: an
  unreachable advisory source (`NU1900`) also fails the restore, so image builds and publishing stop until it
  answers or the build runs with `-p:NuGetAudit=false` on purpose.
  It runs only when something restores, so on a day nothing is built it reports nothing; there is no scheduled
  audit (removed at the owner's request).
- **Dependabot security updates should be switched on for the repository** (GitHub → Settings → Code security →
  Dependabot alerts and Dependabot security updates). It is a repository setting, not part of
  `.github/dependabot.yml`, and it covers every dependency: a Magick.NET advisory then gets a fix PR within hours,
  where the weekly version updates in `dependabot.yml` would take up to 7 days.

There is **no runtime stop switch** (no feature toggles, per CLAUDE.md). For an exploited CVE before a patch
exists, the emergency step is to roll back to the previous image (below).

**Residual risk, accepted:**

- A memory-safety bug in one of the four allowed coders would run in the **worker**, as `app`. The worker
  serves no port and is not internet-facing, but it holds the database credentials and reads the API's Data
  Protection key ring, so it can decrypt every stored credential (see *Two containers hold the key ring*). The
  API never loads ImageMagick.
- GIF, animated PNG and animated WebP reach ImageMagick **unstripped**, and an uploader chooses whether a PNG or
  WebP is animated, so ImageMagick's GIF, PNG and WebP metadata parsing is reachable by any `photos.create`
  holder. Only still JPEG is fully covered by the pre-strip.
- A decoder-only container without the key ring is deferred. It is the next mitigation if a coder CVE proves
  exploitable.

### Platform

The worker image is **linux-musl-x64** (Alpine). Magick.NET ships no `linux-musl-arm64` native library, so
an **arm64 Alpine worker is unsupported**: the self-test fails and the state reads *Decoder self-test
failed*. The Phase 0 spike measured two limits that differ from the specification: the list-length limit is
64 (1 refuses every image, and an animated WebP decodes all its frames), and the pixel-area limit is in
pixels.

### Emergency rollback

- Roll back only to the **immediately previous release**, and only if every migration between it and this one
  is additive. `AddPhotoThumbnails` is.
- **Roll back the `api` and `worker` images together**; they are released as one version.
- **Case R1 — the previous image ships the file store (#346) but not thumbnails.** Remove
  `FileStorage__Bindings__PhotoThumbnails` from **both** services' environments **before** starting them;
  otherwise check B2 (`UnknownFeature`) refuses to start both. The thumbnail rows and files then belong to an
  unknown feature, which B4 leaves untouched: not served, not deleted.
- **Case R2 — the previous image predates the file store.** The `FileStorage__*` variables are ignored, the new
  tables are unused, and the files stay on disk.
- **Erasure while rolled back.** A photo delete cascades its `PhotoThumbnails` rows, but the files — named after
  the photo — stay on disk until the patched image returns and the unreferenced sweep erases them. Record the
  extended window in the incident record.
- **Returning.** Restore the binding and deploy the patched image. The client falls back to originals
  throughout.

## Photo originals

Since issue #428 the original of every photo uploaded in the journal or the photo library lives on the
**`PhotoOriginals`** file-storage feature. A photo owns its bytes: it has no Files-store row, `files.read` does
not reach it, and its original is served by `GET /api/photos/{id}/content` under `photos.read`.

- **`FileStorage__Bindings__PhotoOriginals`** must be set on `api` and `worker` (both compose files and the
  AppHost bind it to `primary`). Without it both refuse to start (check B1). Keep the two services identical.
- **Upload is one multipart call**, `POST /api/photos`, rate-limited per user by `RateLimiting:PhotoUpload`
  (120 per 10 minutes by default). It refuses with `507` when the volume's free space is below the reserve —
  the store's 1 GB floor plus 4 GB — and with `409` (`photo-duplicate`) when the same image is already a photo.
  There is no path that creates a photo from an existing Files-store file.
- **Thumbnails are graced**: their release waits for the deletion grace like everything else.

### Erasure of a photo original

A deleted photo's original waits for the deletion grace (default 24 h, at most 168 h) before the hourly job
removes it, so a backup taken in that window still holds it. For an erasure request that cannot wait, lower
**Deletion grace** at `/settings` to 1 h, delete the photo, and restore the setting after the next
maintenance pass. The file name on disk is the sanitized original name; never log or share a volume
listing.

### Backup

Photo originals are **primary data**, unlike thumbnails: the volume that holds `PhotoOriginals` must be in
every backup, copied after the database dump and within the grace (see [Backup and
restore](#backup-and-restore)). A database restored without its volume makes every photo unreadable,
and the disk check's breaker halts clean-up rather than quarantine the volume.

## Files on the object store

Since issue #448 every other file lives on file-storage features, one per area (four since issue #525 added
`LibraryFiles`). Placement is not an
access boundary: who may read a file is still decided by the claim of the record it is attached to.

| Area | Feature | Root on the volume | What lands there |
|---|---|---|---|
| Finance | `FinanceFiles` | `finance-files/` | Account, transaction, contract, tax-statement and property documents; file-analysis uploads; anything not linked to a journal record |
| Journal | `JournalFiles` | `journal-files/` | Journal-entry and task attachments |
| Contacts | `ContactAvatars` | `contact-avatars/` | Contact images |
| Library | `LibraryFiles` | `library-files/` | Library-module files: book attachments (e-books, PDFs, papers; #527) and recipe files (#525) |

Each file sits at `<root>/<upload year>/<bucket>/<sanitized original name>.<extension>`. The area is chosen once,
at upload: the upload form sends `area` (`Finance`, `Journal` or `Library`), the avatar path always writes `ContactAvatars`.
A file later attached somewhere else stays where it was put.

- **All four features must be bound** (`FileStorage__Bindings__FinanceFiles`, `__JournalFiles`,
  `__ContactAvatars`, `__LibraryFiles`) on **both** `api` and `worker`; the base compose file and the AppHost bind them
  to `primary`. Without them both refuse to start (check B1). `FileStorageDeploymentParityTests` keeps the shipped
  files identical.
- **`POST /api/files`** takes a required `area` form field, validates `description` at 256 characters, and is
  rate-limited per user by `RateLimiting:FileUpload` (120 per 10 minutes by default); the contact-avatar upload
  shares that limit. An upload refuses with `507` when the volume is below the reserve (the store's 1 GB floor
  plus 4 GB), `503` when it is degraded, and `429` past the limit — each with `Retry-After` where it applies.
  Nothing is stored on a refusal, and no bucket slot is taken.
- **Downloads** carry `nosniff`, a strong `ETag`, `Cache-Control: private, no-cache` and range support.

### Legacy file storage removed

**The database holds no file bytes.** Every photo original and file lives on the object store; the former
`FileBlob` table and the jobs that moved its contents out are gone (upgrading across their removal is in
the [upgrade notes](upgrade-notes.md)). The user profile picture's own store (`UserProfileImageBlobs`) is a
different, deliberate design and is unaffected.

- **Demo files on a dev stack** (Development/Testing only) are written by the worker after the API has initialised
  the volumes, so a fresh dev stack shows its demo photos, attachments and contact images a little after it reports
  healthy. `Seed__DemoData` reaches the worker too; the production overlay pins it `false` on both.

### Moving the finance area to its own volume

Every area may bind to its own volume. Rebinding an area that already holds objects is not a configuration
change: the rows name the volume they were written to, so the new binding applies to new writes only and the
old volume must stay mounted. Plan the split before the upgrade, while the area is still empty.

### Erasure and backup

A deleted file waits for the deletion grace (default 24 h) before the hourly maintenance pass removes it from disk;
for an erasure request that cannot wait, follow the procedure under [Erasure of a photo
original](#erasure-of-a-photo-original). The file name on disk is the sanitized original name — personal data —
so never log or share a volume listing. All four areas are **primary data**: every volume they are bound to
must be in every backup, copied after the database dump and within the grace (see [Backup and
restore](#backup-and-restore)).

## Books: the Library file area and EPUB uploads

Books (issue #527) adds a fourth file area, `LibraryFiles` (`library-files/`), for the files attached to book
records, and accepts EPUB (`application/epub+zip`) on **every** upload surface.

### Release note

- **Bind `LibraryFiles` before you upgrade.** Add `FileStorage__Bindings__LibraryFiles` to **both** `api` and
  `worker` (the base compose file and the AppHost bind it to `primary`, like the other three areas). A host with
  volumes and the feature unbound refuses to start (check B1). Keep the two services identical.
- **One migration**, `AddBooks`: the six book tables, one nullable column `FileMetadata.LibraryStoredObjectId`
  (`RESTRICT` to `StoredObjects`, unique), and `CK_FileMetadata_ExactlyOneSource` re-created to count it. Re-adding
  the check re-validates every `FileMetadata` row, which on MariaDB rebuilds the table under a lock: **time it on a
  production-sized copy** before the upgrade window.
- **Rollback.** `Down()` restores the schema only. Once any file has been uploaded with `area: Library`, dropping the
  column would orphan those files, so roll back by **restoring a backup**, not by `Down()`.
- **A contact credited on a book can no longer be deleted** until it is removed from those books (`409` with a
  `bookCredits` body). `detachBlockingLinks=true` does not remove book credits.
- **EPUB is accepted everywhere files are uploaded.** The server checks the EPUB container signature (a ZIP whose
  first entry is the uncompressed `mimetype` file naming `application/epub+zip`); an EPUB is downloaded, never
  rendered.

### Erasure of a credited contact

Deleting a contact who is the main author or a contributor on any book is refused. An erasure request is handled by
reassigning the main author (or deleting the book) and removing the contributor rows, then deleting the contact.
That needs `books.update` (or `books.delete`), `contacts.read` and `contacts.delete`; in the shipped roles it is an
Admin or Owner task.

## News reader: feeds, what leaves the deployment, and the worker's network

The news reader (issue #470) subscribes to RSS, Atom, RDF and JSON feeds. **Only the worker fetches**: the API makes
no outbound request for news. The worker's news job runs every 30 seconds on every replica — purge, claim due feeds,
fetch, ingest, images, and an hourly maintenance pass — and coordinates replicas through per-feed leases on the
`NewsFeedFetchStates` rows and the shared `NewsHostThrottle` rows, never through process state. It appears on
`/background-jobs` as **News feeds** (`JobType.NewsFeedFetch = 9`), gated on `news-feeds.update`.

### Requirements

- **The `NewsArticles` storage feature must be bound** on both `api` and `worker`. It keeps every article's
  source item, plain text and images on the file store, under `news/<feedId>/<bucket>/<articleId>/<storedObjectId>.<ext>`
  (opaque names: no publisher or user text is ever in a path). The base compose file and the AppHost bind it
  to `primary`; an operator with their own bindings must add `FileStorage__Bindings__NewsArticles`, or
  startup check B1 fails with `UnboundFeature`.
- **The worker image needs ICU** (`icu-libs`, `icu-data-full`) and asserts it at startup
  (`GlobalizationGuard`), as the API does: both map an IDN feed host to the same punycode key.
- **The worker has `stop_grace_period: 30s`**, so a deploy stops its fetches cleanly (leases cleared,
  nothing counted as a crash). Keep it if you write your own compose file.
- **Settings** live under **`/settings` → News**: the default fetch interval, the feed cap, the image and
  text quotas and caps, and `NewsPrivateHostAllowList`, which needs `system-settings.security.update`.
  They reach the worker within the 30 s settings TTL; the allow-list is read uncached, so a revoked grant
  binds on the next fetch.

### What leaves the deployment

Outbound `GET`s go only to the feed hosts users subscribe to and the image hosts those feeds reference. The User-Agent
is fixed and anonymous (`OdysseyNewsReader/1.0`); there are no cookies, no proxy (`HTTPS_PROXY` is never consulted), and
the browser never contacts a publisher. Every connection is vetted at connect time: global unicast only, ports 80 and
443, redirects followed manually (at most five, each re-checked), at least 2 s between requests to one host across all
replicas. A private or loopback feed host is opened only by an entry in `NewsPrivateHostAllowList`, and only for that
feed's own host; the hard-denied ranges (metadata endpoints, link-local, `0/8`, `240/4` and the rest) are never opened.

Feed URLs, including any token in a query string, are stored in plain text in MariaDB and its backups (an accepted
residual). They are never shown to a caller without `news-feeds.update` and are logged by host only. A private feed's
item links and image URLs can carry tokens too; the stored source files keep them.

### Storage, quotas and headroom

- **Headroom.** News writes stop at the upload threshold **plus 1 GiB** of free space on the volume, so news always stops
  before user uploads do. A refused write is a per-feed transient skip and the job reports `StorageUnavailable`.
- **Text and source quota** (`NewsTextStoreMaxGiB`, 20 GiB by default). When it is reached no new article is stored,
  affected feeds report health `QuotaReached` with reason `TextQuota`, and the job card shows a warning line — it is
  **not** a held state. Nothing is deleted. To resume, raise the setting; the hourly maintenance pass clears the reason.
- **Image quota** (`NewsImageStoreMaxGiB`, 50 GiB) and the daily caps defer images rather than failing them; raising the
  quota makes deferred images due again.
- **Inodes.** One folder per article holds 2–7 files. Size a dedicated volume's inode count for that, not only its bytes.
- The per-feed counters and the storage totals are relative increments, recounted hourly; a drift is corrected and logged
  as a Warning.

### Network topology

`docker-compose.yml` now declares four networks of its own (issue #470 §11.3):

| Network | Internal | Members |
|---|---|---|
| `db_api` | yes | `mariadb`, `api` |
| `db_worker` | yes | `mariadb`, `worker` |
| `db_migrations` | yes | `mariadb`, `migrations` |
| `egress` | no | `worker` only |

`api` and `client` stay on `default` for user traffic (Caddy, the client's `/api/` proxy, outbound mail). The dev stack
also keeps `mariadb` on `default`, solely so its loopback port `3307` can be published; the production overlay drops it
(`networks: !override`, Compose 2.24.4 or later — the same version `!reset` already needs).

**What it protects.** The worker runs third-party parsers over whatever publishers send. On exactly `db_worker` and
`egress`, it shares no network with `api` or `client` and cannot reach either directly, and the database is reachable
from each consumer over its own internal network only.

**What it does not.** `egress` is an ordinary outbound network: the control against every other destination — the
host's own LAN, cloud metadata, other containers you publish — is still the news egress address policy above, not this
split. It is defence in depth. The worker still holds the Data Protection key ring read-only (see *Two containers hold
the key ring*). **If you add networks of your own, never attach the worker to one the `api` is on.**

### OPML import and export (issue #508)

The Feeds page can export the feed list as OPML 2.0 (`GET /api/news-feeds/opml`, or `POST …/opml/export` for chosen
feeds) and import an OPML file (`POST /api/news-feeds/opml`). The import makes **no outbound request** — no fetch and no
DNS lookup; the worker fetches the new feeds on its normal schedule, at most four at a time under the per-host throttle,
so a large import is fetched over its first hours rather than in one burst. Two operator-facing facts:

- **The proxies cap the upload too.** `NewsOpmlMaxImportMegabytes` (**`/settings` → News**, 64 MB by default, the
  security claim like every import size cap) is enforced by the API, but under Compose the client container's nginx
  sets `client_max_body_size 65m` (`Odyssey.Client/nginx/templates/default.conf.template`) and Caddy's `request_body`
  matches it, so a value above 64 MB has no effect there until both are raised — the same chain as *Upload size* above.
  `NewsOpmlMaxImportEntries` (100,000, the ordinary claim) can only be lowered.
- **One OPML import at a time is enforced per API process**, not deployment-wide, and the upload must arrive at
  64 KiB/s or faster after a 10 s grace (HTTP/1.1; on HTTP/2 the server-wide rate applies). Odyssey runs one API
  replica; if the API ever scales out, the feed-limit bound under concurrent imports holds per replica, not globally.

An export carries every feed's fetch URL, so it is gated on `news-feeds.update`, the claim that already reads them, and
is sent `Cache-Control: no-store`. A downloaded file keeps any token a private feed URL embeds after the caller's claim
is revoked — an accepted residual for a single-household instance.

### Podcasts (issue #556)

A feed can be marked a **podcast** (`NewsFeeds.Kind`). Its items become episodes on their own page, and the worker can
store their audio on the server so a household member downloads it to a device from Odyssey rather than from the
publisher.

**Release note.**

- **Two new storage features**, `PodcastAudio` (`podcast-audio/<feedId>/<xx>/<yy>/…`) and `PodcastArtwork`
  (`podcast-artwork/<feedId>/…`), both opaque-named and graced. Both compose files and the AppHost bind them to
  `primary` for `api` and `worker`. **An operator with their own bindings must add `FileStorage__Bindings__PodcastAudio`
  and `FileStorage__Bindings__PodcastArtwork` to both containers**, or startup check B1 fails with `UnboundFeature`.
- **Two new background jobs**: *Podcast downloads* (`JobType.PodcastAudioDownload = 11`, no manual run, detail under
  `news.read`) and *Podcast audio clean-up* (`JobType.PodcastAudioRetention = 12`, a daily pass; a manual run needs
  `news-feeds.update`).
- **Two new claims**, `podcast-audio.create` (queue a server download) and `podcast-audio.delete` (remove stored audio),
  held by Admin, Owner and User — not Guest. They reach live sessions at the next security-stamp check (≤ 1 minute).
- **Four settings** under **`/settings` → Podcasts**: the audio store limit (`PodcastAudioStoreMaxGiB`, 50 GiB), the
  per-episode size limit (`PodcastAudioMaxEpisodeMb`, 1,024 MB, never above `FileStorage:MaxFileSizeBytes`), and the
  auto-download daily and back-catalogue caps (5 each).
- **The client nginx gains one location**, `/api/api/podcast-episodes/<id>/audio/content`, unbuffered with an hour's
  send timeout. Keep it in your own proxy config: a buffering proxy makes a slow or aborted download look complete to
  the API, which then stamps the episode as downloaded and starts its auto-delete countdown.
- **`PUT /api/news-feeds/{id}` now requires `kind`, `autoDownload`, `autoDeleteEnabled` and `autoDeleteAfterDays`**
  (a full replacement, like `downloadImages`); a body without them is a `400`. The shipped client sends them. A script
  or a browser still running a cached client from before this release must be refreshed.
- **Rolling back** past this release drops every podcast table; the podcast `StoredObjects` rows remain and log the B4
  Critical. Switch every podcast feed back to an article feed (or delete it) and wait out the deletion grace first.

**What leaves the deployment.** The worker fetches the audio enclosure, the show and episode artwork and Podcasting 2.0
chapter files, through the same egress policy as feeds: connect-time vetting, no proxy, the fixed anonymous User-Agent,
no `Referer`, no `Authorization`, no cookies. Audio is requested with `Accept-Encoding: identity`; a compressed response
is refused. Audio redirects may chain up to **ten** hops (tracking prefixes routinely chain three to six); every hop is
re-checked. Publisher and tracking-prefix hosts learn the server's IP and when it downloaded, and auto-download makes
that periodic. The browser never contacts them. Enclosure URLs, which private feeds use to carry tokens, are never
returned by the API or logged — logs name hosts and episode ids only.

**Bounds.** At most two downloads per worker process and one per feed and per host across all replicas (generated
unique guard columns on MariaDB). A download must send its headers within 30 s, may not stall for 60 s, must average
32 KiB/s over each two-minute window after its first minute, and gets `min(60 min, 2 min + size ÷ 32 KiB/s)` in all.
The chapter-marker reader reads at most 16 MiB of an ID3 head tag or 4 MiB of an MP4 `moov`, with a two-second
deadline. Memory: two 80 KiB download buffers plus the chapter window, on top of the news job's peak. A process's two downloads run inside one run of the job, so a long download holds that
process's next run (and its next claim) until it ends — up to the hour's total deadline; other replicas keep claiming.

**Storage.** Podcast files share the news headroom threshold (upload threshold plus 1 GiB), and a download also
reserves its expected size against both the audio quota and the free space, under a lock on the storage totals, so two
replicas cannot both pass a check that only one of them fits. The quota counts stored audio, audio released but still
in the deletion grace, and in-flight reservations; removing audio frees quota only when the store deletes the file. A
full quota or low headroom puts a download in `Deferred` and retries it hourly without spending an attempt. Artwork
counts inside the **image** quota. The hourly maintenance pass recounts all four podcast totals from the rows.

**Retention.** Archiving a podcast feed keeps its stored audio and still serves it. Switching a feed back to articles,
or deleting it, releases its audio within a tick or two. The auto-delete rule (per feed, off by default) releases audio
only after a household member downloaded the whole file to a device and the chosen number of days has passed since that
download or since the rule last changed, whichever is later. Audio never downloaded is never auto-deleted.

## Security checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production` (Swagger off; set in the overlay) — and **not**
      `Testing`, which the API refuses to start under; see the section above.
- [ ] No demo data: if this host ever ran the base `docker compose up`, its `mariadb_data`
      volume was destroyed with `docker compose down -v` before the production overlay came up.
- [ ] No host port mappings for `api`/`mariadb`/`client` — only Caddy's 80/443 are exposed.
- [ ] **Private deployments only:** `CADDY_BIND=127.0.0.1:` (with the trailing colon) is set, and
      `docker compose … config` shows `host_ip: 127.0.0.1` on both of Caddy's ports. Confirm from
      another machine that the host does not answer on 443.
- [ ] **Private deployments only:** `ODYSSEY_DOMAIN` is a `*.localhost` name, not bare `localhost`
      — see [the HSTS note](#use-odysseylocalhost-never-bare-localhost).
- [ ] Strong, unique `MARIADB_ROOT_PASSWORD` and `MARIADB_PASSWORD`.
- [ ] `.env.prod` is not committed (the `.env*` gitignore rule covers it) and is `chmod 600`.
- [ ] TLS verified — `https://your-domain` shows a valid Let's Encrypt cert.
- [ ] SMTP configured so account confirmation/reset emails actually send.
- [ ] `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` set to real values (the example file
      ships them empty).
- [ ] The seeded admin's password changed at first sign-in (the app forces this; confirm it happened,
      since the one-time value is still sitting in `.env.prod`).
- [ ] `dataprotection_keys` is backed up, and stored **separately** from the database dump — it is
      secret-bearing once any credential is stored under System settings → Credentials.
- [ ] The API started cleanly, i.e. the keys directory is writable (see [A keys volume the API cannot write needs a one-time `chown`](#a-keys-volume-the-api-cannot-write-needs-a-one-time-chown)).
- [ ] The API and the worker started cleanly with every file volume verified (`docker compose logs api
      worker | grep "File storage"` shows no `check … failed`), and **System settings → Storage**
      reports each volume *Available* — see [File storage](#file-storage-filesystem-volumes).
- [ ] File volumes are mounted into `api` and `worker` **only**, on a local filesystem (ext4, XFS,
      btrfs), with `noexec,nosuid,nodev` for a bind mount, and the host disk is encrypted (LUKS) — a copied
      volume is directly browsable.
- [ ] Each file volume is backed up after the database dump, within the deletion grace period.
- [ ] The location map's `MAP_ALLOWED_ORIGINS` lists only dedicated tile hosts, the same value reaches
      the `api` and the `client`, and your privacy notice names the tile host — or the map is switched
      off — see [Location map](#location-map-what-leaves-the-deployment-and-how-to-switch-it-off).
- [ ] Roles are assigned on the understanding that **any** of them can read every contact's aliases
      (maiden and former names included), middle names and dates of death, and export the lot — see
      [the section above](#who-can-read-contact-personal-data).
