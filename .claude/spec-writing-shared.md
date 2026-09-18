# Writing an Odyssey feature spec — shared mechanics

Shared by the two spec skills. Each owns one half of a feature:

| Skill | Writes | Owns |
|---|---|---|
| `odyssey-spec-writer-backend` | The backend spec issue | Architecture, data model, **the API contract**, server-side validation, security, migrations |
| `odyssey-spec-writer-frontend` | The frontend spec issue | UX flow, component inventory, client state, accessibility, design-system compliance |

This file is the **single copy** of everything the two have in common — entry points, the shared
sections, formatting, issue creation, cross-linking and the review loop. Neither skill restates it.

## Which skill, and in what order

| The feature… | Do this |
|---|---|
| Needs both halves (most features) | **Backend first**, then frontend. The frontend spec links to the backend issue for the API contract, so the backend issue number has to exist. |
| Is backend-only (migration, job, internal endpoint) | Backend skill alone. Say in the issue that there is no frontend counterpart. |
| Is frontend-only (re-layout, a11y fix, new view over existing endpoints) | Frontend skill alone. Its **API consumption** section names the *existing* endpoints it calls. |
| You genuinely can't tell yet | Interview first (below). The answers to "new endpoints?" and "new entities?" decide it. |

Writing both halves in one session is normal. Write them as **two specs**, not one spec cut in half:
each must stand on its own for the person implementing that half.

## Two entry points

**A) From scratch.** The user has an idea or a few bullets. Interview, then write.

**B) Refining a draft.** The user has something partial or messy. Check it against the required
sections, ask only what's needed to fill the gaps, confirm your assumptions, then reformat.

Interview conversationally — grouped questions over several turns, not one wall of them. Stop asking
once you can fill every required section. Each skill lists its own interview questions.

## The two shared sections

Both issues open with these, and they are the **only** content deliberately duplicated across the
pair — they are short, and each issue has to be readable alone.

**1. Overview** — 2–4 sentences: what is being built, and why (user value). Then a
`### Primary user value` bullet list of 2–4 concrete user-facing benefits. Write it once and use the
same text in both issues, with one clause naming that issue's half.

**2. Goals and Non-Goals** — two numbered lists. Goals are deliverables *for this half*. Non-Goals
carry a version qualifier (`(v1)`). The other half's work is **not** a Non-Goal; it is the
counterpart issue, and belongs in the cross-reference instead.

## Formatting

- `## Section Name` for top-level, `### Subsection` beneath.
- Code blocks for every JSON schema, config snippet and API shape.
- `> Note:` blockquotes for speculative or TBD items.
- Precise but implementation-agnostic — avoid locking in class names unless the name is the point.
- Title carries a draft version: `# {{ Feature Name }} — Backend (Draft v1)`.
- A section that genuinely doesn't apply may be omitted, but say so:
  `> Not applicable for this feature — <reason>.` Silence reads as an oversight.

## Review with the user

Present the full spec in the conversation before filing anything. Ask whether it is complete and
accurate, and whether anything is missing or wrong. Iterate until approved.

## Creating the issue

1. Resolve the repo from the git remote — never hardcode:

```bash
gh repo view --json nameWithOwner -q .nameWithOwner
```

2. Verify the labels exist (`gh label list`) and create any that are missing:

```bash
gh label create claude --color 0075ca --description "AI-generated"
gh label create feature --color a2eeef --description "New feature"
gh label create specification --color c5def5 --description "Feature specification"
gh label create accessibility --color d4c5f9 --description "Accessibility review"
gh label create security --color d93f0b --description "Security review"
gh label create review --color fbca04 --description "Needs review"
gh label create backend --color 1d76db --description "Backend / API / data"
gh label create frontend --color 5319e7 --description "Blazor client"
```

3. Create it with a heredoc, to avoid shell escaping problems. Each skill states its own label set:

```bash
gh issue create \
  --title "{{ FEATURE_TITLE }} — {{ HALF }}" \
  --body-file - \
  --label "{{ LABELS }}" \
  --assignee "centralcmd" <<'BODY'
{{ SPEC_CONTENT }}
BODY
```

The labels are **taxonomy, not triggers** — no workflow fires on a label (`claude.yml` runs on
comments and issue open/assign; `claude-code-review.yml` only on an `@claude review` comment). They
mark what the issue is in scope for; the review loop below does the actual dispatching.

## Cross-linking the pair

Two linked issues, no parent. Because the frontend spec references the backend's API contract, the
backend issue is created first, and the link is completed in two steps:

1. **Backend issue** — created first. No counterpart line yet.
2. **Frontend issue** — created second, opening with the counterpart line under the title:
   `> **Backend counterpart:** #<backend-issue-number>`
3. **Edit the backend issue** to add the matching line
   (`gh issue edit <n> --body-file <updated>`):
   `> **Frontend counterpart:** #<frontend-issue-number>`

Do not skip step 3 — a one-way link means the backend implementer never learns the UI exists. When a
feature has only one half, say so explicitly instead:
`> **No frontend counterpart** — this feature has no UI surface.`

## The review loop

After the issue exists, drive it through its reviewer agents to consensus. **Read
`.claude/agent-review-dispatch.md` for the dispatch mechanics** — the `model: "sonnet"` pin,
single-message parallel dispatch, `agentId` retention and the relay format. Each skill names its own
reviewer set; the loop's shape is the same for both.

**Round 1 — dispatch.** Send the reviewers at the issue number, each asked to post its
specification-review verdict as a comment.

**Triage — update the spec *or* rebut.** For every finding, do exactly one of:

- **Update the spec** — edit the issue body (`gh issue edit <n> --body-file <updated-spec>`), bump the
  `Draft vN` in the title, and note which finding each change addresses ("addresses architect finding
  #3"). This is the default for valid findings.
- **Rebut in a comment** — where a finding is wrong, inapplicable, or conflicts with an explicit user
  decision, post a reasoned reply rather than silently ignoring it. When a finding's *suggestion*
  conflicts with a user decision but its underlying *risk* is real, keep the user's decision, mitigate
  the risk another way, and document the trade-off.

**Round 2 — re-review.** Resume the same agents by `agentId` and ask each to re-verify its prior
findings against the new draft and post a follow-up verdict.

**Stop condition.**

- **All reviewers approve on round 2** → the spec is cleared. Report the consensus with links to the
  verdict comments.
- **Not all approve after round 2** → **stop. Do not loop a third time automatically.** Summarize who
  still requests changes and why, and consult the user for direction.

When both halves of a feature are being specced, each issue runs its **own** loop with its own
reviewers. A finding on one half that implicates the other (most often the API contract) is raised on
**both** issues, and the fix lands on whichever issue owns that section.
