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
| `_ds_manifest.json` gained a component | A new `Ods*` wrapper is owed — confirm none of the 136 existing atoms already covers it |

```bash
ls Odyssey.Client/Components/Ods*.razor
grep -rn "<token-name>" Odyssey.Client/wwwroot/css Odyssey.Client/Components   # every consumer to update
```

## Step 3 — Update the implementation, in this order

**The order is load-bearing — do not interleave it.** A page rolled out against a half-updated atom
gets patched twice, and the second patch is the one that gets forgotten. Finish each stage across the
whole change before starting the next.

### 3.1 — Foundation: tokens first

Components consume tokens, so a token left stale makes every atom built on it wrong.

- Change the value in `app.css` (and `OdysseyTheme.cs` for palette tokens) so it equals the new
  `colors_and_type.css` value. Update **both** dark (primary) and light.
- Consumers referencing `var(--…)` need no edit — fixing the token cascades.

### 3.2 — The component library: new atoms, then revised atoms

Bring `Odyssey.Client/Components/Ods*.razor` to parity **before touching a single page**.

- **New atoms in the design system get an `Ods*` wrapper now**, even if nothing consumes them yet —
  the library is the unit of parity, not the pages that happen to use it. Confirm none of the existing
  atoms already covers it (`ls Odyssey.Client/Components/Ods*.razor`) before creating a duplicate.
- **Revised atoms** get edited to expose the same variants, props and states the updated `.jsx`
  defines. Route enum icon/colour/label visuals through `OdsTypeRegistries.cs`; shared types go in
  `OdsModels.cs`.
- Global/scoped CSS matching `components.css` belongs to this stage too —
  `wwwroot/css/odyssey-components.css` or the relevant scoped `.razor.css`.
- **Moving markup into a child moves its scoped `.razor.css` with it.**

### 3.3 — Roll the components out

Only once the library is correct, replace the consuming markup with it.

- Every place that hand-rolls what a new or revised atom now expresses switches to the `Ods*`
  component. `grep -rn "Ods<Name>" Odyssey.Client` and the inverse — search for the raw markup the
  atom replaces — so a rollout does not stop at the pages you happened to remember.
- A revised atom's changed props ripple to every existing call site; enumerate them, don't sample.

### 3.4 — Everything else

Pages, new pages, layout and preview-state changes, copy, icons — whatever the diff still lists once
3.1–3.3 are done. By this point the pieces these pages are built from are already correct.

## Step 4 — Verify parity against the NEW design renders

A token or atom change ripples across pages. Confirm the running app matches the updated design in
**both themes**:

- Compare each changed component to its updated `Odyssey Design System/preview/*.html` or
  `components/*.html` render — they should match.
- Drive the live app with the Playwright harness on `localhost:5199` (see the `run-odyssey` skill for
  bringing a stack up, and `docs/frontend-mudblazor-gotchas.md` for the host-scoped-cookie and
  dev-server rebuild caveats).

## Step 5 — Second pass: re-diff and prove nothing was dropped

**This is a separate pass, run after you believe you are done — not a feeling that you finished.**
The Step 1 list is long, the work is spread across four stages, and the items lost are the ones that
looked small when you read them.

Go back to the diff and walk it again, top to bottom:

```bash
git diff <ds-commit>^..<ds-commit> -- "Odyssey Design System/" --stat   # the full inventory again
git status && git diff --stat                                          # what you actually changed
```

For **every** entry in the design diff, name the implementation file that answers it — or state
explicitly why it needs none (e.g. a preview page that only re-renders unchanged atoms). An item you
cannot account for either way is unfinished work, not an edge case.

Check the inverse too: a file you changed that no design-system change asked for is either an
undeclared fix (say so) or a mistake.

## Step 6 — Update the documentation

A sync that leaves the docs describing the old design has moved the drift rather than removed it.
Update whatever the change actually invalidated:

- `docs/frontend-mudblazor-gotchas.md` — if the change adds or retires a token convention, a registry
  entry, or a MudBlazor trap you hit while implementing it.
- Any component inventory, `README.md` or `docs/` page that lists the `Ods*` atoms or the token set,
  when the change adds or renames one.
- `CLAUDE.md`, if the change alters a rule stated there.
- This skill, if the sync surfaced a step that was missing from it.

Don't invent new documentation to have something to write; if nothing is invalidated, say so.

## Step 7 — Final checklist

- [ ] Diffed `Odyssey Design System/` to enumerate exactly what changed (Step 1)
- [ ] Worked in order: tokens → component library → rollout → pages/rest (Step 3), not interleaved
- [ ] Every changed token mirrored into `app.css` + `OdysseyTheme.cs`, dark **and** light
- [ ] Every **new** atom added to the `Ods*` library, even if no page consumes it yet
- [ ] Every **revised** atom reflected in its `Ods*` component (+ scoped CSS), not duplicated
- [ ] New/revised components rolled out to **every** call site, found by grep rather than by memory
- [ ] Global/scoped CSS updated to match `components.css`
- [ ] Consuming pages updated where a preview/state changed
- [ ] No raw hex, no raw `px` introduced — values flow from tokens
- [ ] `docs/frontend-mudblazor-gotchas.md` checked before editing `.razor`
- [ ] Verified visually in both themes against the new renders (Step 4)
- [ ] Second pass done: every design-diff entry accounted for, and every file you changed explained (Step 5)
- [ ] Documentation updated where the change invalidated it (Step 6)
- [ ] No drift left between the design system and the implementation
- [ ] Did **not** edit `Odyssey Design System/` to accommodate the implementation

## When you're unsure

The updated design is the authority — match it, don't reinterpret it. If the new design needs a
foundation the implementation can't express (a token the palette lacks, a structurally novel atom),
copy the closest existing `Ods*` pattern and adapt it; if it still doesn't fit, **stop and ask the
user** before diverging.

MudBlazor is the base for every component. If a design element genuinely cannot be built on a
MudBlazor primitive, ask the user rather than hand-rolling one.
