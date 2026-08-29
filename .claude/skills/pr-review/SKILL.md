---
name: pr-review
description: >
  Run the full multi-agent review on a GitHub pull request: dispatch the senior architect/backend
  reviewer, senior frontend reviewer, accessibility auditor, security auditor, and test agent at the
  PR, then report their consolidated findings to the user. Trigger on "review this PR", "review pull
  request N", "run the review agents on the PR", "get a full review of PR N", "architecture/frontend/
  security/accessibility/test review of a PR", or "what do the reviewers think of this PR". The
  orchestrator does NOT add its own review — it only relays the five agents' verdicts.
---

# PR Review (multi-agent)

This skill runs Odyssey's five specialist review agents against one pull request and reports back what
**they** found. It is pure orchestration: **you (the orchestrator) do not review the PR yourself, do
not add your own findings, and do not override an agent's verdict.** You dispatch, collect, and relay.

The "harness" is the **Agent tool** — there is no driver script and nothing to launch, because the
thing being driven is a set of subagents, not a running binary. Each agent does its own PR-review
(reading the diff, the code, posting its verdict comment on the PR per its standard workflow) and
returns its findings to you.

**Paths/commands below are relative to the repo root.**

## The five reviewers

| Role (user's words) | `subagent_type` | Covers |
|---|---|---|
| Senior architect & backend | `senior-architect-reviewer` | architecture, backend, data model, API contract, EF/migrations |
| Senior frontend | `senior-frontend-reviewer` | Blazor client, design-system compliance, state, API integration |
| Accessibility auditor | `accessibility-auditor` | WCAG 2.2 AA, ARIA, keyboard, contrast (frontend changes) |
| Security auditor | `appsec-security-auditor` | OWASP/ASVS, authz/claim boundaries, data exposure, secrets |
| Test agent | `senior-tester` | test coverage/quality of the changes |

All five are dispatched **every time**. The frontend and accessibility agents self-skip a backend-only
PR (they report "no frontend changes detected" rather than inventing findings) — relay that as-is.

## Step 1 — Resolve the PR

Accept a PR number/URL as the argument. With no argument, resolve the current branch's open PR:

```bash
gh pr view --json number,title,url,headRefName -q '{number,title,url,head:.headRefName}'
```

For an explicit target, `gh pr view <N> --json number,title,url,headRefName`. If neither resolves
(no PR for the branch, bad number), stop and tell the user — do not guess.

Capture what the PR touches, so each agent gets concrete context (and so you can sanity-check which
agents will have something to review):

```bash
gh pr diff <N> --name-only
```

## Step 2 — Dispatch all five agents (in parallel)

In a **single message**, make five `Agent` tool calls — one per `subagent_type` above. Run them
concurrently; they are independent. Give each the **PR number and URL**, a one-line note of what the
PR touches (from `--name-only`), and ask it to perform its standard PR review and **post its verdict
as a PR comment**, then return its findings to you.

**Model:** pass `model: "sonnet"` on every one of the five `Agent` calls so the reviewers run on the
latest Sonnet (not the orchestrator's model). This is deliberate — the review fleet uses Sonnet; do not
omit it and let the agents inherit Opus.

Keep each agent's `agentId` from its spawn result (in case the user later wants a re-review after the
PR is updated — resume the same agent with `SendMessage` so it keeps context).

Prompt skeleton for each agent (fill in N / URL / file summary):

> Perform your standard PR review of GitHub pull request #N (`<url>`) in this repo. The PR touches:
> `<short file summary>`. Review within your specialty, post your verdict comment on the PR per your
> normal workflow, and return a concise summary of your verdict and findings to me.

## Step 3 — Report findings to the user

Once all five return, relay a **consolidated report** — do not editorialize or add your own review:

- A per-agent line: **agent → verdict (✅ approved / ❌ changes requested / ⏭️ no relevant changes)**
  and its key findings (severity-tagged where the agent provided it).
- Links to the verdict comments the agents posted on the PR.
- A one-line bottom line: how many requested changes vs. approved.

If an agent errored or returned nothing, say so plainly rather than filling the gap with your own
assessment. **Do not** approve, merge, or change the PR — this skill only gathers and reports.

## Notes & gotchas

- **You are not a sixth reviewer.** The user was explicit: report the agents' findings, don't add your
  own. Resist the urge to "also notice" things — that's the agents' job.
- **Outward-facing:** the agents post public comments on the GitHub PR. That's their normal behavior
  and is expected here; just be aware the run is visible on the PR, not only in the chat.
- **Backend-only PRs:** `senior-frontend-reviewer` and `accessibility-auditor` will self-skip — that's
  a valid result, relay it; it is not an error.
- **Re-review loop:** if the author pushes changes and wants another pass, resume the same agents by
  `agentId` via `SendMessage` (they retain their first-round context) rather than spawning fresh ones.
- This is the PR-stage analog of the `odyssey-spec-writer` skill's spec-review loop; the difference is
  scope (a PR diff vs. a spec issue) and that this skill never edits the artifact under review.
