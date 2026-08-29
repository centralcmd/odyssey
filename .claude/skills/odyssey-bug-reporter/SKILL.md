---
name: odyssey-bug-reporter
description: >
  Creates and refines well-structured, root-caused bug reports for the Odyssey .NET Blazor + MudBlazor project, then publishes them as GitHub issues. Use this skill whenever the user wants to write, draft, file, log, or improve a bug report or defect — e.g. "file a bug for X", "report this bug", "create a GitHub issue for this bug", "write up this defect", "something's broken in Y, log it", or after you discover a reproducible problem while testing. Its core strength is investigating the codebase to pin the precise root cause (file:line + snippet) and a concrete suggested fix, not just describing symptoms. Do not skip just because the user already has a rough description — gap-filling, root-cause analysis, and reformatting are core uses. For new *features* (not defects), use odyssey-spec-writer instead.
compatibility:
  tools: [gh]
---

# Odyssey Bug Reporter

Produces consistent, well-formed, **root-caused** bug reports for the Odyssey project (Blazor + MudBlazor + .NET), then files them as GitHub issues. The structure mirrors the repo's `.github/ISSUE_TEMPLATE/bug_report.yml`, enriched with the **Root cause** and **Suggested fix** sections seen in issues #157 and #158.

What sets a good Odyssey bug report apart — and what the maintainer values — is a **precise root cause** (`file:line` + a code snippet) and a **concrete suggested fix**, not just a symptom. Spend your effort there.

> Filing a *feature*, not a defect? Use `odyssey-spec-writer` instead.

---

## Two Entry Points

### A) From a fresh observation
The user hit a bug, or you found one while testing. Confirm/reproduce it, investigate the code, then write the report.

### B) Refining a rough report
The user gives a vague or partial description. Identify gaps, reproduce, investigate the root cause, then reformat to the standard structure.

Either way the output is the same: a complete, root-caused report, followed by a GitHub issue.

---

## Step 1 — Understand & Reproduce

Establish the facts. Ask the user only for what you can't determine yourself:
1. What did you observe (symptom)? What did you expect instead?
2. Exact steps that trigger it — and is it reliably reproducible?
3. Where? (Affected area — see the list in Step 3.)
4. How are you running it? (Docker Compose / Aspire / bare `dotnet run`.)
5. Any error output? (browser console, API logs, stack trace.)
6. Which commit/branch?

Then **reproduce it yourself** where feasible. A confirmed repro beats a described one.

> Don't ask everything at once — group questions, and skip any you can answer by inspecting the code or running the app.

---

## Step 2 — Investigate & Pin the Root Cause  ← the important part

This is what makes an Odyssey bug report valuable. Do not stop at the symptom.

- Trace the symptom to the responsible code: grep/read the relevant files, follow the call path.
- Identify the **exact** location: `path/File.cs:line` (or a small range) and the specific construct (e.g. `EnsureSuccessStatusCode()` throwing on a 404).
- Quote the offending code in a fenced block.
- Note any clarifying contrast (e.g. "a sibling service handles this case correctly at X").
- **Verify** your hypothesis (re-run / re-curl) before asserting it. If you can't fully confirm, say so and mark it `needs verification` — never invent a cause.
- Derive a **concrete suggested fix** from the root cause. Actionable, but don't over-prescribe; flag risk/uncertainty.

### Investigation toolkit (Odyssey-specific)
- **API directly:** `curl -i http://localhost:5188/<path>` (bypasses NGINX). Via the client origin: `http://localhost:5199/api/<path>`.
- **API logs:** `docker compose logs api --tail 100`.
- **DB:** `docker exec odyssey-mariadb mariadb -uroot -p"<MARIADB_ROOT_PASSWORD from .env>" odyssey -e "<sql>"` (the three logical DBs share one `odyssey` schema).
- **UI repro:** log in with the seeded/test user in `.env`; enter via `/` — a direct GET to `/login`, `/register`, `/manage/*`, `/auth/*` returns 405 (see #157), so don't navigate to them directly.
- **Tests:** `dotnet test Odyssey.sln` (EF InMemory; no database needed).

---

## Step 3 — Write the Bug Report

Follow `references/bug-template.md` (modeled on #157/#158). Sections, in order:

1. Context line — optional one-liner (e.g. `_Found while testing #155._`)
2. Summary
3. Metadata block — **Affected area**, **Environment**, **Version/commit**
4. Reproduction
5. Expected
6. Actual
7. Logs / console output (omit the section if none)
8. **Root cause**
9. **Suggested fix**
10. Additional context (optional)

Rules:
- `## Section` headings; fenced code blocks for snippets, config, logs, and `file:line` references.
- **Affected area** must be one of the official `bug_report.yml` values: API · Client / Frontend · Finance · Auth · File Storage · User Preferences · Database / EF Core Migrations · Docker / Infrastructure · Other / Unknown.
- Be specific and reproducible — no vague "it doesn't work."
- Mark uncertain claims with `> Note:` or "needs verification"; never present a guess as confirmed.
- Title: `bug: <concise symptom>` — lowercase after the prefix, present tense, under ~72 chars.

---

## Step 4 — Review with User

Present the full report. Confirm:
- "Does this match what you saw?"
- "Is the root cause / suggested fix right, or should I dig further?"

Iterate until approved, then Step 5.

---

## Step 5 — Create the GitHub Issue

1. Resolve the repo (don't hardcode):
```bash
gh repo view --json nameWithOwner -q .nameWithOwner
```

2. Duplicate check (the template asks reporters to confirm this):
```bash
gh issue list --state open --search "<key terms>"
```
If a clear duplicate exists, link it instead of filing a new one.

3. Label pre-check — verify labels exist, create any that are missing:
```bash
gh label list
# expected to exist: bug (#600f25), claude (#63c7ed)
# gh label create claude --color 63c7ed --description "Issues related to Claude."
```

4. Create the issue (heredoc avoids shell-escaping):
```bash
gh issue create \
  --title "bug: {{ SHORT_SYMPTOM }}" \
  --body-file - \
  --label "claude,bug" \
  --assignee "centralcmd" <<'EOF'
{{ BUG_REPORT_CONTENT }}
EOF
```

5. Report the issue URL back to the user.

### Labels, milestone, project
- **Always:** `claude` (AI-generated) + `bug`. Mirrors `odyssey-spec-writer`'s `claude,feature`.
- **Add one secondary label when it fits:** `security` (security-relevant), `build` (CI / pipeline / release), `test` (test-only), `doc` (docs). Do **not** use `fix` — that label is for non-bug changes.
- **Assignee:** `centralcmd` (repo convention).
- **Milestone (optional):** bug fixes in this repo usually belong under **"Quality of Life Update"**; use **"MVP"** only if it's release-blocking. Default to none and let triage decide — don't force one. To set it, add `--milestone "Quality of Life Update"` (title must match exactly; list with `gh api repos/<owner>/<repo>/milestones --jq '.[].title'`).
- **Project:** none configured in this repo — skip.

---

## Quality Checklist (before presenting)

- [ ] Symptom reproduced (or it's clearly stated why it couldn't be)
- [ ] Reproduction steps are concrete and ordered
- [ ] Expected vs Actual are both explicit
- [ ] Root cause pinned to `file:line` with a quoted snippet (or honestly marked unconfirmed)
- [ ] Suggested fix is actionable and risk-flagged where uncertain
- [ ] Affected area matches an official template value
- [ ] No invented causes; no orphaned TODO/??? placeholders
- [ ] Duplicate search done before filing
