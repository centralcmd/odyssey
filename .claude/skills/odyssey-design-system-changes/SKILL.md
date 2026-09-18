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
**already changed** — exported by a separate design pipeline as a `docs: update design system` commit.
Your job is **reconciliation**: bring `Odyssey.Client` into alignment with the new design. You are not
authoring the design system; you are catching the implementation up to it.

| Side | Location | Role |
|---|---|---|
| **Source of truth (already updated)** | `Odyssey Design System/` — `SKILL.md`, `README.md`, `colors_and_type.css` (tokens), `components.css`, `components/*.jsx` + `*.html` specimens, `preview/*.html`, `ui_kits/web/*.jsx`, `_ds_manifest.json` | What the design now *is*. Never edit it to accommodate the implementation — the pipeline re-exports the whole folder and your edit disappears. |
| **Implementation (you update this)** | `Odyssey.Client/Components/Ods*.razor` (+ scoped `.razor.css`), `wwwroot/css/app.css` (tokens), `wwwroot/css/odyssey-components.css` (global), `Theme/OdysseyTheme.cs` (`MudTheme`), consuming pages | What the app ships. Bring it to parity. |

> Naming contract: design-system `Foo.jsx` ⇄ Blazor `OdsFoo.razor`; each `colors_and_type.css` token ⇄
> its `app.css` / `MudTheme` counterpart. After this skill runs there should be **no drift**.

**Read `docs/frontend-mudblazor-gotchas.md` (repo root) before editing any `.razor`.** It holds the
MudBlazor v9 traps that compile and then fail at runtime, the token and registry conventions, the
`oklch` exception, and the dev-server rebuild caveat. Those rules live in that one file — this skill
does not restate them.

---

## Step 1 — Find out exactly what changed

Don't eyeball the folder — diff it. Design updates arrive as commits, so let git tell you the delta:

```bash
# Recent design-system commits (pick the update you're implementing)
git log --oneline -- "Odyssey Design System/"

# What changed in the latest design update vs. the commit before it
git show --stat <ds-commit>
git diff <ds-commit>^..<ds-commit> -- "Odyssey Design System/"
```

Catching up across several updates → diff from the last commit the implementation was aligned to up to
`HEAD`. When the range is ambiguous, **ask the user which design change to implement** rather than
guessing. Produce a concrete list: new/edited tokens, new/edited atoms, changed component CSS, new
preview states, manifest additions.

`Odyssey Design System/github.md` records the pipeline's own last-sync notes and screen map — useful
for understanding what the designer intended by the change.

## Step 2 — Map each change to its Blazor target

| Changed in the design system | Update in the implementation |
|---|---|
| A token in `colors_and_type.css` (colour, `--space-N`, radius, type) | The same custom property in `wwwroot/css/app.css`, plus any palette token wired into `Theme/OdysseyTheme.cs` (`PaletteDark` / `PaletteLight`) |
| `components/<Name>.jsx` (new or revised atom) | `Components/Ods<Name>.razor` (+ scoped `Ods<Name>.razor.css`) — create it if the atom is new |
| Rules in `components.css` | `wwwroot/css/odyssey-components.css` (global) or the relevant scoped `.razor.css` |
| A new/changed `preview/*.html` page or state | The consuming page/component that renders that pattern |
| `_ds_manifest.json` gained a component | A new `Ods*` wrapper is owed — confirm none of the 135 existing atoms already covers it |

```bash
ls Odyssey.Client/Components/Ods*.razor
grep -rn "<token-name>" Odyssey.Client/wwwroot/css Odyssey.Client/Components   # every consumer to update
```

## Step 3 — Update the implementation

- **Tokens:** change the value in `app.css` (and `OdysseyTheme.cs` for palette tokens) so it equals the
  new `colors_and_type.css` value. Update **both** dark (primary) and light. Consumers referencing
  `var(--…)` need no edit — fixing the token cascades.
- **Atoms:** edit or create `OdsName.razor` to expose the same variants, props and states the revised
  `.jsx` defines. Route enum icon/colour/label visuals through `OdsTypeRegistries.cs`; shared types go
  in `OdsModels.cs`.
- **Moving markup into a child moves its scoped `.razor.css` with it.**

## Step 4 — Verify parity against the NEW design renders

A token or atom change ripples across pages. Confirm the running app matches the updated design in
**both themes**:

- Compare each changed component to its updated `Odyssey Design System/preview/*.html` or
  `components/*.html` render — they should match.
- Drive the live app with the Playwright harness on `localhost:5199` (see the `run-odyssey` skill for
  bringing a stack up, and `docs/frontend-mudblazor-gotchas.md` for the host-scoped-cookie and
  dev-server rebuild caveats).

## Step 5 — Final checklist

- [ ] Diffed `Odyssey Design System/` to enumerate exactly what changed (Step 1)
- [ ] Every changed token mirrored into `app.css` + `OdysseyTheme.cs`, dark **and** light
- [ ] Every changed/added atom reflected in its `Ods*` component (+ scoped CSS); new atoms created, not duplicated
- [ ] Global/scoped CSS updated to match `components.css`
- [ ] Consuming pages updated where a preview/state changed
- [ ] No raw hex, no raw `px` introduced — values flow from tokens
- [ ] `docs/frontend-mudblazor-gotchas.md` checked before editing `.razor`
- [ ] Verified visually in both themes against the new renders
- [ ] No drift left between the design system and the implementation
- [ ] Did **not** edit `Odyssey Design System/` to accommodate the implementation

## When you're unsure

The updated design is the authority — match it, don't reinterpret it. If the new design needs a
foundation the implementation can't express (a token the palette lacks, a structurally novel atom),
copy the closest existing `Ods*` pattern and adapt it; if it still doesn't fit, **stop and ask the
user** before diverging.

MudBlazor is the base for every component. If a design element genuinely cannot be built on a
MudBlazor primitive, ask the user rather than hand-rolling one.
