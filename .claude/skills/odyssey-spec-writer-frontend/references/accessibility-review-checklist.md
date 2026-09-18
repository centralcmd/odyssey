# Accessibility Review Checklist (frontend spec)

The accessibility concerns the Odyssey accessibility auditor raises on almost every spec with UI.
**Address them upfront, inside the relevant sections** — mostly §6, with hooks in §3, §5, §7 and §11 —
so the spec clears review without a round-trip.

For each item: make an **explicit decision** in the spec, even "N/A — here's why". A stated, reasoned
decision closes a finding; silence opens one.

Target is **WCAG 2.2 Level AA**, also anchored to EN 301 549, Section 508 and Norway's *Forskrift om
universell utforming av IKT*. The UI is Blazor + MudBlazor on the Odyssey design system (`Ods*`
components).

> The security half of the old combined checklist now lives with the backend skill, at
> `.claude/skills/odyssey-spec-writer-backend/references/security-review-checklist.md`.

---

## 1. Reuse an accessible component, or fully spec a new widget

Prefer reusing an existing accessible `Ods*` / design-system component. A **new interactive widget** —
for instance a custom searchable combobox rather than reusing one; note `OdsMultiSelect` is a checkbox
`MudMenu`, **not** a combobox — must specify all of the following as MVP:

- **Accessible name** — a visible, persistent, programmatically-associated label;
  `aria-label` / `aria-labelledby`. Optionality is conveyed **in text**, never by placeholder alone.
  (Odyssey marks *required* only — a `*` after the label; the absence of one means optional.)
- **ARIA role and state semantics** — for a combobox: `role="combobox"` with `aria-expanded` /
  `aria-controls` → `role="listbox"` / `aria-activedescendant` / `role="option"` + `aria-selected`,
  maintained as the user types and navigates.
- **Full keyboard operability** — type-to-filter, Arrow keys move the active option, Enter selects,
  Esc closes and restores focus, Tab/Shift+Tab in logical order, and a **keyboard-operable clear**
  (not a pointer-only "×"). Interactive targets ≥ 24×24 CSS px.
- **Announced async states** — loading, empty and error exposed through a **live region**
  (`aria-live="polite"`; `role="alert"` for failures). Hints programmatically associated with the field.
- **Visible focus** — a `:focus-visible` indicator on the trigger and the active option; the popover
  must not obscure the focused field.
- **Validation association** — inline errors linked by `aria-describedby`, field `aria-invalid="true"`,
  focus moved to the offending field on failure, message actionable.
- **Dialog focus management** — the new field sits in logical tab order; a popover inside a modal keeps
  focus within it (no keyboard trap); two-level Escape closes the popover, then the dialog.

If the feature reuses accessible components only, **say that explicitly** — it is the answer that
closes this item.

## 2. Never convey meaning by icon or colour alone

Type, status, "archived", severity and the like must be available **as text** — visible or `sr-only` —
not only as an icon or a muted/coloured style. The design system's `OdsChip` renders its icon
`aria-hidden`, so any meaning carried on a chip has to live in its text. (WCAG 1.1.1, 1.4.1, 1.3.3)

## 3. Contrast

All text — **including muted, disabled and archived variants** — meets **4.5:1**. Focus indicators,
component boundaries and meaningful icons meet **3:1**. Use the AA-contrast
`--mud-palette-placeholder` token for placeholders and hints, not the 0.38 disabled alpha.
(WCAG 1.4.3, 1.4.11)

## 4. Focus management across the flow

State what receives focus when a dialog opens, when it closes, when a row expands, and when validation
fails. Focus must never be lost to `<body>` after an action, and must never be trapped.

## 5. Make the requirements testable

Add acceptance criteria (§11) that name the a11y requirements and are verifiable with a screen reader
plus an automated contrast check. State them as **MVP requirements, not deferrals** — an a11y
criterion deferred past v1 is the finding this checklist exists to prevent.

---

## How to apply

- During the interview, ask whether the feature adds a **new** interactive widget or reuses existing
  components, and how loading/empty/error are announced rather than merely drawn.
- While writing §3 and §6, walk items 1–5. Put a one-line explicit decision for each that applies, and
  `> Not applicable — <reason>` for those that don't.
- Reflect each decision back as a **testable acceptance criterion** in §11.
