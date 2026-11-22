# Persistent agent memory

Shared instructions for every agent under `.claude/agents/`. Each agent's own file names its memory directory,
`$CLAUDE_PROJECT_DIR/.claude/agent-memory/<agent-name>/`, and says what is worth recording for its role; this file
says how the memory works. Create the directory if it does not exist, then write to it with the Write tool. It is
git-ignored (`.gitignore`), so each contributor keeps their own — it is **not** shared through version control.

Build up this memory over time so that future conversations have a complete picture of who the user is, how they
like to collaborate, what to avoid or repeat, and the context behind the work they give you.

If the user explicitly asks you to remember something, save it immediately as whichever type fits best. If they ask
you to forget something, find and remove the relevant entry.

## Before you start

Read `MEMORY.md` in your directory (it may already be in your context) and open the entries relevant to the task.
Keep the index concise: lines after 200 are truncated when it is loaded.

## Types of memory

<types>
<type>
    <name>user</name>
    <description>Information about the user's role, goals, responsibilities, and knowledge, so you can tailor your behaviour to their preferences and perspective — you collaborate with a senior engineer differently than with someone coding for the first time. Avoid memories that could read as a negative judgement or that are not relevant to the work.</description>
    <when_to_save>When you learn any details about the user's role, preferences, responsibilities, or knowledge.</when_to_save>
    <how_to_use>When your work should be informed by the user's profile or perspective — for example, explaining code in terms of domain knowledge they already have.</how_to_use>
    <examples>
    user: I've been writing Go for ten years but this is my first time touching the React side of this repo
    assistant: [saves user memory: deep Go expertise, new to React and this project's frontend — frame frontend explanations in terms of backend analogues]
    </examples>
</type>
<type>
    <name>feedback</name>
    <description>Guidance the user has given about how to approach work — both what to avoid and what to keep doing. Record from failure AND success: if you only save corrections, you avoid past mistakes but drift away from approaches the user already validated, and may grow overly cautious.</description>
    <when_to_save>Any time the user corrects your approach ("no not that", "don't", "stop doing X") OR confirms a non-obvious approach worked ("yes exactly", "perfect, keep doing that", accepting an unusual choice without pushback). Confirmations are quieter than corrections — watch for them. Save what applies to future conversations, especially if surprising or not obvious from the code.</when_to_save>
    <how_to_use>Let these memories guide your behaviour so that the user does not need to offer the same guidance twice.</how_to_use>
    <body_structure>Lead with the rule itself, then a **Why:** line (the reason the user gave — often a past incident or strong preference) and a **How to apply:** line (when/where this guidance kicks in). Knowing *why* lets you judge edge cases instead of blindly following the rule.</body_structure>
    <examples>
    user: don't mock the database in these tests — we got burned last quarter when mocked tests passed but the prod migration failed
    assistant: [saves feedback memory: integration tests must hit a real database, not mocks. Reason: prior incident where mock/prod divergence masked a broken migration]

    user: yeah the single bundled PR was the right call here, splitting this one would've just been churn
    assistant: [saves feedback memory: for refactors in this area, user prefers one bundled PR over many small ones. Confirmed after I chose this approach — a validated judgment call, not a correction]
    </examples>
</type>
<type>
    <name>project</name>
    <description>Ongoing work, goals, initiatives, bugs, or incidents within the project that are not derivable from the code or git history — the broader context and motivation behind the work.</description>
    <when_to_save>When you learn who is doing what, why, or by when. These change quickly, so keep them current. Always convert relative dates to absolute ones when saving ("Thursday" → "2026-03-05"), so the memory stays interpretable.</when_to_save>
    <how_to_use>To understand the nuance behind a request and make better-informed suggestions.</how_to_use>
    <body_structure>Lead with the fact or decision, then a **Why:** line (the motivation — often a constraint, deadline, or stakeholder ask) and a **How to apply:** line (how this should shape your suggestions). Project memories decay fast, so the why helps future-you judge whether the memory is still load-bearing.</body_structure>
    <examples>
    user: the reason we're ripping out the old auth middleware is that legal flagged it for storing session tokens in a way that doesn't meet the new compliance requirements
    assistant: [saves project memory: auth middleware rewrite is driven by legal/compliance requirements around session token storage, not tech-debt cleanup — scope decisions should favor compliance over ergonomics]
    </examples>
</type>
<type>
    <name>reference</name>
    <description>Pointers to where information can be found in systems outside the project directory.</description>
    <when_to_save>When you learn about a resource in an external system and its purpose — for example, that a class of bugs is tracked in a specific place, or that a dashboard is what on-call watches.</when_to_save>
    <how_to_use>When the user references an external system or information that may live in one.</how_to_use>
    <examples>
    user: the Grafana board at grafana.internal/d/api-latency is what oncall watches — if you're touching request handling, that's the thing that'll page someone
    assistant: [saves reference memory: grafana.internal/d/api-latency is the oncall latency dashboard — check it when editing request-path code]
    </examples>
</type>
</types>

Your agent file's role-specific examples say which of these are most worth recording for your role. They are
project-scoped: tailor memories to Odyssey.

## What NOT to save

- Code patterns, conventions, architecture, file paths, or project structure that can be derived by reading the
  current tree.
- Git history, recent changes, or who changed what — `git log` / `git blame` are authoritative.
- Debugging solutions or fix recipes — the fix is in the code; the commit message has the context.
- Anything already documented in a `CLAUDE.md`, under `docs/`, or elsewhere under `.claude/`.
- Ephemeral task details: in-progress work, temporary state, current conversation context.

These exclusions apply even when the user explicitly asks you to save. If they ask you to save a PR list or activity
summary, ask what was *surprising* or *non-obvious* about it — that is the part worth keeping.

A role-specific example that names a code fact is worth keeping only for what the code does not say: *why* it is
that way, the trap it sets, or where to look first.

## How to save

Saving a memory is two steps.

**Step 1** — write the memory to its own file (e.g. `user_role.md`, `feedback_testing.md`) with this frontmatter:

```markdown
---
name: {{short-kebab-case-slug}}
description: {{one-line summary — used to decide relevance in future conversations, so be specific}}
metadata:
  type: {{user, feedback, project, reference}}
---

{{memory content — for feedback/project types: rule/fact, then **Why:** and **How to apply:** lines. Link related memories with [[their-name]].}}
```

In the body, link to related memories with `[[name]]`, where `name` is the other memory's `name:` slug. Link
liberally — a `[[name]]` with no matching memory yet is fine; it marks something worth writing later.

**Step 2** — add a pointer to that file in `MEMORY.md`. `MEMORY.md` is an index, not a memory: one line per entry,
under ~150 characters, `- [Title](file.md) — one-line hook`, no frontmatter. Never write memory content directly
into `MEMORY.md`.

- Keep the name, description, and type fields up to date with the content.
- Organize by topic, not chronologically.
- Update or remove memories that turn out to be wrong or outdated.
- Do not write duplicates — check for an existing memory to update first.

## When to access memories

- When memories seem relevant, or the user references prior-conversation work.
- You MUST access memory when the user explicitly asks you to check, recall, or remember.
- If the user says to *ignore* or *not use* memory, do not apply, cite, compare against, or mention memory content.
- Memories go stale. Treat them as what was true when written; before acting on one, verify it against the current
  files or resources. If it conflicts with what you observe now, trust the observation and update or remove the
  memory.

## Before recommending from memory

A memory that names a function, file, or flag is a claim that it existed *when the memory was written*. It may have
been renamed, removed, or never merged. Before recommending it:

- If it names a file path, check the file exists.
- If it names a function or flag, grep for it.
- If the user is about to act on your recommendation (not just asking about history), verify first.

"The memory says X exists" is not the same as "X exists now." A memory that summarizes repo state (activity logs,
architecture snapshots) is frozen in time; for *recent* or *current* state, prefer `git log` or the code. This is the
same rule root `CLAUDE.md` applies to cited precedents.

## Memory and other forms of persistence

Memory is for what will be useful in **future** conversations, not for state that only matters within this one.

- Use a plan, not memory, to align with the user on the approach to a non-trivial task, and update the plan when the
  approach changes.
- Use tasks, not memory, to break the current work into steps and track progress.
</content>
</invoke>
