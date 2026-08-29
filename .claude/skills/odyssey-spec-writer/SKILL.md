---
name: odyssey-spec-writer
description: >
  Creates and refines feature specifications for the Odyssey .NET Blazor + MudBlazor project, then publishes them as GitHub issues. Use this skill whenever the user wants to write, draft, improve, or review a feature specification, technical spec, or design document. Also trigger when the user says things like "let's spec out X", "write a spec for Y", "create a feature doc", "refine this spec", "turn this idea into a spec", or "create a GitHub issue for this feature". Always use this skill when the output should end up as a GitHub issue. Do not skip this skill just because the user already has a rough draft — reformatting and gap-filling are core use cases.
compatibility:
  tools: [gh]
---

# Odyssey Spec Writer

Produces consistent, well-formed feature specifications for the Odyssey project (Blazor + MudBlazor + .NET), creates a labeled GitHub issue from the result, then drives it through an automated multi-agent review loop (architecture, frontend, security) to consensus.

---

## Two Entry Points

### A) Creating from scratch
User has an idea, a rough description, or a few bullet points. Interview them, then build the spec.

### B) Refining an existing draft
User provides a partial or messy spec. Identify gaps using the required sections checklist, ask targeted questions to fill them, then reformat to the standard structure.

In both cases the output is the same: a complete spec in the standard format, a labeled GitHub issue, and an automated review loop that drives it to consensus (Step 5).

---

## Step 1 — Interview (from scratch) or Gap Analysis (existing draft)

### From scratch — ask the user:
1. What is the feature? (1–2 sentence summary)
2. What problem does it solve / what is the user value?
3. What is explicitly out of scope for this version?
4. Are there new API endpoints needed?
5. Are there new database entities or schema changes?
6. Are there UI/UX flows to describe?
7. Any security, privacy, or compliance concerns?
8. Any performance expectations?
9. Are there configuration or feature-toggle requirements?

Also probe the **cross-cutting concerns** that the security and accessibility auditors raise on almost
every spec (see `references/cross-cutting-review-checklist.md`) — addressing them now avoids review
round-trips:
- **Tenancy/ownership:** is any new entity per-user owned, or does it live in the shared single-tenant finance domain?
- **Read exposure:** for each new read, what fields does it expose and under which permission claim? Does it nest data normally gated by a *different* claim?
- **Write exposure:** does any write add a relationship to another entity (→ scalar-id-only, no nested over-posting)?
- **New UI widget:** does the feature add a *new* interactive widget (e.g. a custom picker/combobox), or reuse an existing accessible Ods component?

Do not ask all at once — use conversational turns. Group related questions. Stop asking when you have enough to fill all required sections.

### Existing draft — gap analysis:
Read the draft and check which required sections (see Step 2) are missing or thin. Ask only the questions needed to fill the gaps. Confirm any assumptions you make.

---

## Step 2 — Write the Spec

Follow this exact section structure. All sections are required unless marked optional.

See `references/spec-template.md` for the full section definitions, field-level guidance, and examples.

**Before finalizing, walk `references/cross-cutting-review-checklist.md`** — the recurring
security/privacy and accessibility concerns the auditors raise on nearly every spec (tenancy &
ownership model, read-path claim crossover / over-exposure, write-path mass-assignment, error-message
disclosure, new-widget accessibility, meaning-not-by-colour-alone, contrast). Fold an **explicit
decision** for each applicable item into the relevant sections (§3, §6, §7, §9, §10, §11) and reflect
them as testable acceptance criteria in §16. A stated, reasoned decision closes a finding; silence
opens one.

### Required sections (in order):
1. Overview
2. Goals and Non-Goals
3. User Experience (skip if purely backend/infra feature)
4. Supported File Types and Detection (skip if not file-related)
5. Architecture Proposal
6. Data Model Additions
7. API Endpoints
8. LLM Prompt/Output Contract (skip if no LLM involved)
9. Validation and Mapping Rules
10. Security, Privacy, and Compliance Considerations
11. Error Handling and Fallback Behavior
12. Performance Targets
13. Rollout Plan
14. Feature Toggle and Provider Configuration
15. Database Migration Approach
16. Suggested Acceptance Criteria for MVP

### Rules:
- Use `## Section Name` for top-level sections, `### Subsection` for subsections.
- Use `## Subsection` (not `###`) for subsections within a `##` section when following the reference example pattern.
- Code blocks for all JSON schemas, config snippets, and API shapes.
- Mark speculative or TBD items with `> Note:` blockquotes.
- Keep language precise and implementation-agnostic where possible — avoid locking in class names unless necessary.
- Version the draft: start at `Draft v1` in the title.
- Sections that genuinely do not apply to the feature may be omitted, but call that out with a one-liner: `> Not applicable for this feature.`

---

## Step 3 — Review with User

Present the full spec in the conversation. Ask:
- "Does this look complete and accurate?"
- "Anything missing or incorrect?"

Iterate until the user approves. Then proceed to Step 4.

