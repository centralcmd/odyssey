# Security Review Checklist (backend spec)

The security, privacy and compliance concerns the Odyssey security auditor raises on almost every
spec. **Address them upfront, inside the relevant sections** — mostly §7, with hooks in §4, §5, §8,
§9 and §12 — so the spec clears review without a round-trip.

For each item: make an **explicit decision** in the spec, even "N/A — here's why". A stated, reasoned
decision closes a finding; silence opens one.

Anchor to OWASP (Top 10, ASVS, WSTG) and, where data subjects are involved, GDPR / ISO 27001 /
Norwegian Sikkerhetsloven.

> The accessibility half of the old combined checklist now lives with the frontend skill, at
> `.claude/skills/odyssey-spec-writer-frontend/references/accessibility-review-checklist.md`.

---

## 1. Tenancy and ownership model — state it explicitly

The Odyssey **finance domain is a shared, single-tenant workspace**: `Account`, `Contact`,
`Transaction` and friends have **no per-user owner column**. Access is governed entirely by
**permission claims**, not row ownership. State this in §7 so reviewers don't chase **cross-user
IDOR** false positives — there is no cross-user object boundary to violate in the finance domain.

- **Exception that flips it:** an entity that genuinely *is* per-user owned (scoped to one user).
  Call that out and require an ownership check on every read and write — that really is an IDOR
  surface. `UserProfileImage` is the worked example: no administrator write path at all, so the
  mitigation is structural rather than a check.

## 2. Read-path claim crossover / over-exposure (data minimisation)

When a read endpoint **nests or returns a DTO normally gated behind a *different* claim**, you erode
that boundary — an `accounts.read`-only caller reading data that should need `contacts.read`. This is
the single most common finding.

- **Default:** project a **purpose-built minimal DTO** for the nested data. Do **not** reuse a fuller
  existing DTO carrying fields — especially **free text** or **PII** — reachable today only under
  another claim.
- In §5 and §7, **list exactly which fields each read exposes** and under which claim, and justify
  each. Drop large notes/description fields from cross-claim projections.

## 3. Write-path crossover / mass-assignment (over-posting)

Request DTOs carry **only scalar values and FK ids the caller may set** — never a **nested
related-entity object** that could over-post or mutate a different entity.

- **Default:** a link to another entity is set by its **scalar id** (`CustodianId`), never by
  accepting the nested object.
- Pin this as an explicit invariant in §4, and add an acceptance criterion in §12 proving a populated
  nested object in the request body does not create or mutate the related entity.

## 4. Error-message information disclosure (existence oracle)

Echoing identifiers in 4xx bodies can be an existence oracle.

- **Default for Odyssey:** echoing an **opaque GUID** is acceptable — GUIDs aren't enumerable and the
  finance domain is single-tenant, so it isn't a meaningful oracle. Say so in §7/§9 to pre-empt the
  finding.
- **Be cautious** with sequential or enumerable ids, emails, or anything confirming another user's
  data exists — prefer a generic message. The worked cautionary case is the profile-image read path,
  where distinguishing "disabled" from "locked out" would have leaked a password-spray oracle.

## 5. Permission claims — reuse vs new

- **Reusing** a claim: verify the data the endpoint exposes or mutates genuinely sits inside that
  claim's boundary (ties back to #2 and #3). State which claim gates each endpoint.
- **New** claim: add the constant to `PermissionClaims`, then to `RolePermissions.AllClaims` and every
  role that should hold it. **No migration** — `RoleClaimSeeder` reconciles the rows at runtime. Note
  in the spec that **users must sign out and back in**; claims are baked into the auth cookie at
  login, so a refresh is not enough.
- A claim that would be granted to every role still buys something: it is a **revocation lever that
  exists before release**, and retrofitting one later de-authorizes live sessions.

## 6. PII and data minimisation

Identify which new or newly-exposed fields are **personal data**, and expose the minimum needed.

- Norwegian **organisasjonsnummer** is a public business-register identifier — generally fine to
  expose. **But** where a record can represent a **natural person or sole proprietor**, it edges
  toward personal data; prefer least-data.
- Where a feature stores personal data, say how **erasure** works. Odyssey's pattern is an FK chain
  that cascades inside the existing delete transaction, rather than bespoke purge code.

## 7. Audit trail

State whether the change needs an audit surface, or explicitly why not ("account changes are not
currently versioned; no new audit surface for v1").

Where a **secret** is written, the audit line is unconditional and **never echoes the value** — the
presence or absence of a line would itself be a plaintext equality oracle.

---

## Secrets and configuration

A credential, API key or HMAC key belongs in the encrypted `SystemSettingSecrets` store, declared in
`SecretSettingsRegistry` — never in `appsettings.json`, and never carried across from configuration.
The three read states (`Found` / `NotSet` / `Unreadable`) are distinct, and `Unreadable` must never
collapse into `NotSet`. State which way the consumer fails.

An operator-adjustable **non-secret** value is a row in the `SystemSettings` store, admin-editable at
`/settings`, with its own `[Range]`, bound pair and registry descriptor. It is **not** an
`appsettings.json` key, and it is **not** a per-feature kill switch — Odyssey gates capabilities with
permission claims.

---

## How to apply

- During the interview, ask the tenancy/ownership question, what each new read exposes and under which
  claim, and whether any write adds a relationship.
- While writing §7, walk items 1–7; put a one-line explicit decision for each that applies, and
  `> Not applicable — <reason>` for those that don't.
- Reflect each decision back as a **testable acceptance criterion** in §12.
