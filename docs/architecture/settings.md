# System settings, secret settings and deploy-time configuration

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**No runtime feature toggles for new features.** Do not add per-feature kill-switch flags (e.g. a `Feature:Enabled` option that returns `503` when off) when building new features. Gate capabilities with **permission claims**, not config toggles. One early feature (file analysis's `FileAnalysis:Enabled`) predates this convention and keeps its flag; don't follow that pattern in new work and don't add more.

**`ApiDocs:Enabled` and `ApiDocs:RequireLogin` are a deliberate exception, and not a precedent** (issue #569).
They are deploy-time switches that decide whether the API documentation routes exist (never in Production) and
whether reading them needs a session. That is an access posture for a non-production developer surface, not a
product capability: "anyone, no login" cannot be expressed as a permission claim, and both are bound when the
routes are mapped at startup, so neither belongs in the `SystemSettings` store. Do not cite them for a product
toggle.

That rule is unchanged by issue #439, which **relocated** the one grandfathered flag from `appsettings.json` into the `SystemSettings` store (`FileAnalysisEnabled`) rather than adding a new one. Nothing about the claim gates changed — `file-analysis.create` / `.read` / `.import` still apply. What changed is that the flag is operable without a redeploy, which matters because it is the switch that stops personal data leaving the deployment for a third-party processor: an operator facing a processor incident, a withdrawn DPA or a data-residency question should not need a container rebuild. It is read **live and uncached** on every file-analysis call (never on the `FileAnalysisSettings` snapshot), so a disable binds on the next request rather than up to 30 seconds later.

**Instance-wide policy lives in the database, not `appsettings.json`.** A growing set of runtime
settings is admin-editable at `/settings`, backed by the `SystemSettings` key-value table on
`OdysseyContext` (issues #349, #343, #421). Do not add a new `appsettings.json` key for anything an
administrator would reasonably want to change without a redeploy — add a setting instead.

Adding one is a single declaration on each side plus a migration:

1. `SystemSettingsKeys` (`Odyssey.Context`) — the key constant and the key in `AllKeys`.
   The **default value** goes in `SystemSettingsDefaults` (`Odyssey.Dtos`), not here. That split
   is load-bearing, not tidiness: a single-direction setting pins one end of its `[Range]` at its
   shipped default, and that attribute sits on `SystemSettingsUpdate` in
   `Odyssey.Dtos.Application`, which `Odyssey.Context` references. Historically the
   edge the attribute would otherwise need was a project-reference **cycle**; since the DTO projects
   were merged the constants share one assembly, so the split is now a namespace convention rather than
   a cycle-avoidance necessity — but keep it. `Odyssey.Dtos` has zero project references and is
   reachable from both halves of the stack, including the WASM client, which is what lets the seed, the
   bound and the client catalogue name one symbol. Same vocabulary/mapping split as `PermissionClaims`.
2. `SystemSettingsBounds` (`Odyssey.Dtos`, root) — for an **int** setting, its `…Min`/`…Max` **pair**
   (issue #437). One pair, four consumers: the `[Range]`, the registry descriptor, the read-path clamp
   and the client catalogue's `Min`/`Max`. `IntSetting.Min`/`Max` are `required`, so a new int key
   cannot get *no* read-path bound; a guard test asserts all four ends agree. A pair, not a ceiling with
   a hardcoded floor of 1 — several keys have a `[Range]` minimum above 1, and for `EmailMaxTrackedRecipients`
   the **floor is the load-bearing end**. Note this is a different number from `SystemSettingsDefaults`:
   that is what a *missing* row falls back to, this is what a *present-but-out-of-range* row is clamped to.
3. `SystemSettingsRegistry` (`Odyssey.Api/SystemSettings/`) — one descriptor: kind, required claim,
   default, bound pair, cache key to evict. **Accessors are explicit typed delegates, never reflection** — a
   reflection-driven registry lets a renamed DTO property silently lose its claim gate.
4. Both DTOs — `SystemSettingsDto` (non-nullable, compiled default) and `SystemSettingsUpdate`
   (nullable; `null` means "leave unchanged", so never use `[Required]` there).
5. A migration on `OdysseyContext` inserting the row, seeded with **today's effective value**, plus
   the matching `HasData` entry.
6. The settings page catalogue (`Odyssey.Client/Pages/Settings.Catalogue.cs`) — one row with `Field`/`Load`/`Write` delegates. Group icons are
   `Icons.Material.Filled.*` constants; row icons are ligature strings.
7. A lookup for the consuming domain, if it is read on a hot path. The interface lives in the **consuming
   domain project** so that project's tests can fake it.

Guard tests fail the build if a step is missed, which is the point: before the registry, a key present on
the write DTO but missing from the claim-check block was written with **no authorization check at all**,
silently. Do not weaken those tests to make a new setting fit.

Two rules that are easy to get backwards:

- **A degraded read must never loosen a bound**, and "conservative" is not always `min`. It is `max` for
  the AI auto-link threshold and for the mail-throttle *window*, `min` for every cap. Resolve the
  direction per setting; a shared `Math.Min` helper silently inverts half of them.
- **A cap whose bound is a compile-time constant belongs in `[Range]`, not in a validator.** Model
  validation runs *before* the service, so a `RequestCapCeilings` validator whose limit equals the
  `[Range]` limit can never fire — a decorative ceiling. `RequestCapCeilings` exists for bounds that
  genuinely cannot live in an attribute: runtime (injected configuration) or cross-assembly ones. The
  distinction is **static vs. derived**, and the `ErrorMessage` is where the explanation goes.
- **`[Range]` is the write-side bound and it runs on the HTTP path alone.** A row written by a hand
  edit or a restore bypasses it, so a setting whose bound is load-bearing also clamps
  on the **read** path (`Math.Min`/`Math.Max` against the shared default). Resolve the direction per
  setting there too.
- **A single-direction setting is expressed by pinning one end of its range at the shipped default**,
  by naming the `SystemSettingsDefaults` constant — never a second literal that could drift from the
  seed. Widening such a range "so a ceiling has something to reject" re-opens whatever the single
  direction was closing; guard tests assert the pinned end both by value and by the constant's name.
- **A cap that is also enforced earlier in the request can only be tightened.** Two mechanisms do this.
  A `[MaxLength]`/`[StringLength]` attribute is compiled into model validation, so a setting raised
  above it is rejected before the service ever sees it. A *startup* limit does the same from the other
  end: Kestrel's `MaxRequestBodySize` and the multipart limit are fixed at startup from
  `FileStorage:MaxFileSizeBytes` and cannot be raised per request, so an upload cap above that would be
  refused by the transport. Either way the admin's value would silently do nothing. Those settings
  validate against the ceiling on write (`RequestCapCeilings`) and publish it on the read DTO so the UI
  can bound the field, rather than being made freely editable. `RequestCapCeilings` is **injected, not
  static** — a runtime ceiling in a `static` field is process-wide while the configuration behind it is
  per-`WebApplicationFactory`, so one test class would change the ceiling every other test sees.
- **An absent row is healthy, not degraded** — it resolves to the compiled default, the same posture
  `SystemSettingsService` takes. Only a failed query or a row present with an unusable value is degraded.
  Conflating the two returns `503` on any database whose rows have not been seeded. A lookup's read must
  therefore return an explicit `readFailed` signal alongside the values: an empty dictionary cannot say
  which of the two happened.
- **Reading a stored value must never throw, at either read site** (issue #437). Every
  `SystemSettingDescriptor.Project` is `TryParse`-based and returns a `ProjectionOutcome`
  (`Ok`/`Unparseable`/`Clamped`) rather than logging — `SystemSettingsRegistry.All` is `static readonly`,
  so a descriptor can never hold a scoped `ILogger` or `IMemoryCache`; `AssembleAsync` has both and does
  the logging, throttled per key. Before this, one corrupt row `500`d `GET /api/system-settings` — the
  page an administrator would use to repair it — and `500`d a `PUT` **after** it had committed.
- **A non-`Ok` outcome is reported to the administrator, not just logged.** It becomes a `Warnings` entry
  (which **replaces** any `Advise` output for that field — a fault they did not cause outranks a cost they
  chose) plus a `SettingFaultKind` entry on `SystemSettingsDto.ProjectionFaults`, the names-only companion
  that lets the client tell the two conditions apart. The advisory never echoes the stored value.
- **A clamped row is reported, not degraded.** It parsed; it was simply outside its pair, so it resolves to
  the nearer bound — `"0"` included, which is the *below-floor* case and not the unparseable one. Only an
  unparseable value or a failed query is degraded, and those resolve to `min(last-known-good, default)`
  with the watermark carrying the same TTL as the values (a watermark older than the TTL is "last known",
  not "last known good"). Every lookup resolves its int settings through one `IntSettingResolver`
  (`Odyssey.Core/SystemSettings/`, issue #287; moved out of `Odyssey.Api` so `Odyssey.Worker` shares
  it) — never a private parse/clamp copy. Because the watermark
  and the resolved value expire together, the watermark only covers the window after an explicit
  eviction (a save); a fault on the first read after a natural expiry resolves to the cold value. That is
  the accepted price of the TTL rule — lengthen the watermark's TTL only as a deliberate change to it.

**Adding a *secret* setting is a different recipe, and the seed/default steps do NOT apply**
(issue #444). A secret-valued setting — a credential, an API key, an HMAC key — lives in the
`SystemSettingSecrets` table on `OdysseyContext`, encrypted with ASP.NET Core Data Protection, and
is declared in `SecretSettingsRegistry` (`Odyssey.Core/SystemSettings/`, beside its reader, so every host
that consumes a secret shares the environment filter; the write side stays in `Odyssey.Api/SystemSettings/`).
That registry deliberately
**does not share a base type** with `SystemSettingsRegistry`: `SystemSettingDescriptor` carries
`Format` (whose output the audit loop writes verbatim), `Project` (which writes onto the response DTO)
and `AuditChanges` (*derived* from the claim), so a secret subclass carrying the security claim would
log the credential in plaintext at `Information` on its very first write. A separate type means no
existing loop can be handed one.

Adding one:

1. `SecretSettingKeys` (`Odyssey.Dtos.Application`) — the key constant plus its entry in
   `AllKeys`. **No colon** in the key, so it cannot collide with `IConfiguration`'s section separator.
   It lives in the Dtos project rather than `Odyssey.Context` because the Blazor client's
   catalogue names the same constants, and the client cannot reference an EF project.
2. `SecretSettingsRegistry.AllUnfiltered` — one descriptor: key, `RequiredClaim`, and a **`Kind`** of
   `RotatableCredential` or `DerivationKey`. `Kind` is `required` so a follow-up cannot add a secret
   without classifying its recoverability — a rotatable credential can be re-pasted, a derivation key
   cannot, and its loss silently voids everything derived from it. It drives the Clear confirmation's
   copy. There is **no cache key** (secrets are never cached) and **no default value**.
3. The action-level `[Authorize]` policy on `SecretSettingsController.Put`/`Delete` must equal the
   descriptor's `RequiredClaim`. That is **two places on purpose** — the claim has to be evaluated
   before key resolution, and the descriptor re-checks it for non-HTTP callers — and a guard test
   asserts they agree. Both drift directions fail closed, but a descriptor-only edit yields a surface
   requiring *both* claims.
4. The settings page's `SecretCatalogue` (`Odyssey.Client/Pages/Settings.Credentials.cs`) — one row with the title, description and icon. These are
   **authored client-side**, because the status endpoint deliberately carries no presentation fields.
5. **No migration, no `HasData`, no `SystemSettingsDefaults` entry.** The table already exists and a
   secret has no shipped default: an absent row means *not configured*, which is a healthy steady
   state. Seeding an empty-string row would create a fourth state ("present but empty") every consumer
   would have to handle. There is no path from configuration to a secret at all — carrying one across
   would leave the plaintext in the environment, which is much of what the move was meant to escape.
   The accepted consequence is a defined gap
   after upgrade during which each secret reads `NotSet` and its consumer behaves as it did
   unconfigured; that gap belongs in the release notes.
6. **Retire the configuration property, don't keep it as documentation of record.** A moved
   *plaintext* setting may keep its bound options property as documentation — `FileAnalysisOptions`
   still does. `EmailOptions` no longer exists at all: issue #8 moved the last four `Email:*` values
   into the store and deleted the class and the whole `Email` configuration section with them, so
   that half of the example is now historical. A
   secret's property is deleted, because a surviving one is a fallback waiting to be written — and the
   single rule this whole area exists to hold is that an `Unreadable` row never resolves to the
   configured value. Deleting it makes that a compile error rather than a matter of vigilance.

Rules a secret follow-up inherits:

- **The three read states are distinct, and `Unreadable` must never collapse into `NotSet`.**
  `ISecretSettingsReader` returns `SecretResult` (`Found`/`NotSet`/`Unreadable`), never a `string?` —
  a nullable string would let a consumer write `?? configuredFallback` and send with the old
  configured value the administrator believed they had replaced. Whether a given consumer fails open
  or closed on `Unreadable` is that consumer's own call, in its own issue.
- **A secret's *destination* is the dangerous half, and moving one requires either the compensating
  controls in full or a structural close.** An admin-editable destination plus a stored key is
  one-request exfiltration of the credential. Two destinations have moved, by two different routes,
  and the difference is the point:
  - `FileAnalysis:BaseUrl` (issue #439) moved because an internal corporate gateway is much of why
    the setting exists, and it carries the **compensating controls in full**: its own security claim,
    https-only validation rejecting any path, query, fragment or credentials, a host-only projection
    on every echo including the *old* value in an audit line, `AllowAutoRedirect = false` on the
    outbound client, a row advisory saying where the key will travel, and a read path that refuses
    rather than substituting the compiled default. The key still travels to whatever host is set —
    the exposure is mitigated, not removed.
  - `Email:SmtpHost` (issue #8) moved on a **stronger** footing: **changing it clears the stored
    credential in the same transaction**, so there is no credential left to present to the new host.
    Turning `EmailUseStartTls` off clears the same two rows, for the same reason in a different
    shape. That is a structural close rather than a detection, and it is what earns the move — it
    does not depend on anyone reading an audit log. The atomicity is load-bearing: if the two writes
    can interleave, an interruption leaves the new host live with the old credential still stored,
    which is the exploit itself.

  Read neither as a general precedent. Read them as the two bars available: carry every control, or
  remove the thing being exfiltrated.
- **A per-send `DelegatingHandler` must not re-attach the credential across a cross-origin redirect.**
  `.NET` strips only `Authorization`; a custom header survives.
- **The consumer has to be able to `await` a scoped `OdysseyContext` where it needs the value.** A
  header fixed at `AddHttpClient` construction time cannot pick up a rotation anyway, so such a
  consumer moves to a per-request handler first (`SmtpEmailSender`'s `IServiceScopeFactory` is the
  working pattern).
- **The real credentials live in the store** (issue #445, plus `WeatherFrostClientId` from #348):
  `FileAnalysisApiKey`, `EmailUsername`, `EmailPassword`, `WeatherFrostClientId` (all `RotatableCredential`),
  `EmailRecipientHashKey` and `LegalPseudonymizationSecret` (both `DerivationKey`). `SecretSettingsRegistry.AllUnfiltered`
  is the authoritative list (it also holds the non-Production `DiagnosticsSelfTest`). Their per-consumer three-state policies are recorded on the descriptors and in
  `docs/deployment.md`; the short version is that `Unreadable` fails closed everywhere except the
  recipient hash key, where it falls back to the per-process key **and logs an error** so it is never
  mistaken for the healthy `NotSet`. `Legal:PseudonymizationSecret` also lost its `ValidateOnStart`
  gate: a credential entered through the UI cannot be a precondition for the UI coming up, so the
  failure moved to the first account deletion, inside its transaction.
- **The printable-ASCII rule was not relaxed for `EmailPassword`**, the one key where a real credential
  could fall outside `0x20`–`0x7E`. The rule also keeps CR/LF out of an SMTP handshake, and the
  alternative to a relaxation is not a bare `400`: `OdsSecretSettingField` names the constraint as the
  value is typed. A future relaxation goes on that descriptor and says so.
- **Nothing echoes a value, ever** — not a response body, not a log line, not an error message, not a
  length, hash, prefix or last-four. Both `SecretSettingUpdate` and `SecretResult` override
  `ToString()` to redact, because a record prints its members and that is what surfaces in a logged
  exception context.
- **Every successful write is audited unconditionally.** There is no change detection: the presence or
  absence of an audit line would itself be a plaintext equality oracle.
- **A write against an ephemeral Data Protection key ring is refused with `503`, not accepted.** The
  check allow-lists *durable* repository types (`EphemeralXmlRepository` is `internal`, so the negative
  form cannot be written), which makes the allow-list the extension point a future KMS or blob provider
  must update.

**A client-side copy of a server cap is a defect, not a convenience.** If a limit is admin-editable, no
page may hold it as a `const`: lowering it would let the user upload the whole file before the server
rejected it, and raising it would be unusable because the local pre-check still refuses at the old
number. Serve it from a claim-free lookup endpoint (`/api/upload-limits`, `/api/import-limits`) through a
session cache that a settings save invalidates, and **interpolate the effective number into the
message** — a literal in the text goes stale the moment an administrator changes the value. A surface
with a deliberately tighter product limit keeps its own named constant and takes `min(global, surface)`;
`min` is the only correct direction, since a surface may tighten but must never override a lowered
global cap. Source-lints in `Odyssey.Client.Tests` enforce all of this.

**There is no path from `appsettings.json` or the environment into the settings store.** A setting that
moves out of configuration has its environment plumbing **deleted**, not carried across: the migration
seeds the shipped default and the administrator sets the real value at `/settings`.

`SystemSettingsConfigAdoption` used to do that carrying, for the keys migrated by issues #421, #434 and
#439. It was removed when no deployment had ever run a release it could upgrade *from*. **That
precondition no longer holds: a production database with real data has existed since `v0.35.0`
(September 2026)**, so the removal decision is retired with it. The next setting that moves out of
configuration needs a carry-over step again, because a compile-time `InsertData` cannot see an
operator's env var and would silently replace their value with the shipped default. (Nothing moved
between the production deployment and this note — every settings key added since is new, not moved —
so no configured value has been lost yet.) What that step must get right, recorded here so it does
not have to be rediscovered: ownership is decided by `UpdatedBy IS NULL`, never by comparing values (comparing
cannot tell "never touched" from "an administrator deliberately set it back to the default", and would
overwrite the second on every restart); it runs in Production, unlike the `DemoDataSeeder` next to it;
it validates against the same `[Range]` the `PUT` path uses, since a configured value would otherwise
bypass every bound; and it reads the configuration of *whichever resource it runs in*, so the plumbing
has to feed that one.

**A secret is never carried across by any such step**, whatever else is (issue #445 §9) — that would
leave the plaintext in the environment, which is much of what the move exists to escape.

**Non-blocking advisories are a separate channel from errors.** `SystemSettingsDto.Warnings` carries
per-field advisory text keyed by the `SystemSettingsUpdate` property name — the same join key
`ApiProblem.Errors` uses. An advisory never changes the status code, never blocks a write, never sets
`aria-invalid`, and is computed *after* the write commits; a delegate that throws is swallowed and that
one advisory omitted. Raising a cap that costs memory, CPU or third-party spend should say so on the
row rather than being discovered in production. The System settings page renders it in
`OdsSettingField`'s own `Advisory` slot, which is the field block's third helper channel — distinct from
`Help` (description + provenance) and `Error`, so an advisory never displaces the line that says what the
setting does. `OdsSettingField` has no `Footer` by design: on `OdsSettingRow`, which Preferences still
uses, the advisory must go in that component's `Advisory` slot and **never `Footer`**, which is strictly
either/or with `ChildContent`.

Some things deliberately stay in deploy-time config, and the reasons are recorded in issue #421's
Non-Goals so they are not re-litigated: connection strings and secrets; `RateLimiting:*` (the
partitioner is synchronous and caches its limiter per partition key, so a changed limit never reaches a
live partition). `FileAnalysis:PromptVersion`/`PromptTemplatePath` were on that list and are now **gone from
configuration altogether** (issue #444): the template is an embedded resource in `Odyssey.Core`, its version a
`const` beside it, and every job is stamped with the template's SHA-256 — a version string that can drift from
the file it names was the worry, and a hash cannot drift. `Map:*` (issue #370) stays too: the
browser can only reach a tile host the client container's CSP allows, and nginx renders that CSP from
`MAP_ALLOWED_ORIGINS` at container start, so an admin-editable style URL would either be silently blocked
or force a CSP wide enough to defeat it. `FileAnalysis:Model` was
listed there too and **moved in issue #439**: the premise was that an editable model makes the audit trail
editable, and it does not — `FileAnalysisJob.AnalyzerModel` is written once at job creation from the value
resolved for that run and never rewritten, so a change affects which model *future* analyses use and stamp.
What that Non-Goal was really protecting is narrower and is now enforced explicitly: **the stamp must never
name a model that did not run**, so a stored model (or base URL) that cannot be used makes the analysis
*refuse* rather than silently substituting the compiled default. Substitution was the only mechanism by
which the trail could have gone wrong, and it is the one thing forbidden. `FileAnalysis:BaseUrl` moved with
it; `FileAnalysis:ApiKey` did not move *there* — it is a bearer secret, and issue #445 moved it into the
**encrypted secret store** instead, attached per request by `FileAnalysisApiKeyHandler` rather than as a
construction-time `DefaultRequestHeaders` entry (which could never have followed a rotation). The accepted
consequence (it travels to whatever base URL an admin sets) is unchanged and is mitigated by the security
claim, the audit line with a host-only projection, https-only validation, `AllowAutoRedirect = false` on
the outbound client, and a row advisory.
Issue #434 adds one more of the same class:
`FileAnalysis:TimeoutSeconds`, consumed once at startup inside `.AddStandardResilienceHandler()`, so a
runtime value could never reach a live pipeline — and worse than inert, since the options validator
rejects the handler unless `SamplingDuration >= 2 x AttemptTimeout`. Note the contrast with
`FileAnalysis:Match:TimeoutSeconds`, which *is* per-call and *did* move. Since issue #444 it is read by
**both** hosts through the shared `AddFileAnalysis` registration and bounded `[Range(10, 140)]` with
`ValidateOnStart`, because it also sizes the queue's lease; `WorkerHostTests` pins the two shipped values equal.

**Issue #8 moved the last four `Email:*` values and left the section empty.** `Email:SmtpHost`,
`SmtpPort`, `UseStartTls` and `ClientBaseUrl` were the strongest entries on that Non-Goal list, and
they moved on the strongest justification: the threat is closed **structurally**. Changing the host or
the port, or turning STARTTLS off, clears the stored `EmailUsername` and `EmailPassword` **in the same
transaction**, so there is no credential left to present to a relay it was not entered for or to put
on a cleartext wire. `EmailOptions` and the whole `Email` configuration section were **deleted** —
not deprecated, not left as documentation of record — along with every `EMAIL_*` variable in both
`.env*.example` files, both compose files and `AppHost.cs`. There is no configuration path back.
Four things about that change are easy to get wrong and are enforced in code:

- **The clear and the settings write share one transaction**, wrapped in
  `CreateExecutionStrategy().ExecuteAsync` because `EnableRetryOnFailure` is configured (a bare
  `BeginTransactionAsync` throws). `SecretSettingsService.ClearAsync` is *not* composable — it saves
  and audits itself — so the removal was split into `StageClearAsync`, which queues onto the caller's
  context and returns the audit record. Every audit line in `SystemSettingsService.UpdateAsync`, the
  settings ones included, is now emitted **after** commit. Only real MariaDB exercises any of this;
  the EF InMemory provider honours neither transactions nor the execution strategy, so the coverage
  lives in `Odyssey.IntegrationTests`.
- **Two of the three triggers are directional; the third deliberately is not.** The host clears only
  on a change to a *different, non-empty* value, and STARTTLS only on true → false — re-saving the
  same host must not cost an administrator their credential. The **port** clears on any change,
  because unlike the host it has no "off" value: every port is a live listener, so every change moves
  the credential to a different one. The port trigger came from the PR security review and goes beyond
  issue #8's G4, which named the host alone; the goal G4 states is about an *endpoint*, and a host is
  only half of one.
- **A save can trip several triggers at once**, and the audit line names all of them. It still stages
  one removal per secret — the rows are the same rows. First-match reporting under-states a compound
  change in the one record that explains why a credential vanished.
- **The send path does not use `SystemSettingsReader`.** That reader resolves an unparseable value to
  the compiled default by design; here that would substitute a TLS mode or a port nobody chose onto
  the path a reset token travels. `EmailTransportSettingsReader` returns *absent* / *valid* /
  *unusable* per key and **fails closed** on unusable — while treating an absent row as healthy
  ("mail is not configured"), which is the distinction an empty dictionary cannot make.
- **`Email:ClientBaseUrl` has no structural control** and is the accepted residual (issue #8 §10.2):
  whoever can change it receives a reset token for any address they know, another administrator's
  included. 2FA is the compensating control; the claim, the host-only audit projection, https-only
  validation (loopback exempted so the dev stack works) and a client-side origin-mismatch hint are
  the rest.
- **The shape rules are applied on the client too.** They live in `Odyssey.Dtos`, which has zero
  project references and is reachable from the WASM client, so the settings page validates a host or a
  base URL with the *same delegate* the server's descriptor uses rather than a re-implementation
  (`SettingItem.Rule`). A client-side *copy* of a server rule is the defect CLAUDE.md already forbids
  for caps; sharing the delegate is what keeps the check clear of that hazard.

Two mechanisms this added to the settings machinery, both reusable: `StringSetting.AllowEmpty`, for a
key where `""` is a meaningful value rather than a rejected clear — without it, configuring mail
would be a one-way door — and `StringSetting.ReadValidator`, which makes a semantically-invalid
stored string report as a projection fault instead of rendering as healthy. `FileAnalysisBaseUrl` has
that same blind spot today and the same property would close it.
