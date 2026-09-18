---
name: odyssey-spec-writer-backend
description: >
  Creates and refines the BACKEND half of a feature specification for the Odyssey .NET project — architecture,
  data model, API contract, server-side validation, security, and migrations — then publishes it as a GitHub
  issue and drives it through an architecture + security review loop. Use this whenever the user wants to spec
  out a feature that needs new or changed API endpoints, database entities, domain services, background jobs,
  permission claims, or migrations. Trigger on "spec out X", "write a spec for Y", "write the backend spec",
  "we need an endpoint for Z", "turn this idea into a spec", or "create a GitHub issue for this feature".
  Most features need this half first — it owns the API contract that the frontend spec links to. For the UI
  half (UX flow, components, accessibility) use odyssey-spec-writer-frontend. For a defect rather than a
  feature, use odyssey-bug-reporter.
compatibility:
  tools: [gh]
---

# Odyssey Spec Writer — Backend

Writes the **backend half** of a feature spec: what the server builds, stores and exposes. Publishes it
as a labelled GitHub issue, then drives it through architecture and security review to consensus.

**Read `.claude/spec-writing-shared.md` first.** It holds the entry points, the shared Overview and
Goals sections, formatting rules, issue creation, cross-linking and the review loop — all shared with
`odyssey-spec-writer-frontend` and not repeated here.

**This spec owns the API contract.** The frontend spec links to it and never restates endpoint shapes,
so §5 below is the single definition of the HTTP interface for the whole feature. Write the backend
spec **first** when a feature has both halves.

---

## Step 1 — Interview or gap analysis

Ask in conversational groups, not all at once:

1. What is the feature, in 1–2 sentences, and what problem does it solve?
2. What is explicitly out of scope for v1?
3. What new or changed **API endpoints** are needed?
4. What new **database entities** or schema changes?
5. Which **permission claim** gates each new endpoint — an existing one, or a new one?
6. Any third-party service, LLM, or outbound data flow?
7. Any performance expectations or known volume?
8. Does the feature handle file uploads or processing?

Also probe the security concerns the auditor raises on nearly every spec (see
`references/security-review-checklist.md`) — answering them now avoids a review round-trip:

- **Tenancy/ownership** — is any new entity per-user owned, or does it live in the shared
  single-tenant finance domain?
- **Read exposure** — for each new read, which fields does it expose, under which claim? Does it nest
  data normally gated by a *different* claim?
- **Write exposure** — does any write add a relationship to another entity? (→ scalar id only, never a
  nested object.)

---

## Step 2 — Write the spec

Section definitions, field-level guidance and examples: `references/spec-template-backend.md`.

**Before finalizing, walk `references/security-review-checklist.md`.** Fold an explicit decision for
each applicable item into §7 (Security) with hooks in §4, §5, §6 and §8, and reflect them as testable
acceptance criteria in §12. A stated, reasoned decision closes a finding; silence opens one.

### Required sections (in order)

| § | Section | Notes |
|---|---|---|
| 1 | Overview | Shared with the frontend issue — see the shared file |
| 2 | Goals and Non-Goals | Backend deliverables only |
| 3 | Architecture Proposal | Components + processing pipeline, no class names |
| 4 | Data Model Additions | One `###` per entity; FK relationships and on-delete behaviour |
| 5 | **API Endpoints** | **The contract.** Method, path, request, response, status codes, gating claim |
| 6 | Supported File Types and Detection | Omit unless the feature handles files |
| 7 | Security, Privacy, and Compliance | Walk the checklist here |
| 8 | Validation and Mapping Rules | Server-side rules; data annotations on DTOs |
| 9 | Error Handling and Failure Modes | Failure classes → status codes and problem details |
| 10 | Performance Targets | Scenario + metric + percentile |
| 11 | Database Migration Approach | One context, `OdysseyContext`; migration naming |
| 12 | Acceptance Criteria | Independently verifiable; no "works correctly" |
| 13 | LLM Prompt/Output Contract | Omit unless an LLM is involved |
| 14 | Rollout Plan | Phase 1 (MVP) scope |

---

## Step 3 — Review with the user, then file

Per the shared file. Label set for the backend issue:

```
claude,feature,specification,backend,security,review
```

Title suffix: `— Backend`. Create this issue **before** the frontend one, and remember to edit it
afterwards to add the `> **Frontend counterpart:** #N` line (or the explicit "no frontend
counterpart" line).

---

## Step 4 — Review loop

Per the shared file. **The approval set for a backend spec is two agents:**

| Agent | Reviews |
|---|---|
| `senior-architect-reviewer` | architecture, data model, API contract, EF/migrations, backend quality |
| `appsec-security-auditor` | OWASP/ASVS, authz and claim boundaries, data minimisation, mass-assignment |

The frontend reviewer and the accessibility auditor are **not** dispatched here — there is no UI
surface in this spec for them to read. They run on the frontend issue.

---

## Quality checklist

- [ ] Overview and Goals match the frontend issue's wording where they overlap
- [ ] Every endpoint states method, path, success status **and its gating permission claim**
- [ ] Every new entity's FK relationships and on-delete behaviour are stated
- [ ] Data annotations named for every DTO constraint that mirrors an entity limit
- [ ] Security checklist walked; each applicable item has an explicit decision in §7
- [ ] New permission claims note the role-claim mapping and the sign-out/in requirement
- [ ] No `appsettings.json` feature toggle proposed (see the template's §11 note)
- [ ] Migration approach names `OdysseyContext` and a descriptive migration name
- [ ] Acceptance criteria are independently verifiable
- [ ] Cross-reference line present (frontend counterpart, or explicitly none)
- [ ] No orphaned `TODO` or `???` left in the spec
