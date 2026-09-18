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

## Step 2 — Dispatch all five agents

**Read `.claude/agent-review-dispatch.md` for the dispatch mechanics** — the `model: "sonnet"` pin,
single-message parallel dispatch, what each agent is given, `agentId` retention, and the relay format.
Those are shared with the two spec-writer skills' review loops and are not repeated here.

Dispatch all five `subagent_type`s from the table above, giving each the PR number and URL plus the
one-line file summary from Step 1.

Prompt skeleton for each agent (fill in N / URL / file summary):

> Perform your standard PR review of GitHub pull request #N (`<url>`) in this repo. The PR touches:
> `<short file summary>`. Review within your specialty, post your verdict comment on the PR per your
> normal workflow, and return a concise summary of your verdict and findings to me.

## Step 3 — Report findings to the user

Relay the consolidated report per the shared mechanics. **Do not** approve, merge, or change the PR —
unlike the spec-writer skills' loops, this skill never edits the artifact under review.

## Step 4 — Hand off

Close with what happens next, rather than leaving the user holding five verdicts:

- If anything requested changes, **offer** to address the findings — naming which ones you would take
  and which need their call. Acting on them is a separate request; don't start pushing on the strength
  of having run the review.
- If the author pushes changes and wants another pass, resume the same agents by `agentId` (they retain
  their first-round context) rather than spawning fresh ones.

## Notes & gotchas

- **You are not a sixth reviewer.** The user was explicit: report the agents' findings, don't add your
  own. Resist the urge to "also notice" things — that's the agents' job.
- This is the PR-stage analog of the spec-writer skills' review loop; the difference is
  scope (a PR diff vs. a spec issue) and that this skill never edits the artifact under review.
