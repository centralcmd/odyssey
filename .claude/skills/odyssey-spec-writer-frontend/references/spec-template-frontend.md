# Frontend Spec Template Reference

What goes in each section of the **frontend half** of an Odyssey feature spec. Used by the
`odyssey-spec-writer-frontend` skill. Shared mechanics (Overview/Goals wording, formatting, issue
creation, cross-linking, review loop) are in `.claude/spec-writing-shared.md`.

---

## Document header

```markdown
# {{ Feature Name }} — Frontend (Draft v{{ N }})

> **Backend counterpart:** #{{ N }}
```

---

## 1. Overview

2–4 sentences: what is being built and why, plus a `### Primary user value` bullet list. Shared
verbatim with the backend issue, with one clause naming this half. Full guidance in the shared file.

---

## 2. Goals and Non-Goals

**Goals** — numbered; each a frontend deliverable. **Non-Goals (v1)** — numbered, with the version
qualifier. The backend work is *not* a Non-Goal — it is the counterpart issue.

---

## 3. User Experience

The heart of this spec. Three subsections:

- **Entry points** — where the feature appears. Name the route (plural noun: `/tax-statements`),
  the page region, the menu, or the row action. If it is a new page, give its route and where it sits
  in the nav.
- **UI states** — a numbered list of *every* state the user can land in, each named as developers will
  name it in code. Not just the happy ones:

  | Always specify | Because |
  |---|---|
  | Loading | first paint has no data |
  | Empty | a zero-row list is a healthy state, not a defect |
  | Populated | the happy path |
  | Partial / degraded | some data resolved, some didn't |
  | Error | the request failed |
  | Permission-denied | the caller lacks the gating claim |
  | Saving / submitted | in-flight writes need a disabled, announced state |

- **User flow** — numbered, step by step, through the happy path. Then a short list of the notable
  deviations and where each lands in the state list above.

Where a record or value can be **unresolvable** (an archived target, a link whose name is withheld),
say what the UI shows. Odyssey's established answer is to keep the row and drop the name — never a
GUID placeholder, never a silent removal.

---

## 4. Component Inventory

A table of every component the feature touches:

```markdown
| Component | Reused / New | Notes |
|---|---|---|
| `OdsRecordTable` | Reused | List of policies, expand-to-detail |
| `OdsModal` | Reused | The edit dialog |
| `OdsTermRangeField` | **New** | No existing atom pairs two optional dates |
```

**Check before you claim "new".** `ls Odyssey.Client/Components/Ods*.razor` and the design system's
component catalogue (README → Quick reference) between them cover most patterns. A genuinely new atom
needs a one-line justification of what no existing component can express — and, if interactive, the
full a11y specification from the checklist.

If the design system already specifies the component but it isn't built yet, say so and link the
specimen (`Odyssey Design System/components/<name>.html`).

---

## 5. API Consumption

**Link; do not restate.** The backend spec owns the contract. For each endpoint this screen calls:

```markdown
| Endpoint | When | Client behaviour |
|---|---|---|
| `POST …/analyze` | "Analyze" row action | Optimistic → Running state; poll status |
| `GET …/analysis/{id}` | While Running | Poll at 2 s; stop on terminal status |
```

Name the method and path only as a pointer — shapes, status codes and gating claims live in the
backend issue (`#N §5`). If you find yourself needing a field that section doesn't define, that is a
**gap in the backend spec**: raise it there rather than inventing it here.

Also state:

- Which typed `Odyssey.ApiClient` methods are used, or are owed
- How failures surface — `OrToast` / `ValueOrToast` / `ItemsOrToast` / `PagedOrToast` at the page call
  site, since the API client returns results and never presents them
- Any client-side cache and what invalidates it

**A server cap is never copied into a page as a `const`.** Serve it from its lookup endpoint and
interpolate the effective number into the message.

---

## 6. Accessibility

Walk `accessibility-review-checklist.md` here. Cover at minimum:

1. **New widgets** — full specification (name, ARIA roles and states, keyboard, announced async
   states, visible focus, validation association, dialog focus management), or a statement that the
   feature reuses accessible `Ods*` components only
2. **Meaning never by icon or colour alone** — where type, status or "archived" appears as text
3. **Contrast** — 4.5:1 for all text including muted and archived variants, 3:1 for focus indicators
   and meaningful icons
4. **Focus management** — what receives focus on open, on close, on validation failure
5. **Announcements** — which live regions carry loading, empty and error

State these as **MVP requirements**, not deferrals, and mirror them into §11.

---

## 7. Loading, Empty and Error States

For every failure class the backend spec names in its §9, state what the user sees:

```markdown
| Backend failure class | On screen |
|---|---|
| `FileTooLarge` (413) | Inline field error naming the effective cap, file not cleared |
| `AnalysisUnavailable` (503) | Non-blocking banner; action disabled with a reason |
```

An **empty** state is designed, not an accident: it says what would fill it and offers the action that
does. A **degraded** state says what is missing and what still works.

---

## 8. Client-Side Validation and Feedback

What is validated before submit, and how the message reads. Two rules:

- **Client validation mirrors the server's; it never replaces it.** The same constraint exists in the
  DTO's data annotations, named in the backend spec's §8.
- Errors are associated with their field (`aria-describedby`, `aria-invalid`), actionable, and focus
  moves to the first offending field.

Success feedback: state whether it is a toast, an inline confirmation, or a navigation.

---

## 9. Design System Compliance

- Tokens only — `var(--space-1…16)` on a 4px base, semantic palette tokens. **No raw hex, no raw
  `px`.**
- Valid in **both** dark (primary) and light themes.
- Brand colours (Tide teal, Sea cyan) are brand only — never income/expense, which use mint and coral.
- Numbers tabular; negatives use `−` and the expense colour.
- Any new component follows `docs/frontend-mudblazor-gotchas.md`.

Deviations are named here with the reason, not discovered in review.

---

## 10. Performance and Perceived Responsiveness

- Expected payload size and row counts; pagination or virtualization where a list can grow
- What renders before data arrives — skeleton, spinner, or nothing
- Whether writes are optimistic, and how a failed optimistic update is rolled back
- Polling intervals and their stop condition, where the feature polls

---

## 11. Acceptance Criteria

Numbered, each independently verifiable. Cover at least:

1. Happy-path flow completes end to end from the named entry point
2. Every UI state from §3 is reachable and renders as specified
3. Each backend failure class produces its specified on-screen behaviour
4. Full keyboard operability — reachable, operable, escapable, with visible focus
5. Screen-reader verification of names, roles, states and async announcements
6. Automated contrast check passes at AA, muted and archived variants included
7. No meaning conveyed by colour or icon alone
8. Renders correctly in both dark and light themes
9. A caller lacking the gating claim sees the permission-denied state, not a broken page

---

## 12. Rollout Plan

`### Phase 1 (MVP)` — a tight bullet list of what ships first. Note any dependency on the backend
issue landing before this half can be demonstrated.
