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
     [the release note below](#release-note-five-credentials-moved-into-the-encrypted-secret-store-issue-445).
     `.env.prod.example` marks each retired variable in place.

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

Both proxies currently sit at `65MB` for a `67108864`-byte (64 MB) ceiling plus 1 MB of envelope
headroom. Lowering the cap needs no deploy-time change at all.

> `FileStorage:MaxFileSizeBytes` is **not** retired — it stays as the transport ceiling above, read by
> the API at startup. The admin-editable cap is a separate value that seeds at 64 MB, so if you raise
> the ceiling here, raise the cap at `/settings` too or users still hit the old number.

### A fourth, unrelated size limit: photo metadata reads

`System Settings → Photos → Metadata read size` is **not** part of the chain above — it bounds how much
of an already-accepted image is read back out of the database to extract EXIF/IPTC/XMP, not what an
upload may contain. It has its own ceiling, and that ceiling is a **compiled assumption about MariaDB's
default `max_allowed_packet` (16 MiB)**, which this repository pins nowhere: not in
`docker-compose.yml`, not in `docker/mariadb/init/01-init.sql`.

Two consequences worth knowing before you touch either number:

- **Raising `max_allowed_packet` buys you nothing on its own.** The setting still stops at 16 MB,
  because that is where the compiled bound is. Raising the bound is a code change.
- **Lowering `max_allowed_packet` below the setting is safe, not fatal.** The prefix read fails, and
  metadata extraction is *skipped* — the photo is still stored, just without extracted metadata. That
  fail-soft path is what makes the 16 MB bound defensible rather than merely smaller than a larger one.

Note also that extraction materialises a full byte array of this size per photo, so it is a per-upload
memory multiplier, not only a database concern. The row carries an advisory above its shipped 8 MB
saying so.

## Tunable policy lives in System Settings, not in configuration

A growing set of runtime policy — 59 values as of issue #439 — is admin-editable at **`/settings`** and
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

### Release note: fetched exchange rates are stored in one direction (issue #472)

This ships in **two releases, and their order is the whole safety argument.** Nothing in the code enforces
it, so check it before upgrading.

**Release 1 (PR #478) — readers.** Every conversion, status and `/latest` read now uses a fetched
`X → NOK` row as `1 / Rate` for `NOK → X`; manual rates are never inverted. The sync still stores both
directions, and on a tie the stored row wins, so results are unchanged. It adds two migrations:
`CK_ExchangeRates_RatePositive` and a `(From, To, Provider, AsOf)` index. **If any `Rate <= 0` row
exists, the migration service stops before migrating** and names up to 20 row ids; correct each rate
or delete the row, then start it again. Also new: `GET /api/exchange-rates/resolved`, and `/latest`
answers a malformed currency code with `400` (it used to be a `404` echoing the input). An inverted
`/latest` response carries the id and dates of the opposite pair's stored row.

**Release 2 (PR #480) — storage.** The sync stores only the published `X → NOK` row, and the
`StoreFetchedRatesInPublishedDirectionOnly` migration deletes every fetched `NOK → X` row that has its
fetched `X → NOK` partner on the same date. Manual rows and partnerless fetched rows are kept.

1. **Deploy release 1 everywhere first.** A release-1 reader is what keeps `NOK → X` conversions
   working once those rows are gone. An API or worker still on an older release would find no
   `NOK → X` rate after the migration.
2. **Take a database backup before upgrading to release 2.** This is a data migration. Its `Down`
   re-derives the deleted rows from their partners, so it is **approximate**: a rate can differ in
   the last decimal where the published value needed more than eight. The backup is the exact restore.
3. **Stop the API and the worker, run the migration, then start them.** A release-1 worker left running
   through the migration may write `NOK → X` rows again; they are harmless (readers prefer the stored
   row on a tie, which is today's value) but are not removed until the next purge of that currency.
4. **Expect the numbers to halve:** a sync run's inserted rows, and the purge preview's fetched-row
   count. The Exchange rates list stops showing fetched `NOK → X` lines; the Inverse column already
   shows the reciprocal.
5. **A warning to know about:** `Exchange-rate sync stored N rounded XXX rate(s) between … and …`
   means Norges Bank published a value needing more than eight decimals, which it has not done so far.
   The stored rate is rounded, so its reciprocal is off in the last place.

### Release note: file-analysis matching sends more, and the vocabulary cap is gone (issue #433)

- **The match vocabulary cap is retired.** The `FileAnalysisMatchMaxVocabulary` setting (default 500) no
  longer exists, and matching is never skipped for vocabulary size: every active contact and tag is sent.
  Rolling the migration back re-inserts the row at **500**, not at an administrator's previous value.
- **Matching sends contact aliases and tag descriptions** for analyses started after the upgrade. Alias
  labels, notes and organisation numbers are still never sent. An analysis started before the upgrade
  keeps matching on names only for its whole life.
- **Users consent again once.** The consent disclosure now lists the data fields sent and whether
  administrator guidance is in force, and its version covers both.
- **Two new settings, `FileAnalysisExtractionPromptAddition` and `FileAnalysisMatchPromptAddition`**,
  hold administrator guidance (up to 4000 characters each, empty by default) that is added to the
  extraction and match prompts. Each change is logged with the acting user, the length and a digest,
  never the text. Turning either on or off makes users consent again; turning **match** guidance on or
  off, or changing the processor, region, lawful basis or privacy-notice URL, also refuses matches on
  analyses that are already open (`409`, "start a new analysis").
- **Two new deploy-time limits**, per user: `RateLimiting:FileAnalysisAnalyze` (default 10 per hour) and
  `RateLimiting:FileAnalysisMatch` (default 30 per hour). A household importing many statements at once
  may want to raise the analyze limit. Like every `RateLimiting:*` key they are configuration, not
  settings, because the limiter is cached per user.
- **A payload budget** of 200 000 characters bounds each match request: over it, aliases are left out
  for that run, then tag descriptions. Names are always sent, and are never shed: a household whose
  names alone exceed the model's context gets a `Failed` match (review the candidates by hand), not a
  partial one.
- **Wire change for API consumers.** `fileAnalysisMatchMaxVocabulary` is gone from the system-settings
  DTOs, and `maxVocabulary` from the file-analysis payloads. The web client in this release no longer
  reads either; a third-party consumer that did must drop them. Matches refused by the new limits
  answer `429` with `Retry-After`, and the destination refusal answers `409` with
  `code: file_analysis_destination_changed`.

### Release note: three `FileAnalysis` keys retired from `appsettings.json`

`FileAnalysis:MaxTokens`, `FileAnalysis:Match:MaxVocabulary` and `FileAnalysis:Match:TimeoutSeconds` are
database-backed as of issue #434 and no longer read from configuration. (`MaxVocabulary` was later retired altogether by
issue #433; see the note above.)

Their environment variables (`FILE_ANALYSIS_MAX_TOKENS`, `FILE_ANALYSIS_MATCH_MAX_VOCABULARY`,
`FILE_ANALYSIS_MATCH_TIMEOUT_SECONDS`, and the `Aspire:` keys) are **gone**, and nothing carries a
configured value into the store: the settings start at their shipped defaults (8096 / 500 / 60) and an
administrator sets them at `/settings`. A carry-over step existed briefly and was removed once it was
established that no deployment had ever run a release it could upgrade from — see CLAUDE.md, which
records what a future one would have to get right if that changes.

`FileAnalysis:TimeoutSeconds` is **not** affected — it stays in configuration, because it is consumed
once at startup by the HTTP resilience handler and a runtime value could never reach a live pipeline.

### Release note: the file-analysis switch, model and base URL are settings now

`FileAnalysis:Enabled`, `FileAnalysis:Model` and `FileAnalysis:BaseUrl` are database-backed as of issue
#439, editable at **`/settings` → File analysis** by an administrator holding
`system-settings.security.update`. Every change to any of the three is written to the audit log.

As above, their environment variables (`FILE_ANALYSIS_ENABLED`, `FILE_ANALYSIS_MODEL`,
`FILE_ANALYSIS_BASE_URL`, and the `Aspire:` keys) are **gone** and nothing carries a configured value
across. A deployment starts at the shipped defaults — **analysis off**, `claude-sonnet-5`,
`https://api.anthropic.com` — and an administrator turns it on at `/settings`.

That default ordering is deliberate and worth keeping in mind if a carry-over step is ever
reintroduced: a deployment that silently *starts* transferring documents to a third party would be far
worse than one that silently stops, so the switch must fail to **off**.

Four operational notes:

- **`FileAnalysis:ApiKey` moved too, in issue #445** — into the *encrypted secret store*, not into
  these plaintext settings. See the release note below. The consequence stated on the settings row is
  unchanged: the stored key is attached to the outbound client, so **repointing the base URL sends that
  key to the new host**. Repoint it only to a host you control or trust. The base URL must be an
  absolute `https://` address with **no path** — the provider appends `/v1/messages` itself — and no
  credentials, query or fragment; all of those are rejected on save, and a value planted by a restore
  is rejected on read as well, which makes analysis refuse rather than fall back to
  `api.anthropic.com`.
- **The switch is honoured on the next request**, not up to 30 seconds later: it is read live on every
  file-analysis call rather than from the 30-second settings cache. An operator instructed to stop
  transfers can stop them.
- **Redirects are not followed.** The outbound client runs with `AllowAutoRedirect = false`; a `3xx`
  from the configured host is recorded as a provider error. .NET strips only `Authorization` across
  origins, so a custom `x-api-key` header would otherwise survive a redirect — along with the document
  on a `307`/`308`. Configure a gateway at its final address.
- **The circuit breaker is per typed client, not per host.** After repointing the base URL, a breaker
  opened by failures against the *previous* host stays open for the rest of its sampling window
  (`2 x FileAnalysis:TimeoutSeconds`, so 240s at the shipped 120s). Pre-existing behaviour, but now
  reachable by an admin action: wait it out, or restart the worker (which sends the document since issue
  #444; the API keeps a breaker of its own for the match step).

### Release note: statement analysis runs in the worker (issue #444)

Extraction moved out of the API request and into `Odyssey.Worker`. What an operator needs to know:

- **The worker now needs egress to the analysis provider** (the base URL host, `api.anthropic.com` by
  default). The API still needs it for the match step. Neither compose file restricts egress today; a
  deployment that firewalls containers must open it for both.
- **`POST /api/accounts/{id}/files/{fileId}/analyze` answers `202 Accepted`** with a `Location` to
  `GET /api/file-analysis/{jobId}`, which a client polls (it sends `Retry-After: 2` while the job is open and
  `Cache-Control: no-store` always). A second request while a job is open on the file is a `409` with
  `code: analysis_in_progress` and the open job's `analysisJobId`. Third-party consumers that read
  candidates from the old `200` body must poll instead.
- **A statement above 32 MiB is refused with `422`** (`code: file_too_large_for_analysis`, `limitBytes`),
  a fixed ceiling, before anything is queued.
- **`FileAnalysis:PromptVersion` and `FileAnalysis:PromptTemplatePath` are retired.** The prompt is embedded
  in the build and every job records its SHA-256; a configured value is ignored.
- **`FileAnalysis:TimeoutSeconds` must be 10–140** and is validated at startup in both the API and the
  worker; keep the two equal (the shipped `appsettings.json` files are).
- **An unset API key now refuses before sending.** A gateway that authenticates by network position
  rather than by key still needs *a* key stored — any placeholder value — or every job fails with
  `provider_credential_error` and nothing is sent.
- **Nothing is ever re-sent automatically.** A worker that dies mid-call leaves the job
  `Failed / interrupted`, reported to the user as "possibly sent"; a job no worker claims within 15
  minutes fails as `not_started`, "not sent". Jobs left `Running` by the old synchronous path when you
  upgrade are failed the same way by the first sweep.
- The run log at `/background-jobs` gains a **File analysis** entry (counts of completed, failed and
  refused analyses). It has no *Run now*.

### Release note: five credentials moved into the encrypted secret store (issue #445)

`FileAnalysis:ApiKey`, `Email:Username`, `Email:Password`, `Email:RecipientHashKey` and
`Legal:PseudonymizationSecret` are no longer read from configuration. They live in the encrypted
`SystemSettingSecrets` table and are entered at **`/settings` → Credentials** by an administrator
holding `system-settings.security.update`. Each write is audited; no value is ever shown again.

**Every one of them must be entered by hand.** A secret is deliberately
**not adopted from configuration** — no mechanism reads one out of the environment and writes it to
the store, and none should be added: that would require the plaintext to still be present in the environment, which is most
of what moving them was for, and would leave the row owned by configuration with no visible owner. So
there is a defined gap between deploying and an administrator entering each value.

| Credential | While it is unset, after the upgrade | Kind |
|---|---|---|
| `FileAnalysis:ApiKey` | Every analysis job fails and is recorded as a credential failure. Nothing is transferred and nothing is lost. | rotatable |
| `Email:Username` + `Email:Password` | **Transactional mail is not sent.** With *both* unset the relay is used unauthenticated (which is what an empty `EMAIL_USERNAME` did before); with only one stored, or either unreadable, the send is logged and skipped rather than attempted with half a credential. | rotatable |
| `Email:RecipientHashKey` | Unchanged and healthy: the send throttle generates a per-process key, so its recipient digests correlate within one process and not across a restart. | derivation |
| `Legal:PseudonymizationSecret` | Account deletion cannot pseudonymise consent records. Outside Production the fixed development value is substituted; in Production the deletion fails, inside its own transaction, leaving the acceptance rows intact. | derivation |

Read this before upgrading:

- **Copy `LEGAL_PSEUDONYMIZATION_SECRET` out of your environment first, and enter that same value.** It
  is a *derivation* key: if consent rows were already pseudonymised under it, a different value leaves
  those rows permanently un-re-derivable — the property GDPR Art. 7(1) consent attribution depends on.
  There is no provider to re-issue it from and no way to recover it afterwards.
- **Production no longer fails to start without it.** The `ValidateOnStart` gate is gone, deliberately:
  a credential an administrator enters through the UI cannot be a precondition for the UI coming up.
  The failure moved to the first account deletion, with the remedy in the message.
- **If you set any of the five by editing `Odyssey.Api/appsettings.json` directly, that value is
  simply gone** — the keys are no longer bound at all. Note them down before upgrading.
- **An unreadable row never falls back to a configured value, for any of the five.** There is nothing
  to fall back to: the configuration properties were deleted in the same change. An unreadable
  credential means the Data Protection key ring changed or was lost; restore it, or clear the row and
  enter the credential again. `/settings` shows such a row in coral, and the page header raises a
  **Credentials** signal naming what is broken.
- **The keys volume is now load-bearing for these credentials as well as for sessions.** The database
  backup alone is no longer sufficient — see [The Data Protection keys volume](#the-data-protection-keys-volume)
  and back both up together.

### Mail is configured after first sign-in

`Email:SmtpHost`, `Email:SmtpPort`, `Email:UseStartTls` and `Email:ClientBaseUrl` used to stay in
configuration, and issue #8 moved all four into the settings store. They are edited at **`/settings` →
Email**, gated on `system-settings.security.update`, and every change binds on the **next send** — no
restart, no cache wait. Their environment variables and the whole `Email` configuration section were
**deleted**, not deprecated: nothing reads `Email:` from configuration any more, and a value set there
is not a fallback.

**Startup validation is gone.** The Production `ValidateOnStart` gate on `Email:SmtpHost` could not
survive the move — a value entered through the settings UI cannot be a precondition for that UI coming
up — so the failure moved from startup to the first send, which logs and skips. This is the identical
trade issue #445 made when `Legal:PseudonymizationSecret` lost its own gate.

The consequence, and it is the part worth planning around:

> A new deployment sends **no mail at all** until an administrator sets an SMTP host. You sign in with
> the one-time `BOOTSTRAP_ADMIN_PASSWORD` and then configure SMTP. **If that password is lost during
> that window there is no self-service recovery** — the forgot-password flow needs working mail — and
> the account must be repaired directly in the database.

So configure mail immediately after your first sign-in, before blanking
`BOOTSTRAP_ADMIN_PASSWORD`, and then send yourself a password reset. Nothing short of a real send
checks the whole path, relay credential included, and nothing checks it at startup any more.

While no host is set, the System settings page reports **"Transactional mail is not configured"** in
its page header, at Information severity, with a Fix action jumping to the row. That covers an
administrator who is looking at the page; it does not cover an operator who deploys and never visits
`/settings`, which is why this section exists.

**Changing the SMTP host or port, or turning STARTTLS off, clears the stored SMTP username and password** —
in the same database transaction as the change itself, so the two land together or neither does. The
threat the old Non-Goal was protecting against is real: the sender connects and *then* authenticates,
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

### Release note: user profile pictures (issue #94)

A signed-in user can attach, replace and remove **one picture of themselves** from `/account`. It then
stands in for their initials wherever the application already identifies them: the account page header,
and the administrator's user list and detail panel. Four things an operator should know before
upgrading.

**A new permission claim, `profile-images.read`, and revoking it from Guest is the supported way to
narrow who sees colleagues' pictures.** It is granted to **every** role out of the box, so its effective
reach is "any authenticated caller" — what it buys is a *revocation lever that exists before release*
rather than narrower access today. It needs **no migration**: `RoleClaimSeeder` reconciles
`AspNetRoleClaims` at runtime and applies removals as well as additions. A revocation lever nobody knows
about is not a lever, which is why it is written down here.

**Pictures stay blank for already-signed-in users until they sign out and back in.** Claim values are
baked into the auth cookie at sign-in, so a session that predates this deploy does not hold the new
claim and sees the initials monogram. It is a one-time, self-healing degradation to a decorative
surface. On `/account` the picture *is* the feature rather than decoration, so for that window the
controls are disabled with "Sign out and back in to manage your profile picture" — note that the same
disable covers **Remove**, so the self-service erasure control is briefly unavailable too. One sign-out
clears it.

**Disabling an account also hides its picture — from administrators as well.** The read endpoint `404`s
an administratively disabled subject, which is what makes "disable the account" a proportionate remedy
for an abusive upload (there is deliberately no administrator write path, so an admin can neither
replace nor remove someone else's picture). The row survives, but only direct database access reaches
it. **So capture the evidence before disabling**, not after. Note this applies to an *administrative*
disable only — a user locked out by five mistyped passwords keeps their picture, deliberately: hiding
it would turn the endpoint into a `200 → 404 → 200` confirmation channel for failed-login lockouts.

**`Down` on the `AddUserProfileImage` migration destroys the stored images.** The schema is reversible;
the data is not. The migration is otherwise purely additive — two new tables, one unique index, two
cascade foreign keys, no existing table altered and no rows seeded.

Three new `RateLimiting:*` sections are shipped with defaults and stay in deploy-time configuration
(`ProfileImageWrite`, `ProfileImageDelete`, `ProfileImageRead`). The write and delete budgets are
**separate on purpose** — sharing one would mean a user who had exhausted their upload budget could not
*remove* their picture, which throttles a GDPR Art. 17 control. The read budget is deliberately generous:
a rejected read is invisible to the user (the avatar silently falls back to the monogram), so a tight
limit produces a silent defect rather than an error anyone reports.

### Release note: contract signature dates are backfilled, and the values are not evidence (issue #145)

A contract now carries two signature stamps, **Ready** (marked ready for signature) and **Signed**
(signed by all parties), and they fold into the derived contract status as two new states, **Draft**
and **Ready**. An unsigned contract is on file but not in force: it is excluded from the contracts run
rate, its by-type cost split and the upcoming charges. Three things an operator should know before
upgrading.

**The `AddContractSignatureDates` migration writes a FABRICATED signature timestamp onto every existing
contract row, and no such value is a statement of fact.** A field that looks like evidence and is not
is a provenance defect, so it is recorded here rather than left to be discovered: if you need to know
when an agreement was really signed, the backfilled value does not tell you. The value is derived from
each row's *own* dates — `LEAST(COALESCE(StartDate, CompletionDate, CreatedAtUtc), CreatedAtUtc)`, i.e.
*signed no later than the day its term began, and no later than the day its row was created* — rather
than one shared clock reading stamped across the table, so the residual is a per-row approximation
instead of a table-wide assertion. The clamp is what keeps a contract whose term starts in the future
from being stamped as signed in the future, which the write path refuses outright.

**Leaving the columns null would not have been the safer alternative.** The derivation reads a null
`Signed` as unsigned, so an un-backfilled upgrade would flip *every* contract in the database to
`Draft` and empty the run rate and the upcoming charges overnight. Backfilling is what makes the change
invisible until someone deliberately records a draft: no existing contract changes status across the
upgrade, and the summary body is identical except that `countsByStatus` gains `draft: 0` and
`ready: 0`.

**No new permission claim and no new configuration.** The two fields ride the existing
`contracts.read` / `.create` / `.update` claims, so **no sign-out/sign-in is required on deploy** —
nothing baked into an existing auth cookie is affected. Neither column is indexed (the status is
derived in memory after projection, so neither is ever a SQL predicate), and neither carries a
user-attribution column: a `Ready`/`Signed` transition is recorded as one structured `Information` log
line naming the contract, which stamp changed, whether it was set or cleared, and the acting user.
`Down` drops both columns; the backfilled values are not recoverable, which is correct — they were
fabricated on the way up.

One behaviour change for anyone driving the API directly: **`GET /api/contracts?sortBy=Status` now
orders by a lifecycle rank** (`Draft → Ready → Upcoming → Active → Paused → Expired → Archived`)
rather than the enum ordinal, so `Expired` and `Archived` now sort *after* `Active`. The persisted and
wire ordinals are unchanged — `Draft` and `Ready` are appended at 5 and 6 — and this affects display
order only.

### Release note: contract terms now carry a money direction (issue #159)

Each priced contract term now says **which way the money moves**, from the household's perspective —
`Outgoing` or `Incoming` — so a landlord's rent is no longer summed into "what the file costs to run"
and an employment contract's salary no longer reads as an expense. Three things an operator should
know.

**The `AddTermDirection` migration is additive and behaviour-preserving, with no backfill statement.**
It adds one `NOT NULL DEFAULT 0` `int` column to `Terms`; the default writes `Outgoing` into every
existing row, which is what every existing term already meant. On a file whose contracts only record
costs, `GET /api/contracts/summary` returns a **byte-identical** body before and after, except for the
new fields being null or empty. No index, no seeded row, no data migration. `Down` drops the column,
and the directions recorded after the upgrade are not recoverable from it.

**`runRate.monthly` / `.yearly` / `.byType` keep their names *and* their meaning — they are now
explicitly the OUTGOING side.** The incoming side arrives under new names (`incomingMonthly`,
`incomingYearly`, `incomingByType`), the net under `netMonthly` / `netYearly`, and the incoming
movements in a new `upcomingReceipts` list beside `upcomingCharges`. A consumer reading the existing
fields today is correct tomorrow. Two rules worth knowing when reading the payload: no gross ever
mixes directions, and the net is the only signed figure — it is `(incoming ?? 0) − (outgoing ?? 0)`,
computed from the unrounded sums, and null only when **both** sides are.

**The contracts header now shows four run-rate tiles instead of two.** "Monthly run rate" and
"Yearly run rate" are renamed **"Monthly out" / "Yearly out"** and joined by **"Monthly in" /
"Yearly in"**; the two by-type tiles become **"Monthly net by type" / "Yearly net by type"**, each
closing with a ruled **Net** row. The figures behind the two "out" tiles are the same numbers the old
two showed — the rename is because the figure is now one side of two, and a name that stayed would
read as a total. The signal panel gains a **Next receipts** group beside Next charges. Nothing here
needs an operator action; it is noted so the change is not mistaken for a defect.

**`ContractMaxSummaryCharges` changes meaning without changing its value or bound.** It now caps the
outgoing and incoming movement lists **separately**, so a file with many charges cannot starve the
receipts — and the worst-case row count for that part of the payload doubles. The setting's own
description on **System settings → Contracts** says so; nothing about it needs changing on upgrade.

**No new permission claim and no new configuration.** Direction rides the existing `contracts.read` /
`.update` and `accounts.terms.write` claims, so **no sign-out/sign-in is required on deploy**. A new
`400` exists on the write path: `Incoming` on an *account*-owned term — no account surface reads a
direction yet, so accepting one there would
record a fact the product then contradicts. And note that `PUT …/terms/{termId}` is a **full replace**:
omitting `direction` resets the term to `Outgoing`, exactly as omitting `label` or `anchorDate` already
clears those. Every read path returns the field, so a read-modify-write round trip carries it back
unchanged.

### Release note: rate term kinds folded into labelled terms, and `TermKind` removed

Two migrations run in order. `FoldRateTermKindsIntoFee` rewrites every `InterestRate`,
`ExpectedReturn` and unrecognised term as an ordinary labelled term — labelled `Interest rate`,
`Expected return` or `Unspecified` where it had no label — keeping its value, unit and dates.
`RemoveTermKind` then drops the `Terms.TermKind` column; a term's series is now its label alone.
If an owner already had a term labelled `Interest rate` or `Expected return`, the converted rate joins
that history rather than forking a second one. No data is lost, and the down migrations are
best-effort (they restore a kind only from the exact label the fold wrote).

On the wire, `termKind` is gone from every term request and response, from the data export, and as
the `?kind=` filter on both history endpoints; `label` is now required on every term.
`ExistingAccount.currentInterestRate` / `currentInterestRateKind` are removed. The account row's
headline rate, the rate step chart and the "Interest charged" tint are withdrawn for now — a former
rate shows as a term like any other.

### Release note: account terms moved onto contracts (issue #190)

**Take a database backup before upgrading.** The `MoveAccountTermsToContracts` migration moves data
and then drops a column, it has **no immutable record** of what it did (a migration step cannot write
to the application log, and its working table is dropped at the end), and its `Down` restores the
schema only. The backup is the recovery path.

**What the migration does.** Every term recorded directly on an account is moved, row for row and with
the same `TermId`, onto a contract the account takes part in. The expected contract type is `Deposit`
for an asset account, `Loan` for a liability and `Other` for an unclassified one. If the account is a
party (in any role) on exactly one contract of that type, and none of the moving terms shares a
`(label, effective date)` with a term already on it, that contract is reused untouched. Otherwise a
new contract is created from the account — name, description, account number as reference, opened and
closed dates, archived state — **unsigned (Draft)**, with the account as `Object` and its custodian, if
any, as `Custodian` / `Lender` / `Other`. Accounts without terms are not touched.

**What to review afterwards.** Each moved account gets one system event on its contract, titled
*Terms migrated from account "…"*, listing the codes below that apply. It is an ordinary, editable
event — a to-do list, not an audit record.

| Code | Meaning | What to do |
|---|---|---|
| A1 | The contract was created by the migration | Review it; mark it Ready and Signed with the real dates. Until signed it is Draft and **excluded from the run rate**. |
| A2 | Two or more matching contracts existed | Move the terms to the right one and delete the created contract, or keep it. |
| A3 | The account is a party on a contract of another type | Check whether the terms belong there. |
| A4 | An unclassified account became an `Other` contract | Choose the real contract type (party roles may need changing first). |
| A5 | The account had no custodian | Add the institution as `Custodian` / `Lender`. |
| A6 | The reference number was copied from the account number | Replace it with the agreement's reference, if different. |
| A7 | The account's closed date precedes its opened date | Set the contract's end date. |
| A8 | A percentage term on a Deposit was flipped to `Incoming` | Verify — a negative rate or a percentage **fee** should stay `Outgoing`. |
| A9 | The one matching contract already had a series with the same label and date, so a new contract was created | Decide which contract the series belongs on; move or delete the duplicate. |
| A10 | The reused contract now exceeds the per-contract term cap | New terms are refused (`422`) until it is below the cap; edits still work. |
| A11 | The reused contract is archived | Its terms are hidden with it; unarchive or move them. |
| A12 | An amount's currency was filled from the account, and that currency is inactive | The term cannot be re-saved until the currency is active or changed. |
| A13 | The account also has value estimates | Informational — estimates stay on the account. |
| A14 | Periodic amount terms now sit on a Deposit / Loan | Once signed they **count in the contracts run rate**; confirm the amounts. |
| A15 | An existing contract was reused | The account keeps its existing role and no custodian party is added; add parties if wanted. |

**What is lost.**

- **L1** — An amount term with no currency used to follow the account's currency, including later
  changes; it is now frozen to the account currency at migration time.
- **L2** — The original direction of a flipped percentage term (A8) survives only in the event text.
- **L3 — Deleting an account no longer deletes its terms.** It removes only the account's party row;
  the contract and its terms remain and follow the contract's lifecycle. Orphaned Draft contracts left
  by an account deletion are expected and are not pruned automatically.
- **L4** — Guest loses read access to these terms: they are now read under `contracts.read`, which
  Guest does not hold. No role gains access.
- **L5** — Per-term history events: the move writes one summary event per account, not one per term.
- **L6** — `ExistingAccount.termCount` and `.currentTerms` are removed from the API, and with them the
  account row's term badge, Current-band term tiles, Terms section and *New term* menu item.
- **L7** — `TermExport.accountId` is removed from the admin data export.

**API and permissions.** The five `/api/accounts/{accountId}/terms…` routes are gone and answer `404`;
`/api/contracts/{id}/terms…` is the only term API. `ExistingTerm` loses `accountId` and its
`contractId` is never null. The account-term read/write claim pair is deleted; `RoleClaimSeeder` revokes
its `AspNetRoleClaims` rows at the next start with no migration. Cookies issued before the upgrade
still carry the dead values until sign-out/sign-in, which is harmless — no endpoint checks them.

**If the migration is interrupted.** An interruption before the schema change is safe to re-run: every
decision it takes is staged once and reused. An interruption *during* the final column drop is not
covered by the start-up drift check; repair it by hand per
[`migration-history-drift.md`](migration-history-drift.md).

### Release note: currencies in use can no longer be deleted (issue #241)

**Take a database backup before upgrading.** `AddCurrencyForeignKeys` writes data, and its `Down`
restores the schema only.

**What the migration does.** It adds a `RESTRICT` foreign key to `Currencies` from every
currency-code column: accounts, transactions, budgets, tax statements, properties, account and property
estimates, and contract terms. Before this release a currency could be deleted while those rows still
named it, and rows like that would make adding the keys fail. So first, every code that rows use but
`Currencies` no longer holds is **restored** as an active currency named
`Restored currency XYZ (review)` with two minor units. No referencing row is changed or removed.
Every orphaned code is restored, including a blank or malformed one a hand edit may have left, because
skipping it would stop the upgrade at the key it violates.

**What to review afterwards.** On the Currencies page, search for `Restored currency`. For each row,
either correct its name, symbol and minor units, or change the records that use it to another
currency and then delete it. From now on, deleting a currency that is still in use is refused with a
`409` naming how many of each kind of record use it.

**Plan for write downtime.** MariaDB can add a foreign key only by copying the whole table
(`ALGORITHM=COPY`), which blocks writes to it while the copy runs. All eight tables are rebuilt,
`Transactions` among them. On a large database, upgrade during a quiet window.

## Backups

The stateful pieces are three named volumes — back up all three:

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
  #346). It holds users' files on disk, under their original names. Back it up **after** the database
  dump, within the deletion grace period — the order matters; see
  [File storage → Backup and restore](#backup-and-restore). No feature stores files in it yet, so today it
  holds only its `.odyssey-volume.json` marker.

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

Since issue #348 the **worker** mounts the volume too, **read-only**, because a background job reads the
encrypted secret store (the Frost client id today; mail and other jobs are expected to follow). This was
a deliberate choice over the alternatives: keeping secret-reading jobs in the API, or an internal endpoint
through which the worker asks the API for one secret at a time.

What it changes and what it does not:

- **A compromise of either container exposes every stored credential.** The incident response above
  applies to the worker exactly as to the API: rotate every credential at its provider.
- **The worker never writes the ring.** The mount is `:ro`, and automatic key generation is turned off
  in the worker (`WorkerHost`), so the API remains the only process that creates, rotates or revokes a
  key. A worker started against an empty volume refuses to protect anything rather than inventing a key.
- **Both must use the same path and the same application name** (`Odyssey`). A worker without
  `DataProtection__KeysPath` logs a warning at startup, and every secret then reads as *cannot be
  decrypted*: observations stop and log an error hourly, while forecasts continue.
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

#### Upgrade note — an existing keys volume may need a one-time `chown`

The API image creates `/var/odyssey/dataprotection-keys` owned by `app` at build time, and the API
**asserts at startup that the directory is writable**, failing to start if it is not. Docker copies
image directory ownership into a named volume **only when the volume is empty**, so a
`dataprotection_keys` volume created before this release may still be `root:root 0755` — in which case
that installation has been running an ephemeral key ring all along (forcing a re-login on every
restart) and will now stop instead of continuing silently. That is the intended posture once
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
| Demo-data seeding | `Odyssey.MigrationService/DemoDataSeeder.cs` | Allowed, with the published shared password |
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
separate `contacts.sensitive.read` claim: it would make the contact read DTO claim-conditional, need a
`RolePermissions` change, and force every existing session to sign out and back in, for a boundary
that does not exist in that threat model.

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

## Household locations: what leaves the deployment, and how to erase one

Issue #350 added **locations** — the household's places, each with a name, an address line,
coordinates to about 1 m, a time zone and a free-text description. Three things about them are an
operator's concern.

### Release note

- **New claims.** `locations.read` (every role, Guest included), `locations.create` and
  `locations.update` (Admin, Owner, User). Issue #380 later added `locations.delete` (below). Claims are baked into the
  auth cookie, so **every user must sign out and back in** before the Locations page appears.
- **Address search sends text to a third party** (below).
- **The runtime image needs `tzdata`.** The API Dockerfile installs it, and the API refuses to start
  if `Europe/Oslo`, `Europe/Kyiv`, `America/New_York` or `Etc/UTC` does not resolve. If you build your
  own image, keep it.
- Two new deploy-time limits: `RateLimiting:LocationSearch` (per user, default 30 per 60 s) and
  `RateLimiting:LocationSearchGlobal` (whole instance, default 120 per 60 s).

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

### Release note: unified locations (issue #380)

- **Take and verify a backup first.** The upgrade converts every contact address, every property
  address and every free-text place on calendar events, recurring series and journal entries into
  locations, once, and drops the old address columns in the same release. No copy of the old values is
  kept, so **rollback is a database restore** — a migration `Down()` restores schema, not rows. The
  migration service logs the conversion's row counts at start and its created and linked counts at the
  end (counts only).
- **A contact address with no usable text is removed.** An `Addresses` row whose six fields are all
  empty once control characters and whitespace are cleaned has no address to link to a place. The
  conversion deletes such rows before the contract migration and logs how many (a warning, count only);
  the contact itself is kept. This is an accepted loss, chosen so the upgrade never stops on it.
- **Link-like values stay text.** A place that looks like a meeting link or carries a passcode is never
  turned into a location (locations are readable by every `locations.read` holder, Guest included).
- **New claim `locations.delete`** (Admin, Owner, User; not Guest). **Every user must sign out and back
  in** to receive it.
- **Converted places have no coordinates** until someone geocodes them on the Locations page.
- **An event or entry at a place linked to a weather point uses that point** (issue #387), with no
  fallback to the caller's preference or the household default even when the point's forecast is off; a
  place with no link falls through to the preference and then the household default.
- **`locations.read` sees every address in the app** (product decision 3), including those that used to
  sit behind `contacts.read` or `properties.read` alone.

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
  not listed. Earlier releases shipped
  `positron` and `dark`, and `dark` was nearly black. All five OpenFreeMap styles (`liberty`,
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

### Release note

- **New claims.** `weather.read` and `weather.preferences.update` (every role, Guest included), and
  three Admin-only management claims — renamed by issue #387 to `weather.points.read`,
  `weather.points.update` and `weather.points.delete` (see *Weather points* below).
  **Every user must sign out and back in** before the weather widget and the Admin controls appear.
- **Nothing is fetched until an administrator sets it up.** MET's terms require a contact in the
  `User-Agent`, so set **`/settings` → Weather → Contact for MET Norway** (an email address or an
  `https://` URL). Until then every weather read reports *not configured* and no request leaves the
  deployment. Then turn weather on per place on the Locations page.
- **Observations are optional.** They need a Frost client id (free, from `frost.met.no`), entered in
  **`/settings` → Weather** as a secret. Without it, past days show the forecast only. An id that
  cannot be decrypted (a lost Data Protection key ring) stops observations and logs an error hourly.
- Four new settings: the contact, the refresh interval (default 1440 min, i.e. once a day; 30–1440), the cap on scheduled
  places (default 5, 1–20) and the household default place. No configuration keys are added or moved.
- Two new deploy-time limits: `RateLimiting:WeatherRead` (per user, default 120 per 60 s) and
  `RateLimiting:WeatherRefresh` (per user, default 6 per 300 s, covering refresh, history purge and
  point delete).
- **A new container, `worker`** (image `odyssey-worker`), runs the schedule: once a minute, at most one
  MET request per second. It is where every background job runs from now on; it has no port and serves
  nothing. Every worker replica runs the schedule, and a lease on each place keeps two from fetching the
  same one. The API still fetches on demand and for the Admin refresh.
- **The worker mounts `dataprotection_keys` read-only** so it can read the Frost client id. That makes it
  the second container able to decrypt every stored credential; see
  [Two containers hold the key ring](#two-containers-hold-the-key-ring).

### Weather points (issue #387)

Weather is now kept per **weather point** — a named position fetched once — and any number of places
**link** to one point, so two streets in one city share one forecast and one history.

- **Back up the database before deploying.** Two migrations, `WeatherPointsExpand` and
  `WeatherPointsContract`, turn every per-place weather row into a point **under the same id**, re-key
  every weather table onto it, link each place to its point, delete orphaned weather rows, rename the
  settings keys `WeatherDefaultLocationId` → `WeatherDefaultPointId` and `WeatherMaxTrackedLocations` →
  `WeatherMaxTrackedPoints` (values kept) and rewrite each weather preference's field. They are data
  migrations and **not replay-safe**: `Down()` restores the schema only, and a failure is fixed forward,
  with the backup as the fallback (see [`migration-history-drift.md`](migration-history-drift.md)).
- **Stop `api` and `worker` before the migrations job runs** when upgrading a running stack: an old
  worker against the contracted schema fails every tick. Compose and the AppHost already order a fresh
  start correctly.
- **Renamed claims.** The three Admin weather claims are now `weather.points.read`,
  `weather.points.update` and `weather.points.delete`. `RoleClaimSeeder` adds the new values and removes
  the old ones; an existing session picks the new values up at its next security-stamp revalidation —
  within about **one minute**, without signing out — and until then the point endpoints answer `403`.
- **Linking** a place to a point needs `locations.update` and `weather.read` (Admin, Owner, User). It
  sends nothing to MET; only an Admin creates or moves a point.
- `/api/weather/locations` is gone; points are `/api/weather/points`, and a place's link is
  `PUT /api/locations/{id}/weather-point`.

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
## Exchange-rate sync: what leaves the deployment, and what changes for existing rates

Issue #349 added a scheduled sync of exchange rates from **Norges Bank** (`data.norges-bank.no`).
It is off until an Admin starts syncing a currency on the *Currencies* page (`/currencies`), which also
shows the sync status, *Run now* and the run history; *System settings* keeps only the start date and
the interval.

### Release note

- **Existing direct rates can lose to a NOK hop.** A pair with no direct rate now converts through NOK
  (`A → NOK → B`), and when both exist the one dated later wins — the direct rate keeps the tie on the
  same UTC date. So a hand-entered `USD → EUR` dated 2025 stops being used once USD and EUR sync. That
  is intended.
- **A date-only manual rate loses to the same day's fetched rate.** Fetched rates are stamped at 16:00
  Norwegian time (14:00 or 15:00 UTC); a manual rate entered for a date is stamped 00:00. To override a
  fetched day, enter a manual rate with a later time.
- **Future-dated rates now count only from their date** and list with the status *Upcoming*. Before,
  a rate dated next week was already used for conversions.
- **Fetched rates cannot be edited or deleted one by one.** `PUT` and `DELETE /api/exchange-rates/{id}`
  on one are a `422`, whoever asks: the sync never refetches a day removed from inside what it already
  holds, so a single delete would leave a gap that conversions silently bridge with an older rate.
  Fetched rates leave per currency, through *Delete fetched rates* once its sync is off (Admin). Owner
  keeps editing and deleting manual rates.
- **Rolling back the migration** (`AddExchangeRateSync`) keeps the rows but drops the column that says
  they were fetched, so they then read as manual. `Down()` restores schema, not provenance.
- **No new claims**, so nobody needs to sign out. No new configuration: the start date and interval are
  System settings, and nothing is carried over from the environment (both keys are new).
- **The sync runs in `Odyssey.Worker`**, like every background job. Without a running worker nothing is
  fetched: *Run now* still records a `Running` run, which no process takes; the next run or purge
  started after two minutes marks it *Abandoned* and proceeds, logging a warning that asks whether the
  worker runs. The API reaches Norges Bank too — for the list of published currencies behind the
  Sync column on Currencies — so both containers need egress to `data.norges-bank.no`.
- **Both runtime images need `tzdata`** for `Europe/Oslo`, and both Dockerfiles already install it.
  Without it the sync runs fail with *Time zone unavailable*; the hosts still start.

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

## File storage (filesystem volumes)

Issue #346 adds a **filesystem object store**: files kept on one or more deploy-time *volumes* instead of
as `LONGBLOB` rows in MariaDB. It is the foundation photo thumbnails (issue #362) build on, and photo
thumbnails are its **first and only feature** — see [Photo thumbnails](#photo-thumbnails). Existing uploads
stay in the database. What changes for an operator is that two containers now mount a
third named volume, verify it at startup, and refuse to start on a misconfiguration.

### Release note

- **A third named volume, `files_primary`**, is mounted read-write into **`api` and `worker`** at
  `/var/odyssey/files/primary`, with `FileStorage__Volumes__primary__RootPath` set on both. Both come
  from the base `docker-compose.yml`, so the production overlay needs no edit. `docker compose pull` and
  `up -d` create the volume, and the API **initialises** it on first start: it writes
  `.odyssey-volume.json` and records the volume in the new `FileStorageVolumes` table. The worker waits
  (up to 2 minutes) for the API to do that, then verifies it.
- **One migration**, `AddFileSystemObjectStorage`: four new tables (`FileStorageVolumes`,
  `StoredObjects`, `FileStorageOrphanSightings`, `FileStorageMaintenanceLeases`) and one settings row.
  Schema only, additive; `Down()` drops all of it.
- **One new setting**, **System settings → Storage → Deletion grace period** (default 24 h, 1–168 h). It
  takes the security claim and every change is audited.
- **One new claim**, `file-storage.read`, Admin only, reconciled onto the role by the migrations job. It
  gates **System settings → Storage health**. An administrator already signed in must **sign out and back
  in** to see the panel: claims are baked into the auth cookie.
- **A file volume that fails verification in the worker stops file clean-up only.** Weather and
  exchange-rate sync keep running, and the worker container stays up — so `docker compose ps` no longer
  shows the fault. The Storage health panel does: *File clean-up is stopped: the worker's storage check
  failed*, naming the check. The worker re-runs the checks every 10 minutes and resumes on its own once
  they pass. Once a release adds a storage feature (photo thumbnails), a worker with no `FileStorage__*`
  configuration at all reports the same way.
- **A second migration**, `RecordFileStorageWorkerVerdicts`: one new table, `FileStorageWorkerVerdicts`
  (a row per worker whose storage check is failing). Schema only, additive; `Down()` drops it.

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
| `FileStorage__Bindings__<Feature>` | Which volume a storage feature's **new** files go to (`FileStorage__Bindings__PhotoThumbnails=primary`). The shipped files bind both features, `PhotoOriginals` and `PhotoThumbnails`, to `primary`. A host with volumes and an unbound feature refuses to start (check B1). |

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
files between volumes is not supported in this release.

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
database's `LONGBLOB` columns: encrypt the host disk (LUKS / dm-crypt), and treat a copied volume as
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

Issue #409 records the runs of the worker jobs — exchange-rate sync, weather, file-storage maintenance, photo
thumbnails and, since issue #428, the photo-original move — in one table, `JobRuns`, and each worker process's
last tick per job in `JobHeartbeats`.
**`/background-jobs`** reads both; everything there is under `system-settings.read` (Admin), and a job's own
counters additionally need that job's read claim (`weather.points.read`, `file-storage.read`, `photos.read`).

### Release note: the exchange-rate sync's run log moved (data migration)

Two migrations run in order, both automatically before the API starts:

1. **`AddJobRuns`** validates every `ExchangeRateSyncRuns` row against the new table's constraints first — a
   row that cannot be copied aborts the migration *naming its id*, with nothing created — then creates
   `JobRuns`, `JobHeartbeats`, the `PhotoThumbnailJobs.ManualRequeues` column and the `JobRunRetentionDays`
   setting (90 days), and copies every row across **with its id** (`INSERT … WHERE NOT EXISTS`, so a replay
   after an interruption copies nothing twice).
2. **`DropExchangeRateSyncRuns`** checks that every legacy row is in `JobRuns` with an equal status, trigger,
   timestamps and row counters, aborts (dropping nothing) if one is not, and only then drops the old table.

Because MariaDB DDL is not atomic, they are two migrations on purpose: an interruption in the first leaves the
source intact. If either aborts, follow [`migration-history-drift.md`](migration-history-drift.md).

**Rollback is a backup restore.** `Down()` restores schema, not rows: rolling back past
`DropExchangeRateSyncRuns` recreates an *empty* `ExchangeRateSyncRuns`. Take the usual pre-upgrade database
dump. The existing exchange-rate sync endpoints and the Currencies page keep their wire shape.

**Deploy order: migrations, then the worker and the API together.** A worker from before this release would
write into a table that no longer exists, and an API from before it would read one; both images come from the
same release tag, so the normal `pull` + `up -d` does this.

### What the worker records

| Job | One run row per | Held rows |
|---|---|---|
| Exchange-rate sync | run or purge (unchanged) | — |
| File-storage maintenance | pass — scheduled hourly, or manual, picked up by a **30 s request poll** | while *this* worker's storage checks fail (`HeldByCheck` names the check, never a path) |
| Weather, photo thumbnails, photo-original move | **work burst**: consecutive ticks with work share one row; an idle tick, or the burst reaching 1 h, finishes it | thumbnails: while the worker is Paused, Tripped, PolicyFailed or StorageUnavailable. Photo-original move, only while photos are left to move: StorageUnavailable (volume degraded or below its reserve) or BackingOff (after a storage or database error, until the back-off ends) |

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
(`weather.points.update`, `file-storage.read` for both file-storage maintenance and the photo-original move,
`photos.update`; the sync needs none beyond the first). A photo-original move run moves one batch at once and
**ends a back-off** early; it is refused (`409 held`) only while every live worker is held for storage. The
file-storage manual pass is gated on `file-storage.read` by decision — no file-storage write claim exists — and
uses the same deletion grace and lease as a scheduled pass. A thumbnail manual run drains pending jobs and
re-queues each `Failed` job **once in its lifetime** (`ManualRequeues`); `Unsupported` jobs, crashes and
timeouts included, are never re-queued.

Each job has a 60 s cooldown, and a per-user limiter sits in front of all of them:
`RateLimiting:BackgroundJobsRun` (default 5 requests per user per minute). Every accepted manual run is written
to the application log at Information under the System settings audit category
(`Odyssey.Api.SystemSettings.SystemSettingsService`) with the acting user's id, the job and the run id —
independent of the row, which is pruned and whose requester is nulled when the account is deleted.

Under `ASPNETCORE_ENVIRONMENT=Testing` only the exchange-rate sync executes a manual run inline; the others
stay unclaimed (no test may call MET or load ImageMagick).

## Photo thumbnails

Issue #362 renders two WebP thumbnails per photo — **Small (200 px)** and **Medium (400 px)** on the long
side — in **`Odyssey.Worker`**, and stores them on the `PhotoThumbnails` file-storage feature. Grids,
album covers, pickers and the journal gallery show them (issue #365); the viewer, "Open original", the
lightbox and the edit banner always show the original. Thumbnails are **derived data**: every one can be
regenerated from its photo, so none is exported, and a volume holding only thumbnails may be left out of
backups.

### Release note

- **One migration**, `AddPhotoThumbnails`: five tables (`PhotoThumbnails`, `PhotoThumbnailJobs`,
  `PhotoThumbnailBuckets`, `PhotoThumbnailIncidents`, `PhotoThumbnailWorkerHeartbeats`) and the one bucket
  row. Schema only, additive; `Down()` drops all of it (the files are then unreferenced and the hourly
  sweep erases them).
- **The backfill runs on its own.** The worker queues every existing photo at background priority, 500
  per pass, behind new uploads. Until a photo's two thumbnails exist the client shows the original, exactly
  as before.
- **A new native dependency, ImageMagick (Magick.NET), in the worker image only.** The API never loads it;
  `Odyssey.Worker.Tests` fails if any other project references it.
- **Worker container limits**, in both compose files: `mem_limit: 1g`, `pids_limit: 256`, and `/tmp` as a
  64 MB `tmpfs`. ImageMagick itself is capped at 320 MB of pixel cache with disk spill off.
- **`FileStorage__Bindings__PhotoThumbnails=primary`** on `api` and `worker`. Keep the two identical.
- **No new claim and no new setting.** Thumbnails are served by `GET /api/photos/{id}/thumbnail` under
  `photos.read`. The worker state on `GET /api/photos/stats` is filled only for `system-settings.read`.
- **Renaming a photo's file re-renders its thumbnails**, because the stored files carry the name. A rename
  that collides with a running render twice in a row returns `409 rename-busy` with `Retry-After: 1`.

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
| Not running | No worker has written a heartbeat in 3 minutes. Thumbnails, file clean-up, weather and exchange-rate sync are all stopped. | Start the worker container. Photos keep showing their originals. |

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

Issue #428 moves the original of every photo uploaded in the journal or the photo library out of the
database (`FileBlob`) onto the **`PhotoOriginals`** file-storage feature. Files uploaded anywhere else —
finance documents, contract and journal attachments, avatars — stay where they were. A photo now owns its
bytes: it has no Files-store row, `files.read` does not reach it, and its original is served by
`GET /api/photos/{id}/content` under `photos.read`.

### Release note

- **One migration**, `MovePhotoOriginalsToObjectStore`: new columns on `Photos` (`StoredObjectId`, `FileName`,
  `ContentType`, `SizeBytes`, `Sha256Hash`, `LegacyFileId`; `FileId` becomes nullable), three tables
  (`PhotoOriginalBuckets`, `PhotoOriginalBackfillLeases` with its one row, `PhotoOriginalBackfillFailures`) and
  two checks (`CK_Photos_ExactlyOneSource`, `CK_Photos_OriginalComplete`). **`Down()` only works before the
  backfill has moved anything**: once a photo's `FileId` is `NULL` the column cannot become `NOT NULL` again.
  Rolling back after that is a **backup restore**, nothing else.
- **`FileStorage__Bindings__PhotoOriginals=primary`** on `api` and `worker`, in both compose files and the
  AppHost. Without it both refuse to start (check B1). Keep the two services identical.
- **One more background job on `/background-jobs`**, the photo-original move. Its runs are recorded from the
  first release (migration `AddPhotoOriginalBackfillJobRuns` widens the run log's checks), and its counts need
  `file-storage.read`.
- **Upload is one multipart call**, `POST /api/photos`, rate-limited per user by `RateLimiting:PhotoUpload`
  (120 per 10 minutes by default). It refuses with `507` when the volume's free space is below the reserve —
  the store's 1 GB floor plus 4 GB — and with `409` (`photo-duplicate`) when the same image is already a photo.
  Creating a photo from an existing Files-store file is gone.
- **Thumbnails become graced.** Their release used to be *Immediate*; it now waits for the deletion grace
  like everything else.

### The backfill

The worker moves existing originals on its own, oldest photo first, 25 per tick every 30 seconds within a
60-second budget, behind a single lease row so only one replica runs it. For each photo it writes the file,
reads it back and compares the SHA-256 before the database changes; then, in one transaction that first
locks the file row, it repoints the photo and deletes the old file **only if nothing else references it**
(an attachment, an avatar, a contract file — the check is derived from the database model, not a list). A
file still in use elsewhere is **copied** and kept; the count of those is reported.

It does nothing while the photo volume is degraded or below the reserve. A storage or database error stops
the tick and backs off 1, 5, 15, then 60 minutes; no photo is blamed for it. A photo that cannot be moved for
a reason of its own is **parked** with a code and never retried automatically:

| Code | Meaning | What to do |
|---|---|---|
| `hash` | The stored bytes do not match their recorded SHA-256, or the copy did not read back identically. | Restore the file from a backup, or delete the photo. |
| `read` | The bytes are missing, a different length, or the type is not a photo type. | Same. |
| `too-large` | The file is larger than `FileStorage:MaxFileSizeBytes`. | Raise that limit and retry, or leave the photo in the database. |
| `error` | An unexpected failure that is neither of the above nor a storage or database outage (the worker log names the exception type and the photo id). | Read the log line, fix the cause, then retry. |

A parked photo stays readable from the database exactly as before. **To retry**, delete its row:

```sql
DELETE FROM PhotoOriginalBackfillFailures WHERE PhotoId = '{{ PHOTO_ID }}';
```

Progress — remaining, parked by code, copied-shared, the back-off — is on **`/background-jobs`** (the
*Photo original move* tile, its status card and its runs, each run counting the photos moved, copied, parked and
the interruptions) and on `GET /api/file-storage/photo-backfill`, both under `file-storage.read`. They report
counts and codes only, never a name. A manual run from the page ends a back-off early; it does not retry a parked
photo — that is still the `DELETE` above. Once nothing is left to move the job records nothing more, and its tile
keeps showing its last run.

### Erasure of a photo original

A deleted photo's original waits for the deletion grace (default 24 h, at most 168 h) before the hourly job
removes it, so a backup taken in that window still holds it. For an erasure request that cannot wait, lower
**Deletion grace** at `/settings` to 1 h, delete the photo, and restore the setting after the next
maintenance pass. The file name on disk is the sanitized original name; never log or share a volume
listing.

### Backup

Photo originals are **primary data**, unlike thumbnails: the volume that holds `PhotoOriginals` must be in
every backup, copied after the database dump and within the grace (see [Backup and
restore](#backup-and-restore)). A database restored without its volume makes every moved photo unreadable,
and the disk check's breaker halts clean-up rather than quarantine the volume.

## Files on the object store

Issue #448 moves every other file out of the database (`FileBlob`) onto three file-storage features, one per
area. Placement is not an access boundary: who may read a file is still decided by the claim of the record it is
attached to.

| Area | Feature | Root on the volume | What lands there |
|---|---|---|---|
| Finance | `FinanceFiles` | `finance-files/` | Account, transaction, contract, tax-statement and property documents; file-analysis uploads; anything not linked to a journal record |
| Journal | `JournalFiles` | `journal-files/` | Journal-entry and task attachments |
| Contacts | `ContactAvatars` | `contact-avatars/` | Contact images |

Each file sits at `<root>/<upload year>/<bucket>/<sanitized original name>.<extension>`. The area is chosen once,
at upload: the upload form sends `area` (`Finance` or `Journal`), the avatar path always writes `ContactAvatars`.
A file later attached somewhere else stays where it was put.

### Release note

- **Bind the three features before you upgrade.** Add `FileStorage__Bindings__FinanceFiles`,
  `FileStorage__Bindings__JournalFiles` and `FileStorage__Bindings__ContactAvatars` to **both** `api` and `worker`
  (the base compose file and the AppHost bind all three to `primary`). Without them both refuse to start (check
  B1). Keep the two services identical; `FileStorageDeploymentParityTests` enforces it for the shipped files.
- **One migration**, `MoveFilesToObjectStore`: three nullable columns on `FileMetadata`
  (`FinanceStoredObjectId`, `JournalStoredObjectId`, `ContactAvatarStoredObjectId`, each `RESTRICT` to
  `StoredObjects`), `FileBlobId` becomes nullable, the check `CK_FileMetadata_ExactlyOneSource`, and three tables
  (`StoredFileBuckets`, `FileBlobBackfillLeases` with its one row, `FileBlobBackfillFailures`, which has no
  foreign key). The run log's checks widen for the new job type. **`Down()` only works before the backfill has
  moved anything**; after that, rolling back is a **backup restore**.
- **Uploads go to disk from the first request.** `POST /api/files` takes a required `area` form field and is
  rate-limited per user by `RateLimiting:FileUpload` (120 per 10 minutes by default); the contact-avatar upload
  shares that limit. An upload refuses with `507` when the volume is below the reserve (the store's 1 GB floor
  plus 4 GB), `503` when it is degraded, and `429` past the limit — each with `Retry-After` where it applies.
  Nothing is stored on a refusal, and no bucket slot is taken.
- **Downloads are unchanged on the wire** (disposition, content type) and now carry `nosniff`, a strong `ETag`,
  `Cache-Control: private, no-cache` and range support, whether the file is still in the database or on disk.
- **`fileBlobId` is gone from every attachment DTO.** A client that read it must stop.
- **`POST /api/files` validates `description`** at 256 characters and answers `400` where it used to cut the text
  silently.
- **One more background job on `/background-jobs`**, the file move. Its counts need `file-storage.read`.
- **The `FileBlob` table stays** until a later release has seen every deployment report `remaining = 0`. Do
  not drop it by hand.

### The backfill

The worker moves the remaining files on its own, oldest first, 25 per tick every 30 seconds within a 60-second
budget, behind a single lease row. Each file's area is decided from what references it **at move time**:

| References | Area |
|---|---|
| None, or any finance record | Finance |
| Only journal entries or tasks | Journal |
| Only a contact, as its image, with an image content type | Contacts |
| Only a contact, with any other content type | Finance |

For each file it writes the bytes, reads them back and compares the SHA-256, then, in one transaction that first
locks the file row and re-classifies it, repoints the row and deletes the blob. A file still wrapped by a photo
that has not moved yet is left for the photo move and counted as *waiting on photos*. The job skips an area whose
volume is degraded or below the reserve and keeps moving the others; with nothing movable on a healthy area it
holds as *storage unavailable*. A storage or database error backs off 1, 5, 15, then 60 minutes.

A file that cannot be moved for a reason of its own is **parked** and never retried automatically:

| Code | Meaning | What to do |
|---|---|---|
| `hash` | The stored bytes do not match their recorded SHA-256, or the copy did not read back identically. | Restore the file from a backup, or delete it. |
| `read` | The bytes are missing or a different length from the recorded size. | Same. |
| `too-large` | Larger than `FileStorage:MaxFileSizeBytes`. | Raise that limit and retry, or leave the file in the database. |
| `type` | The content type has no file extension the store accepts. | Leave it in the database, or delete and re-upload it as a supported type. |
| `error` | An unexpected failure that is neither of the above nor an outage; the worker log names the exception type and the file id. | Fix the cause, then retry. |

A parked file stays readable from the database exactly as before. **To retry**, delete its row:

```sql
DELETE FROM FileBlobBackfillFailures WHERE FileId = '{{ FILE_ID }}';
```

A failure row whose file has since been deleted is removed on the next tick. Progress — `remaining` (split into
`toMove`, `waitingOnPhotos` and `failed`), `remainingBytes`, parked by code, files on disk per area and the
back-off — is on `/background-jobs` (the *File move* card and its runs) and on `GET /api/file-storage/file-backfill`,
both under `file-storage.read`. Counts and codes only, never a name or path. Size the volumes from
`remainingBytes` **before** the upgrade.

### Moving the finance area to its own volume

Every area may bind to its own volume. Rebinding an area that already holds objects is not a configuration
change: the rows name the volume they were written to, so the new binding applies to new writes only and the
old volume must stay mounted. Plan the split before the upgrade, while the area is still empty.

### Erasure and backup

A deleted file waits for the deletion grace (default 24 h) before the hourly maintenance pass removes it from disk;
for an erasure request that cannot wait, follow the procedure under [Erasure of a photo
original](#erasure-of-a-photo-original). The file name on disk is the sanitized original name — personal data —
so never log or share a volume listing. All three areas are **primary data**: every volume they are bound to
must be in every backup, copied after the database dump and within the grace (see [Backup and
restore](#backup-and-restore)).

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
- [ ] The API started cleanly, i.e. the keys directory is writable (see the upgrade note above).
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
