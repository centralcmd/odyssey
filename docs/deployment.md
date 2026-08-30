# Deploying Odyssey to a single VPS

This describes the supported production deployment: a **single Linux VPS running Docker**,
fronted by **Caddy** for automatic TLS, deploying **pinned SemVer image tags manually**.

The app runs as four containers behind one origin:

```
            ┌──────────────────────────── VPS ────────────────────────────┐
 Internet ──┤  Caddy :443 (TLS) → client nginx :80 ─/api/→ api :8080       │
   (443)    │                                          │                   │
            │                                       mariadb :3306 (internal)│
            │                          migrations (runs once, then exits)  │
            └──────────────────────────────────────────────────────────────┘
```

Because the client's nginx proxies `/api/*` to the API, the browser only ever talks to one
origin (`https://your-domain`). That keeps cookie auth working without CORS configuration.

Images are published to GHCR automatically on every release (`…-api`, `…-client`,
`…-migrations`), tagged with an immutable SemVer. Production **pulls** these — the server
never builds.

## Files involved

| File | Purpose |
|---|---|
| `docker-compose.yml` | Base service definitions (shared with dev). |
| `docker-compose.prod.yml` | Production overlay: pull images, Production env, Caddy, no host ports, persisted keys. |
| `Caddyfile` | TLS termination + reverse proxy to the client. |
| `.env.prod.example` | Template for the server-side secrets/config file. Copy to `.env.prod`. |

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

4. **Create `.env.prod`** from the template and fill in real values:
   ```bash
   cp .env.prod.example .env.prod
   # edit .env.prod: GHCR_OWNER, IMAGE_TAG, ODYSSEY_DOMAIN, DB passwords, SMTP host/port/TLS,
   #                 BOOTSTRAP_ADMIN_EMAIL, BOOTSTRAP_ADMIN_PASSWORD
   ```
   - Generate strong DB passwords (e.g. `openssl rand -base64 24`).
   - `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` create the initial administrator on a
     fresh database and are **required** there — see [First-run notes](#first-run-notes).
   - `EMAIL_CLIENT_BASE_URL` **must** be your real `https://` domain, or confirmation and
     password-reset links will be wrong.
   - A real SMTP **transport** is required, and only the transport lives here:
     `EMAIL_SMTP_HOST`, `EMAIL_SMTP_PORT`, `EMAIL_USE_STARTTLS`. In Production an empty
     `EMAIL_SMTP_HOST` **fails startup** (issue #405): the no-relay fallback logs the action link
     instead of sending it, which for a password reset means writing a working credential into the
     log, so a Production deployment refuses to run in that state rather than degrading into it.
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

## Upload size: three ceilings, in order

The maximum upload is enforced at three layers, and **raising it means raising all three, outermost
first**. Raising only the inner one produces a `413` from a component that never sees the app's
setting; raising only the outer ones changes nothing.

| Layer | Where | Must be |
|---|---|---|
| Reverse proxy | `Caddyfile` (`request_body max_size`) and `Odyssey.Client/nginx.conf` (`client_max_body_size`) | ≥ transport ceiling + multipart envelope headroom |
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

### Release note: three `FileAnalysis` keys retired from `appsettings.json`

`FileAnalysis:MaxTokens`, `FileAnalysis:Match:MaxVocabulary` and `FileAnalysis:Match:TimeoutSeconds` are
database-backed as of issue #434 and no longer read from configuration.

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
  reachable by an admin action: wait it out, or restart the API.

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

`Email:SmtpHost`, `Email:SmtpPort`, `Email:UseStartTls` and `Email:ClientBaseUrl` deliberately stay in
configuration. The sender connects to the host and *then* authenticates, so an admin-editable host
would harvest the relay credential and every password-reset token; `ClientBaseUrl` is the host of every
reset link.

## Backups

The only stateful pieces are two named volumes — back both up:

- `mariadb_data` — all application data. A nightly logical dump is recommended:
  ```bash
  docker exec odyssey-mariadb sh -c \
    'exec mariadb-dump -uroot -p"$MARIADB_ROOT_PASSWORD" --all-databases' \
    > odyssey-$(date +%F).sql
  ```
- `dataprotection_keys` — **the Data Protection key ring, and it is secret-bearing.** It has always
  held the auth-cookie and antiforgery signing keys; since issue #444 it is also what decrypts any
  credential an administrator stores under **System settings → Credentials**. See the section below.

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

**Only the `api` container mounts it.** The migrations job did too for a while, so that a step there
could protect a value under keys the API could read; no such step exists, so the mount was removed —
every container holding the ring is one more that can decrypt every stored credential. If you add a
mount, add it with the step that needs it, pointing at this same volume.

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

**Both containers mount it.** `api` and `migrations` share the volume and set the same
`DataProtection__KeysPath`, so a value protected by one is readable by the other. That widens key
custody to two containers, deliberately: the alternative is a migrations job writing rows the API can
never decrypt. It works because both images are `aspnet:10.0-alpine` running `USER app`, so the key
files one writes are readable by the other — and the migrations job runs first, so it is usually the
one that creates the ring.

**The mount must be read-write on every service that has it.** A read-only mount does not fail loudly:
Data Protection falls back to an in-memory key ring with a log line, which is the same
silently-ephemeral failure by another route.

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

Set `IMAGE_TAG` back to the previous version and re-run pull + up. Note that **schema
migrations are forward-only** — if the newer release added a migration, rolling the images
back does not roll the database back. Take a DB backup before deploying a release that
includes migrations.

The migration history has been squashed to a single `InitialCreate` more than once — most recently
when the identity context was merged into `OdysseyContext` — so there is no upgrade path from a
database built by an earlier build. What licenses a squash is that **no deployed database holds data
anyone needs to keep**, not the absence of releases: tagged releases and published images have existed
since `v0.8.0`. Every such database has been a development or test one; recreate rather than migrate
them. The first real deployment retires that licence permanently, after which a schema change is an
additive migration even when a squash would be tidier.

## Security checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production` (Swagger off; set in the overlay) — and **not**
      `Testing`, which the API refuses to start under; see the section above.
- [ ] No demo data: if this host ever ran the base `docker compose up`, its `mariadb_data`
      volume was destroyed with `docker compose down -v` before the production overlay came up.
- [ ] No host port mappings for `api`/`mariadb`/`client` — only Caddy's 80/443 are exposed.
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
