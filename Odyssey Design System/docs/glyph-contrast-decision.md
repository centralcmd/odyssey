# Registry glyph contrast on light — decision

Resolves the design-system side of the issue raised from PR #163 (WCAG 2.2 SC 1.4.11 on the light theme).

## Decision

Fix 3 from the issue: a lightness cap in CSS. The 124 registry constants are not re-authored and `OdsTypeOption` does not gain a second colour.

| Token | Dark | Light | Used for |
|---|---|---|---|
| `--glyph-l` | `l` | `min(l, 0.58)` | glyphs, icon tiles, rail nodes (1.4.11, 3:1) |
| `--glyph-text-l` | `l` | `min(l, 0.50)` | registry colour used as text: kind chips, value figures (1.4.3, 4.5:1) |

Render sites wrap the registry colour as `oklch(from <colour> var(--glyph-l, l) c h)` (or `--glyph-text-l`). Chroma and hue are unchanged, so hue relationships across the family hold. Soft tints (`/ 0.16`) keep the authored colour. Dark is unchanged: both tokens pass `l` through.

## Measured

Worst case over every hue 0–360 at C ≤ 0.16:

- `--glyph-l` (0.58): 3.65:1 on `#FFFFFF`, 3.28:1 on the colour's own `/ 0.16` tint.
- `--glyph-text-l` (0.50): 5.63:1 on `#FFFFFF`, 5.09:1 on the tint.
- Before: 1.77:1 worst.

0.60 would give 3.04:1 on the tint; 0.58 keeps margin. Yellows (h 85–110) land at an ochre that still reads as the family.

Specimen: `preview/glyph-contrast.html` (all 81 distinct authored colours; dark / light before / light after).

## Changed in the design system

- `colors_and_type.css`: both tokens, per theme.
- `components.css`, `ui_kits/web/properties.css`: `--rec` / `--odc-infotile-accent` glyph consumers wrap through `--glyph-l`.
- Components: `Select`, `MultiSelect`, `TypeSelect`, `Combobox`, `TagMultiSelect`, `InfoTile`, `BreakdownTile`, `AccountTypeChip`, `ContactChip`, `CustodianChip`, `FileUpload`, `FilesTable` (avatar glyph; kind chip label uses `--glyph-text-l`).
- UI kit: `AccountTerms`, `AccountEstimates`, `PropertyEstimates`, `Journal`, `AddTransactionModal`, `Properties`, `Components` (kit mirrors).
- `handoff/README.md`: `app.css` supplement for the Blazor client.

## What Odyssey.Client does

1. Add the two tokens to `app.css` (see `handoff/README.md` → *Registry glyph lightness*).
2. At every site that writes `style="color:@opt.Color"` for a registry glyph (`OdsTypeSelect`, the event rail node, record marks, file avatars, info tiles), write `oklch(from @opt.Color var(--glyph-l, l) c h)`. For registry colour used as text, `--glyph-text-l`. Leave backgrounds / soft tints alone.
3. Tests: one generic test over all fifteen registries. Rendered light colour = `(min(L, 0.58), C, H)`; assert `>= 3.0` against `Surface(dark: false)` and against the composited `/ 0.16` tint. Keep the dark assertion. This replaces `The_light_theme_shortfall_is_a_known_pre_existing_state_of_the_whole_family`, which will start failing once the cap is applied, as intended.

## Fallback

A browser without relative colour syntax drops the declaration and the glyph inherits the text colour — high contrast, no hue.

## Not covered

Chart series colours (`StepChart` lines, tooltips, legend values) are out of scope; they follow the chart token rules.
