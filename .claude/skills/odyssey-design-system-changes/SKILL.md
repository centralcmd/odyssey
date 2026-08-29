---
name: odyssey-design-system-changes
description: >
  Use this skill when the Odyssey Design System has ALREADY changed and the Blazor frontend implementation
  must be updated to match the new design. Trigger after a design-system update lands — e.g. a
  "docs: update design system" commit touching `Odyssey Design System/` — or when the user says things like
  "the design system changed, update the frontend", "sync the UI to the new design", "bring the Ods
  components in line with the design system", "the design was updated, implement it", or "match the new
  tokens/colors/component". The design system is the source of truth; this skill reconciles `Odyssey.Client`
  to it. For building a NEW page that only consumes the EXISTING tokens/components, use odyssey-design-system
  instead.
compatibility:
  tools: [Bash, Read, Edit, Write, Grep, Glob]
user-invocable: true
---

# Odyssey Design System — Sync the Frontend to a Design Change

The design system in `Odyssey Design System/` (note the space) is the **source of truth**, and it has
**already changed** — typically committed by a separate design pipeline (look for commits like
`docs: update design system`). Your job is **reconciliation**: bring the Blazor + MudBlazor implementation in
`Odyssey.Client` into alignment with the new design. You are *not* authoring the design system here; you are
catching the implementation up to it.

| Side | Location | Role |
|---|---|---|
| **Source of truth (already updated)** | `Odyssey Design System/` — `colors_and_type.css` (tokens), `components.css`, `components/*.jsx` (+ `.d.ts`), preview `*.html`, `_ds_manifest.json` | What the design now *is*. Do not edit to "fix" the implementation; treat it as the spec. |
| **Implementation (you update this)** | `Odyssey.Client/Components/Ods*.razor` (+ `.razor.css`), `wwwroot/css/app.css` (foundation tokens), `wwwroot/css/odyssey-components.css` (global), `MudTheme`, and consuming pages | What the app ships. Bring it into parity. |

> Naming contract: design-system `Foo.jsx` ⇄ Blazor `OdsFoo.razor`; each `colors_and_type.css` token ⇄ its
> `app.css` / `MudTheme` counterpart. After this skill runs, there should be **no drift** between them.

---

## Step 1 — Find out exactly what changed in the design system

Don't eyeball the whole folder — diff it. Design updates arrive as commits, so let git tell you the delta:

```bash
# Recent design-system commits (pick the update you're implementing)
git log --oneline -- "Odyssey Design System/"

# What changed in the latest design update vs. the commit before it
git show --stat <ds-commit>
git diff <ds-commit>^..<ds-commit> -- "Odyssey Design System/"
```

If you're catching up across several updates, diff from the last commit the implementation was aligned to up
to `HEAD`. When the range is ambiguous, **ask the user which design change to implement** rather than
guessing. Build a concrete list of what changed: new/edited tokens, new/edited atoms, changed component CSS,
new preview states, manifest additions.

## Step 2 — Map each design change to its Blazor target

For every item from Step 1, locate its counterpart in `Odyssey.Client`:

| Changed in the design system | Update in the implementation |
|---|---|
| A token in `colors_and_type.css` (color, `--space-N`, radius, type) | The same custom property in `wwwroot/css/app.css`, and any palette token wired into `MudTheme` `PaletteDark`/`PaletteLight` |
| `components/<Name>.jsx` (new or revised atom) | `Components/Ods<Name>.razor` (+ scoped `Ods<Name>.razor.css`) — create it if the atom is new |
| Rules in `components.css` | `wwwroot/css/odyssey-components.css` (global) or the relevant scoped `.razor.css` |
| A new/changed `preview/*.html` page or state | The consuming page/component that renders that pattern |
| `_ds_manifest.json` gained a component | A new `Ods*` wrapper is owed; confirm none exists before creating |

```bash
ls Odyssey.Client/Components/Ods*.razor
grep -rn "<token-name>" Odyssey.Client/wwwroot/css Odyssey.Client/Components   # find every consumer to update
```

## Step 3 — Update the implementation to match

