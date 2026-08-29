# Cross-Cutting Review Checklist

Recurring security/privacy and accessibility concerns that the Odyssey security and accessibility
auditors raise on almost every spec. **Address these upfront, inside the relevant sections** (mostly
§3, §6, §7, §9, §10, §11, §16) so the spec passes review without a round-trip. Each item says what to
decide, where it lives, and the Odyssey-specific default so you state it correctly the first time.

For each concern: make an explicit decision in the spec (even "N/A — here's why"). A stated,
reasoned decision is what closes a finding; silence is what opens one.

---

## Security, Privacy & Compliance (mostly §10, with hooks in §6/§7/§9/§11)

Anchor to OWASP (Top 10, ASVS, WSTG) and, where data subjects are involved, GDPR / ISO 27001 /
Norwegian Sikkerhetsloven.

### 1. Tenancy & ownership model — state it explicitly
The Odyssey **finance domain is a shared, single-tenant workspace**: `Account`, `Counterparty`,
`Transaction`, etc. have **no per-user owner column**. Access is governed entirely by **permission
claims**, not by row ownership. State this in §10 so reviewers don't chase **cross-user IDOR**
false positives — there is no cross-user object boundary to violate in the finance domain.
- **Exception that flips it:** if the feature introduces an entity that *is* per-user owned (scoped
  to a specific user), call that out and require an ownership/authorization check on every read and
  write — that genuinely is an IDOR surface.

### 2. Read-path claim crossover / over-exposure (data minimisation)
When a read endpoint **nests or returns a DTO that is normally gated behind a *different* permission
claim**, you erode that claim boundary — an `accounts.read`-only caller could read data that should
require `counterparties.read`. This is the single most common finding.
- **Default:** project a **purpose-built minimal DTO** for the nested/returned data. Do **not** reuse
  a fuller existing DTO that carries fields (especially **free-text** or **PII**) reachable today only
  under another claim.
- In the spec, **list exactly which fields are exposed** and under which claim, and justify each.
  Drop large free-text/notes/description fields from cross-claim projections.

### 3. Write-path crossover / mass-assignment (over-posting)
Request/write DTOs must carry **only scalar values and FK ids the caller is allowed to set** — never
a **nested related-entity object** that could over-post or mutate a different entity.
- **Default:** a link to another entity is set by its **scalar id** only (e.g. `CustodianId`), never
  by accepting the nested object. When you add a relationship, **pin this as an explicit invariant**
  in §6 and add an **acceptance criterion** (§16) that proves a populated nested object in the request
  body does not create/mutate the related entity.

### 4. Error-message information disclosure (existence oracle)
Echoing identifiers in 4xx messages can be an existence oracle.
- **Default for Odyssey:** echoing an **opaque GUID** is acceptable — GUIDs aren't enumerable and the
  finance domain is single-tenant, so it's not a meaningful oracle. Say so in §10/§11 to pre-empt the
  finding.
- **Be cautious** with sequential/enumerable ids, emails, or anything that confirms the existence of
  another user's / tenant's data — prefer a generic message there.

### 5. Permission claims — reuse vs new
- **Reusing** an existing claim: verify the data the endpoint exposes/mutates genuinely belongs inside
  that claim's boundary (ties back to #2/#3). State which claim gates each new/changed endpoint.
- **New** claim: note the **role-claim migration** and that **users must log out/in** (claims are baked
  into the auth cookie at login) — not just refresh.

### 6. PII & data minimisation
Identify which new or newly-exposed fields are **personal data** and expose the minimum needed.
- Norwegian **organisasjonsnummer**: a public business-register identifier (not secret, not personal
  data for a company) — generally fine to expose. **But** if a record can represent a **natural person
  / sole proprietor**, that number edges toward personal data; prefer least-data.

### 7. Audit trail
State whether the change needs an audit surface, or explicitly why not (e.g. "account changes are not
currently versioned; no new audit surface for v1").

---

## Accessibility (§3 — User Experience), WCAG 2.2 Level AA

Also anchor to EN 301 549 / Section 508 / Norway's *Forskrift om universell utforming av IKT*. The
finance UI is Blazor + MudBlazor on the Odyssey design system (Ods* components).

### 8. Reuse an accessible component, or fully spec a new widget
Prefer reusing an existing accessible **Ods/DS component**. If the feature introduces a **new
interactive widget** (e.g. a custom **searchable combobox/picker** rather than reusing one — note
`OdsMultiSelect` is a checkbox `MudMenu`, *not* a combobox), the spec must require, as MVP:
- **Accessible name** — a visible, persistent, programmatically-associated label; `aria-label`/
  `aria-labelledby`; optionality conveyed **in text**, not by placeholder alone.
- **ARIA role/state semantics** — for a combobox: `role="combobox"` + `aria-expanded` /
  `aria-controls` → `role="listbox"` / `aria-activedescendant` / `role="option"` + `aria-selected`,
  maintained as the user types and navigates.
- **Full keyboard operability** — type-to-filter, Arrow keys move active option, Enter selects, Esc
  closes and restores focus, Tab/Shift+Tab logical order, and a **keyboard-operable clear** (not a
  pointer-only "×"); interactive targets ≥ 24×24 CSS px.
- **Announced async states** — loading / empty / error exposed via a **live region**
  (`aria-live="polite"`; `role="alert"` for failures); hints programmatically associated with the field.
- **Visible focus** — `:focus-visible` indicator on trigger and active option; popover must not obscure
  the focused field.
- **Validation association** — inline errors linked via `aria-describedby`, field `aria-invalid="true"`,
  focus moved to the offending field on failure; message is actionable.
- **Dialog focus management** — new field in logical tab order; popover keeps focus within the modal
  (no keyboard trap); two-level Escape (close popover, then dialog).

### 9. Never convey meaning by icon or colour alone
Type, status, "archived", severity, etc. must be available **as text** (visible or `sr-only`) — not
only an icon or a muted/coloured style. Note the design system's `OdsChip` renders its icon
`aria-hidden`, so any meaning on a chip must live in text. (WCAG 1.1.1, 1.4.1, 1.3.3)

### 10. Contrast
All text — **including muted / disabled / archived variants** — meets **4.5:1**; focus indicators,
component boundaries, and meaningful icons meet **3:1**. Use the AA-contrast
`--mud-palette-placeholder` token for placeholders/hints, not the 0.38 disabled alpha. (WCAG 1.4.3,
1.4.11)

### 11. Make a11y requirements testable
When the feature has new UI, add **acceptance criteria** (§16) that name the a11y requirements and are
verifiable with a screen reader + an automated contrast check — state them as MVP requirements, not
deferrals.

---

## How to apply

- During interview / gap analysis, ask the cross-cutting questions (tenancy/ownership, what each new
  read exposes & under which claim, whether any write adds a nested relationship, new UI widgets).
- While writing §10, walk items 1–7; while writing §3, walk items 8–11. Put a one-line **explicit
  decision** for each that applies, and `> Not applicable — <reason>` for those that don't.
- Reflect the security/a11y decisions back as **testable acceptance criteria** in §16.
