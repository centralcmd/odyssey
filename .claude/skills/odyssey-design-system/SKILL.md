---
name: odyssey-design-system
description: >
  Use this skill for ANY frontend task in a Blazor + MudBlazor project that uses the Odyssey Design System.
  This skill MUST be triggered whenever Claude is creating, editing, or reviewing Blazor components (.razor files),
  MudBlazor theme configuration, CSS, or any UI-related code. It ensures all frontend work references and stays
  consistent with the Odyssey Design System — including MudTheme overrides, CSS variables, color palettes, and
  typography. Trigger on phrases like: "create a component", "add a page", "update the UI", "style this",
  "fix the layout", "change the color", "add a button/form/dialog", or any Blazor/MudBlazor frontend work.
  Do NOT skip this skill just because the task seems small — even single-component edits must follow the design system.
  For reconciling the implementation after the design system ITSELF changed, use odyssey-design-system-changes.
---

# Odyssey Design System — Build Against the Existing System

You are **consuming** the design system: it is fixed, your code is what changes. Every component, page
or style must draw its values from the system rather than restating them.

> If the design system has *already changed* and the implementation needs to catch up, that is the
> other direction — use **`odyssey-design-system-changes`** instead.

---

## Step 1 — Load the design system (selectively)

`Odyssey Design System/` holds ~607 files / 11 MB. **Do not read it all.** Read in this order and
stop as soon as you have what the task needs:

| Read | For |
|---|---|
| `Odyssey Design System/SKILL.md` | The rules of thumb, in 64 lines. Always start here. |
| `README.md` → **Quick reference** section | Cheat sheet, component catalogue (name → purpose → specimen), token map, page & template map |
| `colors_and_type.css` | The actual token values, when you need a specific one |
| `components/<Name>.html` or `preview/*.html` | The specimen for the exact pattern you are building |
| `ui_kits/web/<Page>.jsx` | The reference implementation of a whole page pattern |

The README's Quick reference is the index — use it to find the two or three files that matter instead
of scanning the tree. If the folder is missing or empty, **stop and tell the user**.

> Path quoting: the folder name contains a space — `"Odyssey Design System/..."`.

### Both themes, always

Dark is the primary surface, light is a first-class alternate. Every colour you write must be valid in
both. Use semantic tokens (`Color.Primary`, `var(--mud-palette-surface)`) — never a literal that only
works in one mode, and never ask the user to pick a mode.

---

## Step 2 — Check what already exists

`Odyssey.Client/Components/` already holds **135 `Ods*` atoms**. Building a duplicate is the most
common failure here.

```bash
ls Odyssey.Client/Components/Ods*.razor
grep -rn "<OdsSomething" Odyssey.Client/Pages   # how existing pages consume it
```

Cross-check the DS component catalogue (README Quick reference) against that listing: the naming
contract is design-system `Foo.jsx` ⇄ Blazor `OdsFoo.razor`.

- Suitable component exists → **reuse or extend it**
- Close but not quite → **modify it**, and say so in your summary
- Genuinely nothing → create one, following the nearest existing atom as a template

---

## Step 3 — Apply the system

**Read `docs/frontend-mudblazor-gotchas.md` (repo root) before
writing any `.razor`.** It carries the traps that compile fine and break at runtime (literal string
params, icon ligatures, `MudMenu` activators, modal CSS specificity) plus the token and registry
conventions. Those rules are not repeated here — that file is the single copy.

The short form:

- **Components:** MudBlazor primitives (`MudButton`, `MudTextField`, `MudCard`) over raw HTML wherever
  one exists. Wrap them in an `Ods*` atom when the pattern is reusable.
- **Colour:** semantic palette tokens only. No raw hex.
- **Spacing:** `var(--space-1…16)` (4px base) or MudBlazor `pa-N` / `ma-N`, which map 1:1. No raw `px`.
- **Typography:** MudBlazor `Typo` values. Don't override font-family or font-size inline.
- **Visual metadata** for an enum (icon, colour, label) goes in `OdsTypeRegistries.cs`, not at the
  call site.
- **New colour not in the palette** → ask the user first.

---

## Step 4 — Self-check before you output code

- [ ] Read the DS `SKILL.md` + the Quick reference entry for this pattern — not the whole folder
- [ ] Checked the 135 existing `Ods*` atoms — not creating a duplicate
- [ ] Read `docs/frontend-mudblazor-gotchas.md`; string params prefixed with `@`; icons are SVG
      constants or `material-icons` spans
- [ ] Colours are semantic tokens, valid in dark **and** light
- [ ] Spacing and type flow from tokens — no raw hex, no raw `px`
- [ ] Scoped `.razor.css` sits with its component
- [ ] Matches the DS specimen for this pattern

---

## Step 5 — Call out deviations

If the request needs something the system doesn't define, flag it instead of inventing it:

> ⚠️ This would deviate from the Odyssey Design System. The closest equivalent is `{{ ALTERNATIVE }}`
> (`{{ SPECIMEN_PATH }}`). Proceed with the deviation, or use the alternative?

Never edit `Odyssey Design System/` to make your implementation easier — it is generated by a separate
design pipeline and re-exported wholesale, so the edit would be lost and the drift would be real in
the meantime.