- **Tokens:** change the value in `app.css` (and `MudTheme` for palette tokens) so it equals the new
  `colors_and_type.css` value. Update **both** dark (primary) and light themes. Don't hand-edit every
  consumer if they reference the token via `var(--…)` — fixing the token cascades.
- **Atoms:** edit/create `OdsName.razor` to expose the same variants/props/states the revised `.jsx`
  defines. Wrap the MudBlazor primitive; put enum icon/color/label visuals in the existing registries
  (`OdsTypeRegistries`, `*Visuals`) and shared types in `OdsModels.cs` — see
  [[project_type_metadata_registries]].
- **Moving markup into a child also moves its scoped `.razor.css`** — see [[project_god_component_split]].

### MudBlazor v9 razor gotchas (read before touching any `.razor`)

Wrapping MudBlazor has many edges that compile fine but break at runtime — string params passed as literals
unless prefixed with `@`, `MudMenu`/popover quirks, `StartIcon` needing an SVG constant not a Material
ligature, `OdsModal` head/content/foot overrides needing the `.mud-dialog ` prefix, and more. **Consult
[[feedback_mudblazor9_razor_gotchas]] before editing or creating an Ods component** rather than
rediscovering them.

## Step 4 — Hold the adherence rules

The design defines values as tokens; your implementation must reference them, never re-hardcode:

- **No raw hex** — `var(--mud-palette-primary)`, never `#1de9b6`.
- **No raw `px`** — `var(--space-4)` (4px base), never `16px`; MudBlazor `pa-N`/`ma-N` map 1:1.
- Brand colors (**Tide** teal / **Sea** cyan) are brand only — **never** encode income/expense with them; use
  mint/coral. No emoji, no gradients in product chrome, numbers tabular, negatives use `−` + expense color.
- Exception already in the tree: picker `oklch(...)` literals intentionally mirror the design system and are
  **not** tokenized — don't "fix" them. See [[project_type_metadata_registries]].

## Step 5 — Verify parity against the NEW design renders

A token or atom change ripples across pages — confirm the running app now matches the updated design, in
**both themes**:

- Use the screenshot/parity harness (Playwright + the design-system http server + the running client). Drive
  the live app on **`localhost:5199`** (not `127.0.0.1` — the auth cookie is host-scoped); log in with the
  seeded `.env` credentials. See [[project_ds_alignment_pass]] and [[reference_running_app_login]].
- Compare each changed component to its updated `Odyssey Design System/preview/*.html` (or
  `components/*.html`) render — they should match.
- ⚠️ **Do not `dotnet build`/`dotnet run` `Odyssey.Client` while the dev server is up** — it desyncs the
  `blazor.boot` hashes. Rebuild only with the dev server stopped. ([[project_ds_alignment_pass]])

## Step 6 — Final checklist

- [ ] Diffed `Odyssey Design System/` to enumerate exactly what changed (Step 1)
- [ ] Every changed token mirrored into `app.css` + `MudTheme`, for both dark and light
- [ ] Every changed/added atom reflected in its `Ods*` component (+ scoped css); new atoms created, not duplicated
- [ ] Global/scoped CSS updated to match `components.css`
- [ ] Consuming pages updated where a preview/state changed
- [ ] No raw hex / no raw `px` introduced — values flow from tokens
- [ ] MudBlazor gotchas checked ([[feedback_mudblazor9_razor_gotchas]])
- [ ] Verified visually in both themes against the new renders; dev-server rebuild caveat respected
- [ ] No drift left between the design system and the implementation
- [ ] Did **not** edit `Odyssey Design System/` to accommodate the implementation — the design stayed the source of truth

## When you're unsure

The updated design is the authority — match it, don't reinterpret it. If the new design seems to require a
foundation the implementation can't express (a token the palette lacks, a structurally novel atom), copy the
closest existing `Ods*` pattern and adapt; if it still doesn't fit, **stop and ask the user** before
diverging. Never silently edit the design-system source to make the implementation easier. 

Also remember that we are using MudBlazor as the base for our components, so if MudBlazor can not be used 
as a base, consider asking the user for advice.