---

## Step 4 — Create GitHub Issue

Once the spec is approved:

1. Resolve the repo from git remote (do not hardcode):
```bash
gh repo view --json nameWithOwner -q .nameWithOwner
```

2. Create the issue using a heredoc to avoid shell escaping problems. **Label it with the full review
   trigger set** — `specification,accessibility,security,review` — in addition to `claude,feature`:
```bash
gh issue create \
  --title "{{ FEATURE_TITLE }}" \
  --body-file - \
  --label "claude,feature,specification,accessibility,security,review" \
  --assignee "centralcmd" <<'EOF'
{{ SPEC_CONTENT }}
EOF
```

3. Report the issue URL back to the user, then proceed to **Step 5** (automated review loop).

### Label pre-check:
Before creating the issue, verify the labels exist:
```bash
gh label list
```
Create any that are missing:
```bash
gh label create claude --color 0075ca --description "AI-generated"
gh label create feature --color a2eeef --description "New feature"
gh label create specification --color c5def5 --description "Feature specification"
gh label create accessibility --color d4c5f9 --description "Accessibility review"
gh label create security --color d93f0b --description "Security review"
gh label create review --color fbca04 --description "Needs review"
```

---

## Step 5 — Automated Multi-Agent Review Loop

After the issue exists, run it through the reviewer agents and drive it to consensus. **The approval
set is exactly three agents:** `senior-architect-reviewer`, `senior-frontend-reviewer`, and
`appsec-security-auditor`. (The `accessibility` label is applied for the accessibility-auditor's own
trigger, but that agent is **not** part of this skill's gating approval set — do not block on it.)

### Round 1 — dispatch
Dispatch all three reviewer agents (in parallel) via the Agent tool, each pointed at the issue number
and asked to post its specification-review verdict as a comment on the issue:
- `senior-architect-reviewer` — architecture, backend, data model, API contract, EF/migrations.
- `senior-frontend-reviewer` — Blazor client surfaces, design-system reuse, state, a11y intersection.
- `appsec-security-auditor` — OWASP/ASVS, authz/claim boundaries, data minimisation, mass-assignment.

Keep each agent's `agentId` from its spawn result — you will resume the same agent for re-review so it
keeps its review context.

### Triage — update the spec *or* rebut
For every finding across the three reviews, do **one** of:
- **Update the spec** — edit the issue body (`gh issue edit <n> --body-file <updated-spec>`), bump the
  `Draft vN` version in the title line, and note which finding each change addresses (e.g.
  "addresses architect finding #3"). This is the default for valid findings.
- **Rebut in a comment** — if you believe a finding is wrong, inapplicable, or conflicts with an
  explicit user decision, post a reasoned reply comment on the issue explaining why, rather than
  silently ignoring it. (When a finding's *suggestion* conflicts with a user decision but its
  underlying *risk* is real, prefer keeping the user's decision and mitigating the risk another way —
  document that trade-off.)

### Round 2 — re-review
Once the spec is updated, **resume the same three agents** (via SendMessage with their `agentId`, so
they retain context) and ask each to re-verify its prior findings against the new draft and post a
follow-up verdict comment.

### Stop condition
- **All three approve on round 2** → the spec is cleared; report the consensus to the user with links
  to the verdict comments.
- **Not all three approve after round 2** → **stop. Do not loop a third time automatically.** Summarize
  the outstanding findings (who still requests changes and why) and **consult the user** for direction.

> Note: This loop gates on the three named reviewers only. If the user separately asks for the
> accessibility-auditor or senior-tester, run them, but they do not change this skill's stop condition.

---

## Quality Checklist (self-check before presenting spec)

- [ ] All required sections present (or explicitly marked N/A)
- [ ] Goals and Non-Goals are specific, not vague
- [ ] Data model has all FK relationships noted
- [ ] API endpoints include HTTP method, path, response code
- [ ] Acceptance criteria are testable (not "works correctly")
- [ ] Feature toggle section present if any config is needed
- [ ] Migration approach specified if schema changes exist
- [ ] Security section addresses data sent to third-party services (if any)
- [ ] Cross-cutting checklist walked (`references/cross-cutting-review-checklist.md`):
  - [ ] Tenancy/ownership model stated (shared single-tenant vs per-user owned → IDOR surface or not)
  - [ ] Each new read lists exposed fields + gating claim; no cross-claim over-exposure (minimal projection, free-text/PII dropped)
  - [ ] Any new relationship is set by scalar id only — no nested over-posting (invariant + acceptance test)
  - [ ] Error-message id-echo decision stated (opaque GUID OK; caution for enumerable ids)
  - [ ] New permission claims note role-claim migration + re-login; reused claims justified
  - [ ] New UI widgets specify a11y (name, ARIA roles/states, keyboard, announced states, focus, validation) or reuse an accessible Ods component
  - [ ] No meaning by icon/colour alone (type/status/archived in text); contrast targets stated
- [ ] No orphaned `TODO` or `???` placeholders left in spec
