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
---

# Odyssey Design System Skill

## Purpose

Ensure all Blazor + MudBlazor frontend work is consistent with the **Odyssey Design System**. Every component,
page, or style change must reference the design system — never hardcode values that the design system already defines.

---

## Step 1: Load the Design System

Before writing or modifying any frontend code, read the design system files:

```
{{ PROJECT_ROOT }}/Odyssey Design System/
```

Read ALL files in this folder. They contain:

| File type | What it defines |
|---|---|
| MudTheme overrides | MudBlazor palette, typography, shape, shadows |
| CSS variables | Spacing, colors, borders, breakpoints |
| Color palette | Named colors and their intended usage |
| Typography settings | Font families, sizes, weights, line heights |

### Light & Dark Mode

The design system defines both a **light** and **dark** theme. When writing frontend code:

- All color references must be valid in **both** themes — use semantic palette tokens (`Color.Primary`, `var(--mud-palette-surface)`, etc.), never hardcode a color that only works in one mode
- If a component behaves differently per theme, use MudBlazor's built-in theme-aware props rather than manual CSS overrides
- Test your mental model against both theme definitions before outputting code

If the folder is missing or empty, **stop and inform the user** — do not proceed with frontend work.

---

## Step 2: Check for Existing Components

Before creating anything new, scan the project for existing Blazor components that may already serve the purpose:

```bash
find {{ PROJECT_ROOT }} -name "*.razor" | sort
```

- If a suitable component exists, **reuse or extend it** — do not create a duplicate
- If a similar component exists but needs modification, update it and inform the user
- Only create a new component if nothing suitable exists

---

## Step 3: Understand the Task

Identify what is being created or modified:

- New Blazor component (`.razor`)
- New page
- Updating existing component or page
- Changing styles or layout
- Adding MudBlazor components

---

## Step 4: Apply Design System Rules

### MudBlazor Components
- Use only MudBlazor components (`MudButton`, `MudTextField`, `MudCard`, etc.) — no raw HTML equivalents when a MudBlazor component exists
- Apply `Color`, `Variant`, and `Size` props using values consistent with the MudTheme — never hardcode hex colors
- Use theme palette references: `Color.Primary`, `Color.Secondary`, `Color.Error`, etc.

### CSS / Styling
- Use CSS variables defined in the design system — never hardcode pixel values, colors, or font sizes that the design system defines
- Correct: `var(--mud-palette-primary)`, `var(--spacing-md)`
- Wrong: `#3D5AFE`, `16px`, `font-size: 1rem` (if defined in the design system)

### Typography
- Use MudBlazor `Typo` enum values (`Typo.h1`, `Typo.body1`, etc.) that map to the design system typography settings
- Never override font-family or font-size inline unless explicitly instructed

### Colors
- Reference the named palette from the design system
- Never introduce new colors not defined in the palette without asking the user first

### Spacing & Layout
- Use MudBlazor spacing props (`Margin`, `Padding`, `Gap`) with values consistent with the design system spacing scale
- Use CSS variables for spacing where inline styles are necessary

---

## Step 5: Write the Code

Follow this checklist before outputting any code:

- [ ] Existing components scanned — no duplicate being created
- [ ] All colors use semantic tokens valid in both light and dark mode
- [ ] All typography uses MudBlazor `Typo` enum or design system CSS variables
- [ ] All spacing uses MudBlazor props or design system CSS variables
- [ ] No raw hardcoded hex, pixel, or font values that duplicate design system definitions
- [ ] Only MudBlazor components used where applicable
- [ ] Component follows existing naming and structure conventions seen in the project

---

## Step 6: Call Out Deviations

If the user's request would require deviating from the design system (e.g., a custom color, non-standard spacing), explicitly flag it:

> ⚠️ This would deviate from the Odyssey Design System. The closest design-system equivalent is `{{ ALTERNATIVE }}`. Proceed with deviation or use the alternative?

---

## Notes

- The design system folder name contains a space: `Odyssey Design System` — handle path quoting accordingly
- Both light and dark themes are always present — write code compatible with both; do not ask the user to pick one
- When in doubt about a design token, prefer reading the source file over guessing
