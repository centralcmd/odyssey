# Dispatching the review agents

Shared mechanics for the two skills that drive Odyssey's specialist review agents:

| Skill | Artifact | Agents | May edit the artifact? |
|---|---|---|---|
| `odyssey-spec-writer` (Step 5) | A spec **issue** | 3 — architect, frontend, security | **Yes** — it updates the spec and rebuts findings |
| `pr-review` | A **pull request** diff | 5 — the three above + accessibility, tester | **Never** — pure relay |

Everything below applies to both. What differs — which agents, whether the orchestrator may edit, and
the stop condition — stays in each skill. This file is the **single copy** of the mechanics; a second
copy is how the model pin below came to be stated in one skill and silently omitted in the other.

## Model

**Pass `model: "sonnet"` on every `Agent` call.** The review fleet runs on the latest Sonnet, not on
the orchestrator's model. Omitting it makes the agents inherit the orchestrator (Opus), so the same
`senior-architect-reviewer` would behave differently depending on which skill dispatched it. This is a
fleet-wide standard, not a per-skill choice — change it in this file or not at all.

## Dispatch

Make all the `Agent` calls **in a single message** so they run concurrently; the reviewers are
independent and there is no ordering between them.

Each agent gets:

- The artifact's **number and URL**.
- A one-line note of **what it touches** — the changed-file summary for a PR, the feature area for a
  spec — so the agent has concrete context and you can sanity-check which agents will have anything to
  say.
- An instruction to perform its **standard review** for its specialty, **post its verdict as a
  comment** on the issue or PR per its normal workflow, and return a concise summary to you.

## Keep the `agentId`

Retain each agent's `agentId` from its spawn result. A re-review **resumes the same agent** via
`SendMessage` rather than spawning a fresh one, so it keeps its first-round context and can say whether
its own prior findings are now addressed. A fresh agent re-derives everything and tends to re-raise
findings that were already resolved.

## Relay, don't editorialize

When the agents return, report a **consolidated result**:

- One line per agent: **agent → verdict** (✅ approved / ❌ changes requested / ⏭️ no relevant changes)
  plus its key findings, severity-tagged where the agent supplied one.
- Links to the verdict comments the agents posted.
- A one-line bottom line: how many requested changes vs. approved.

**You are not an additional reviewer.** Do not add your own findings, and do not override or soften an
agent's verdict. If an agent errors or returns nothing, say so plainly rather than filling the gap with
your own assessment.

## Self-skipping is a valid result

`senior-frontend-reviewer` and `accessibility-auditor` report "no frontend changes detected" on a
backend-only artifact rather than inventing findings. Relay that as-is — it is not an error and not a
reason to re-dispatch.

## This run is visible

The agents post public comments on the GitHub issue or PR. That is their normal behaviour and is
expected, but it means the run is outward-facing, not chat-only. Say so if the user may not expect it.
