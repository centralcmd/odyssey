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

## `gh` in a web session: use the REST form

The same `gh` command does not work everywhere. In a Claude Code on the web session `gh` is installed,
but every **GraphQL-backed** subcommand is refused with
`HTTP 403: GitHub GraphQL is not available from Claude Code sessions; use the REST API` — that covers
`gh issue create/list/view/edit`, `gh pr view`, `gh pr checks`, `gh label list`, `gh repo view`,
`gh search` and `gh api graphql`. `gh api` **REST** calls go through, and so does `gh pr diff`. The
`claude-code-action` runner and a local terminal have no such restriction.

So every skill and agent that touches GitHub gives the `gh api` REST form as the **portable** path, and may
keep the friendlier subcommand as the local-terminal alternative. The `mcp__github__*` tools are a third
route where they are loaded.

Three things about `gh api` that are easy to get wrong:

- **`{owner}/{repo}` is filled in from the git remote only inside the endpoint path**, never inside a
  field value. Resolve the repo once and reuse it:
  `repo=$(gh api 'repos/{owner}/{repo}' --jq .full_name)`.
- **`-f` sends a literal string; `-F` converts `true`/`false`/`null`/integers and reads `@file`, or
  `@-` for stdin.** So a body from a file or heredoc is `-F body=@-` (with `-f` it would post the literal
  text `@-`), a milestone is `-F milestone=<number>`, and a title stays `-f` so a numeric one is not
  converted.
- **Arrays take `[]`**: `-f 'labels[]=claude' -f 'labels[]=bug'`, `-f 'assignees[]=centralcmd'`.

| Task | Portable (`gh api`, REST) | Local-terminal alternative |
|---|---|---|
| Resolve the repo | `gh api 'repos/{owner}/{repo}' --jq .full_name` | `gh repo view --json nameWithOwner -q .nameWithOwner` |
| Search open issues | `gh api -X GET search/issues -f q="repo:$repo is:issue is:open <terms>" --jq '.items[].html_url'` | `gh issue list --state open --search "<terms>"` |
| List labels | `gh api "repos/$repo/labels" --paginate --jq '.[].name'` | `gh label list` |
| Create a label | `gh api "repos/$repo/labels" -f name=<name> -f color=<hex> -f description="<text>"` | `gh label create <name> --color <hex> --description "<text>"` |
| Create an issue | `gh api "repos/$repo/issues" -f title="<title>" -F body=@- -f 'labels[]=<label>' -f 'assignees[]=<login>' -F milestone=<number> --jq .html_url <<'BODY'` … `BODY` | `gh issue create --title … --body-file - --label … --assignee … --milestone "<exact title>"` |
| Replace an issue body | `gh api -X PATCH "repos/$repo/issues/<n>" -F body=@<file> -f title="<title>"` | `gh issue edit <n> --body-file <file> --title "<title>"` |
| Comment on an issue or PR | `gh api "repos/$repo/issues/<n>/comments" -F body=@<file>` | `gh issue comment` / `gh pr comment <n> --body-file <file>` |
| View a PR | `gh api "repos/$repo/pulls/<n>" --jq '{number,title,url:.html_url,head:.head.ref}'` | `gh pr view <n> --json number,title,url,headRefName` |
| The current branch's open PR | `gh api "repos/$repo/pulls?head=${repo%%/*}:<branch>&state=open" --jq '.[].html_url'` | `gh pr view --json number,title,url` |
| Files a PR touches | `gh pr diff <n> --name-only` (REST-backed — works everywhere) | the same |

Leave `milestone` out of the create call when none fits; there is no "unset" value to send.

## Discovering the open milestones

```bash
gh api "repos/$repo/milestones" --jq '.[] | "\(.number)\t\(.title)\t\(.open_issues) open"'
```

If `gh` is unavailable altogether, use the MCP tools. No MCP tool lists milestones directly, so read them
off sibling issues — `search_issues` returns the field:

```
mcp__github__search_issues(owner, repo, query: "<area> issues", fields: ["number","title","milestone"])
```

The `milestone` object it returns carries both `title` and `number`. If nothing comes back milestoned,
ask the user rather than assuming none exists.

## Setting it

| Tool | How |
|---|---|
| `gh api` (REST) — portable | `-F milestone=<number>` on the create call, or `gh api -X PATCH "repos/$repo/issues/<n>" -F milestone=<number>` to add one after the fact |
| `gh issue create` / `gh issue edit <n>` — local terminal only | `--milestone "<exact title>"` — the title must match exactly |
| `mcp__github__issue_write` | `milestone: <number>` — the **number**, not the title (e.g. `1`) |

There is deliberately no list of current milestones here: look them up at filing time.
