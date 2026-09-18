---
name: odyssey-spec-writer-frontend
description: >
  Creates and refines the FRONTEND half of a feature specification for the Odyssey Blazor WebAssembly + MudBlazor
  client — UX flow, UI states, Ods component inventory, client-side state, accessibility and design-system
  compliance — then publishes it as a GitHub issue and drives it through a frontend + accessibility + security
  review loop. Use this whenever the user wants to spec out a new page, dialog, view, or UI flow, or the UI half
  of a feature whose backend is already specced. Trigger on "spec out the UI for X", "write the frontend spec",
  "design a page for Y", "what should this screen do", or a feature request that is mostly about what the user
  sees and does. It does NOT define API endpoints — the backend spec owns the contract and this one links to it;
  use odyssey-spec-writer-backend for that half. For a defect rather than a feature, use odyssey-bug-reporter.
compatibility:
  tools: [gh]
---

# Odyssey Spec Writer — Frontend

Writes the **frontend half** of a feature spec: what the user sees, does, and is told. Publishes it as
a labelled GitHub issue, then drives it through frontend, accessibility and security review.

**Read `.claude/spec-writing-shared.md` first.** It holds the entry points, the shared Overview and
Goals sections, formatting rules, issue creation, cross-linking and the review loop — all shared with
`odyssey-spec-writer-backend` and not repeated here.

**This spec does not define the API.** The backend spec owns the contract; §4 here lists which
endpoints the client *consumes* and what it does with each, referencing the backend issue rather than
restating shapes. If the endpoints don't exist yet, the backend spec is written **first**.

**Ground the UI in the design system.** Before writing §3 and §4, read `Odyssey Design System/SKILL.md`
and the README's **Quick reference** for the component catalogue and page patterns, and
`ls Odyssey.Client/Components/Ods*.razor` for what is already built. Specifying a widget that already
exists is the most common waste here.

---

## Step 1 — Interview or gap analysis

Ask in conversational groups, not all at once:

1. What is the feature, in 1–2 sentences, and what problem does it solve for the person using it?
2. What is explicitly out of scope for v1?
3. **Where does it appear** — a new page and route, a dialog, a section on an existing page, a row
   action?
4. What does the user **do**, step by step, on the happy path?
5. What **states** can they land in — loading, empty, partial, error, permission-denied, success?
6. What data does the screen show, and what does it send back?
7. Does this reuse existing `Ods*` components, or does it need a **new** interactive widget?
8. Anything that must be visible to, or hidden from, a particular role?

Also probe the accessibility concerns the auditor raises on nearly every spec (see
`references/accessibility-review-checklist.md`) — answering them now avoids a review round-trip:

- **New widget or reuse?** A new interactive widget carries a full a11y specification; reusing an
  accessible `Ods*` component does not.
- **Meaning by colour or icon alone?** Type, status, "archived", severity must be available as text.
- **Async states** — how are loading, empty and error announced, not just drawn?

---

## Step 2 — Write the spec

Section definitions, field-level guidance and examples: `references/spec-template-frontend.md`.

**Before finalizing, walk `references/accessibility-review-checklist.md`.** Fold an explicit decision
for each applicable item into §6 (Accessibility) with hooks in §3, §5 and §7, and reflect them as
testable acceptance criteria in §11. A stated, reasoned decision closes a finding; silence opens one.

### Required sections (in order)

| § | Section | Notes |
|---|---|---|
| 1 | Overview | Shared with the backend issue — see the shared file |
| 2 | Goals and Non-Goals | Frontend deliverables only |
| 3 | User Experience | Entry points, UI states, step-by-step user flow |
| 4 | Component Inventory | Which `Ods*` components are reused; which are new and why |
| 5 | API Consumption | Which endpoints are called, and what the client does with each — **links, never restates** |
| 6 | Accessibility | Walk the checklist here; WCAG 2.2 AA |
| 7 | Loading, Empty and Error States | What each failure class from the backend spec looks like on screen |
| 8 | Client-Side Validation and Feedback | Mirrors the server's rules; never the only enforcement |
| 9 | Design System Compliance | Tokens, both themes, no raw hex or `px` |
| 10 | Performance and Perceived Responsiveness | Payload size, pagination, optimistic updates |
| 11 | Acceptance Criteria | Independently verifiable; includes the a11y criteria |
| 12 | Rollout Plan | Phase 1 (MVP) scope |

---

## Step 3 — Review with the user, then file

Per the shared file. Label set for the frontend issue:

```
claude,feature,specification,frontend,accessibility,security,review
```

Title suffix: `— Frontend`. Create this issue **after** the backend one so it can open with
`> **Backend counterpart:** #N`, then edit the backend issue to add the matching line back.

---

## Step 4 — Review loop

Per the shared file. **The approval set for a frontend spec is three agents:**

| Agent | Reviews |
|---|---|
| `senior-frontend-reviewer` | Blazor client surfaces, design-system reuse, component structure, state |
| `accessibility-auditor` | WCAG 2.2 AA, ARIA, keyboard operability, focus, contrast |
| `appsec-security-auditor` | what the UI exposes, role-conditional surfaces, client-side trust boundaries |

The architect reviewer is **not** dispatched here — the data model and API contract it reviews live in
the backend issue.

---

## Quality checklist

- [ ] Overview and Goals match the backend issue's wording where they overlap
- [ ] Every UI state named and specified — including empty, error and permission-denied
- [ ] Component inventory checked against the existing `Ods*` atoms; no accidental duplicate
- [ ] Every **new** interactive widget carries the full a11y specification from the checklist
- [ ] §5 references the backend issue for endpoint shapes and restates none of them
- [ ] Each backend failure class from the counterpart spec has a stated on-screen behaviour
- [ ] No meaning conveyed by colour or icon alone
- [ ] Both dark and light themes accounted for
- [ ] Client-side validation stated as mirroring the server, never replacing it
- [ ] A11y requirements appear as **MVP acceptance criteria**, not deferrals
- [ ] Cross-reference line present (backend counterpart, or explicitly none)
- [ ] No orphaned `TODO` or `???` left in the spec
