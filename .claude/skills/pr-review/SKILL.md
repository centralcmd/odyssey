---
name: pr-review
description: >
  Run the full multi-agent review on a GitHub pull request: dispatch the senior architect/backend
  reviewer, senior frontend reviewer, accessibility auditor, security auditor, and test agent at the
  PR, fix what they find and re-review, up to four rounds, then report their consolidated findings to
  the user. Trigger on "review this PR", "review pull
  request N", "run the review agents on the PR", "get a full review of PR N", "architecture/frontend/
  security/accessibility/test review of a PR", or "what do the reviewers think of this PR". The
  orchestrator does NOT add its own review — it acts on and relays the five agents' verdicts.
---

# PR Review (multi-agent)

This skill runs Odyssey's five specialist review agents against one pull request, fixes what **they**
found, and re-reviews — up to **four rounds** — then reports back. **You (the orchestrator) do not
review the PR yourself, do not add your own findings, and do not override an agent's verdict.** You
dispatch, fix or rebut, re-dispatch, and relay.

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

## Step 3 — Triage: fix or rebut

After each round, for every finding, do exactly one of:

- **Fix it** — change the code on the PR's head branch, run the affected test tiers, and push a **new
  commit** (Conventional Commits, naming the finding it addresses in the body). Never amend, rebase or
  force-push. This is the default for valid findings.
- **Rebut it** — where a finding is wrong, inapplicable, or conflicts with an explicit user decision,
  reply on the PR with the reasoning rather than silently ignoring it.

Only push to a branch this session may push to (the session's development branch, or a branch the user
named). If the PR's head branch is someone else's, do not push: report the findings and stop after
round 1, offering the fixes instead.

## Step 4 — Re-review, up to four rounds

A round is one pass of the reviewers followed by your triage. Round 1 is Step 2; rounds 2–4 resume the
same agents by `agentId` and ask each to re-verify its prior findings against the new commits (and
review anything they changed), posting a follow-up verdict. Re-dispatch only the agents that did not
approve in the previous round; an approval stands unless a later commit touches that agent's area, in
which case resume it too. Four is a ceiling, not a target.

**Stop condition** — check after each round, in this order:

- **All agents approve** (or self-skip) → report the consensus with links to the verdict comments.
- **A finding needs user input** — a product decision, two agents disagreeing, or a conflict with an
  explicit user decision a rebuttal cannot settle → stop and consult the user, whatever the round.
- **Not all approve after round 4** → **stop. Do not start a fifth round automatically.** Summarize who
  still requests changes and why, and consult the user.

## Step 5 — Report

Relay the consolidated result per the shared mechanics, plus a per-round line: what was fixed (commit
SHAs), what was rebutted, and who approved in that round. **Never** approve or merge the PR.

## Notes & gotchas

- **You are not a sixth reviewer.** The user was explicit: report the agents' findings, don't add your
  own. Resist the urge to "also notice" things — that's the agents' job.
- This is the PR-stage analog of the spec-writer skills' review loop; the difference is scope (a PR
  diff vs. a spec issue) and that fixes land as commits rather than a new spec draft.
