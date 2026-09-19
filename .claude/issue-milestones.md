# Milestones on new issues

Shared by every surface that files a GitHub issue — the two spec-writer skills, the bug reporter,
and the review agents that open `bug` / `tech-debt` / `test-debt` / `security` / `accessibility`
issues. Read this before creating an issue; it is one step, not a separate workflow.

## The rule

**Always check the open milestones before filing, and set one when the issue plainly belongs to it.**
An unmilestoned issue is invisible to release planning, and nothing sweeps them up later — the repo
has no project board (no Projects v2 tooling is available in a Claude session), so the milestone is
the only scheduling signal an issue carries.

Do not force one. Three outcomes, in order of preference:

1. **The issue falls inside an open milestone's scope** → set it.
2. **No open milestone covers it** → leave it unset. Say so in the report to the user
   (`filed #N, no milestone — none of the open ones cover <area>`), so triage is a decision rather
   than an omission.
3. **It arguably fits, but the call is the maintainer's** (cross-cutting work, a defect that could be
   deferred, anything that would widen a milestone's scope) → file it unset and ask the user in one
   line whether to add it.

**Never create a milestone.** Creating one is a release-planning decision; propose it instead.

When a feature is filed as a backend/frontend pair, both halves get the **same** milestone or neither
does — a split pair silently drops half a feature out of the release.

## Discovering the open milestones

`gh` where it exists (the `claude-code-action` runner):

```bash
gh api repos/<owner>/<repo>/milestones --jq '.[] | "\(.number)\t\(.title)\t\(.open_issues) open"'
```

In a Claude Code on the web session there is **no `gh` and no milestone-listing MCP tool**. Read them
off sibling issues instead — `search_issues` is the only call that returns the field:

```
mcp__github__search_issues(owner, repo, query: "<area> issues", fields: ["number","title","milestone"])
```

The `milestone` object it returns carries both `title` and `number`. If nothing comes back milestoned,
ask the user rather than assuming none exists.

## Setting it

| Tool | How |
|---|---|
| `gh issue create` | `--milestone "<exact title>"` — the title must match exactly |
| `gh issue edit <n>` | `--milestone "<exact title>"` to add one after the fact |
| `mcp__github__issue_write` | `milestone: <number>` — the **number**, not the title (e.g. `1`) |

## Milestones as of 2026-09-19

| # | Title | Scope |
|---|---|---|
| 1 | `Quality of Life Update: Contracts` | contracts work — issues #121, #122, #135 |

This table is a snapshot and goes stale; it is here to show the shape (a themed, area-scoped
milestone), not to be trusted as current. Look them up at filing time.
