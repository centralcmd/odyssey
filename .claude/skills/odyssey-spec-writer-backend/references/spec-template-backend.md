# Backend Spec Template Reference

What goes in each section of the **backend half** of an Odyssey feature spec. Used by the
`odyssey-spec-writer-backend` skill. Shared mechanics (Overview/Goals wording, formatting, issue
creation, cross-linking, review loop) are in `.claude/spec-writing-shared.md`.

---

## Document header

```markdown
# {{ Feature Name }} — Backend (Draft v{{ N }})

> **Frontend counterpart:** #{{ N }}
```

---

## 1. Overview

2–4 sentences: what is being built and why, plus a `### Primary user value` bullet list. Shared
verbatim with the frontend issue, with one clause naming this half. Full guidance in the shared file.

---

## 2. Goals and Non-Goals

**Goals** — numbered; each a backend deliverable. **Non-Goals (v1)** — numbered; each explicitly
deferred, with the version qualifier. The frontend work is *not* a Non-Goal — it is the counterpart
issue.

---

## 3. Architecture Proposal

High-level design: components and data flow, no implementation detail.

- **High-level components** — numbered, one line each. Logical names ("Import Service"), not class names.
- **Processing pipeline** — numbered, ordered, end to end.

Say where each component lives in the solution — `Odyssey.Api/Controllers/`, `Odyssey.Core/Finance/`,
`Odyssey.Context/` — so a reader can find the seam without guessing.

---

## 4. Data Model Additions

One `###` subsection per new entity. Fields as bullets, each with name, type or FK relationship, and a
note when non-obvious.

**State the on-delete behaviour for every relationship.** Odyssey declares real foreign keys and the
choice is load-bearing: `SET NULL` for optional links and user attribution, `CASCADE` for owned
children, `RESTRICT` where a link must block deletion.

```markdown
### `FileAnalysisJob`
- `Id`
- `FileId` (FK → `FileMetadata`, CASCADE)
- `RequestedByUserId` (FK → `AspNetUsers`, SET NULL — the job outlives its requester)
- `Status` (required enum; `0=Queued`, `1=Running`, `2=Completed`, `3=Failed`)

> Note: MVP does not include a separate ImportedTransactionLink table.
```

When a write adds a relationship, pin the **scalar-id-only** invariant here and prove it with an
acceptance criterion in §12.

---

## 5. API Endpoints — the contract

**This section is the single definition of the HTTP interface for the whole feature.** The frontend
spec links here; it does not restate shapes. Anything missing from this section is missing for the
frontend implementer too.

Numbered list. Each entry:

- HTTP method + path — **plural resource nouns** (`/api/insurance-policies`, not `/api/insurance-policy`)
- One-line description
- Request body shape (code block) where there is one
- Success status code and response shape
- Notable error statuses
- **The permission claim that gates it**

```markdown
1. `POST /api/accounts/{accountId}/files/{fileId}/analyze`
   - Starts an analysis job for one account-attached file.
   - Gated on `file-analysis.create`.
   - Returns `202 Accepted` + `{ "analysisJobId": "<guid>" }`.
   - `404` when the file is not attached to that account; `409` when a job is already running.
```

For a list endpoint, state the query-string binding model and its `QueryParams<TSortBy>` sort keys,
`Search` max length and `Offset`/`Limit` ranges.

---

## 6. Supported File Types and Detection

Only when the feature handles file uploads or processing. Otherwise
`> Not applicable for this feature — <reason>.`

- **Allowed MIME types / extensions (v1)** — table or bullets
- **Detection strategy** — numbered; magic-byte checks before trusting a declared content type

---

## 7. Security, Privacy, and Compliance

Walk `security-review-checklist.md` here. Numbered list covering at minimum:

1. **Tenancy and ownership** — shared single-tenant finance domain, or a per-user owned entity
2. **Permission claims** — which claim gates each endpoint; reused or new, and why
3. **Read-path exposure** — the fields each read returns, and the claim each sits behind
4. **Write-path exposure** — scalar-id-only invariant for every new relationship
5. **Third-party data flow** — what leaves the deployment, to whom, under what consent
6. **Secrets** — any credential goes in the encrypted `SystemSettingSecrets` store, never configuration
7. **Audit trail** — needed, or explicitly why not
8. **Error-message disclosure** — what identifiers 4xx bodies echo

A new claim must note the `RolePermissions` mapping **and** that existing sessions only pick it up
after a sign-out/sign-in — claims are baked into the auth cookie at login.

---

## 8. Validation and Mapping Rules

- **Validation rules** — required fields, ranges, formats, uniqueness. Name the **data annotation**
  carrying each one (`[StringLength(256)]`, `[Range(1, 500)]`), since DTO constraints must mirror the
  entity's limits rather than relying on the database alone.
- **Mapping to the internal model** — how inbound data maps onto existing domain entities.

A bound that is a compile-time constant belongs in an attribute; only a runtime or cross-assembly
bound needs a validator. Say which applies.

---

## 9. Error Handling and Failure Modes

- **Expected failure classes** — named, one line each
- **Status code and problem-details shape per class** — what the API returns
- **Retry and fallback behaviour** — for outbound calls and background work

The frontend spec decides how each of these *appears to the user*; this section decides what the API
*returns*. Name each failure class precisely enough that the frontend spec can reference it by name.

---

## 10. Performance Targets

Bullets: scenario + target metric + percentile. Include any hard timeout.

```markdown
- Small CSV (<2 MB): job completes under 10 s (P50).
- Medium PDF (2–10 MB): under 60 s (P50).
- Hard per-job timeout: 5 minutes.
```

---

## 11. Database Migration Approach

- Confirm EF Core migrations against **`OdysseyContext`** — one context, one migrations folder
  (`Odyssey.Context/Migrations/`), one `__EFMigrationsHistory`
- The `dotnet ef migrations add` invocation, with a descriptive feature-scoped name
- Whether the migration seeds any row, and what
- Confirm the migration is committed with the feature branch

> **No feature toggles.** Odyssey does **not** gate new features behind an `appsettings.json` flag
> returning `503` when off — capabilities are gated by **permission claims**. If the feature genuinely
> needs an operator-adjustable value, it is a row in the `SystemSettings` store (admin-editable at
> `/settings`), not a configuration key, and adding one has its own recipe in `CLAUDE.md`. Secrets go
> in `SystemSettingSecrets`. State which of the three applies.

---

## 12. Acceptance Criteria

Numbered, each independently verifiable — never "works correctly" or "handles errors". Cover at least:

1. Happy-path request/response works end to end
2. Authorization enforced on every endpoint — the right claim admits, a missing claim gets `403`
3. Each named failure class returns its specified status code
4. Validation rejects each out-of-range or malformed input with `400`
5. A populated **nested** related object in a request body does not create or mutate that entity
6. The migration applies cleanly and the FK on-delete behaviour matches §4
7. At least one real fixture exercised, where the feature ingests data

---

## 13. LLM Prompt/Output Contract

Only when an LLM is involved; otherwise `> Not applicable for this feature.`

- **Principles** — prompt and output design rules
- **Output schema** — JSON code block with field types
- **Guardrails** — retries, truncation, refusal and malformed-output handling

---

## 14. Rollout Plan

`### Phase 1 (MVP)` — a tight bullet list of what ships first. This is the commit; keep later phases
brief.
