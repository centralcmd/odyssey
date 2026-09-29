**Design system decision: fix 3, a lightness cap applied in CSS.**

The registry constants stay as authored, and `OdsTypeOption` does not get a second colour. On light only, glyphs render through a lightness cap. Chroma and hue don't change, so the family keeps its hue relationships. Dark is unchanged.

| Token | Dark | Light | Use |
|---|---|---|---|
| `--glyph-l` | `l` | `min(l, 0.58)` | glyphs, icon tiles, rail nodes (1.4.11) |
| `--glyph-text-l` | `l` | `min(l, 0.50)` | registry colour used as **text**: kind chips, value figures (1.4.3) |

**Measured, worst case over every hue at C ≤ 0.16:**
- `--glyph-l`: 3.65:1 on `#FFFFFF`, 3.28:1 on the colour's own `/ 0.16` tint (was 1.77:1)
- `--glyph-text-l`: 5.63:1 on `#FFFFFF`, 5.09:1 on the tint

The design system is updated: tokens, shared components, UI kit, and the `handoff/README.md` → *Registry glyph lightness* section.

### To do in Odyssey.Client

1. **`app.css`:** add the tokens.
   ```css
   :root, [data-theme='dark'] { --glyph-l: l; --glyph-text-l: l; }
   html:not([data-theme='dark']) { --glyph-l: min(l, 0.58); --glyph-text-l: min(l, 0.50); }
   ```
2. **Glyph render sites** (`OdsTypeSelect` trigger + options, event rail node, record marks, file avatars, info tiles, type/contact chips): replace `color:@opt.Color` with
   `color:oklch(from @opt.Color var(--glyph-l, l) c h)`.
   Where the colour arrives as `--rec` in scoped CSS, wrap it there: `color: oklch(from var(--rec) var(--glyph-l, l) c h)`.
3. **Registry colour as text** (file-kind chip labels, estimate/term value figures): same wrap with `--glyph-text-l`.
4. **Leave soft tints alone.** The `/ 0.16` backgrounds keep the authored colour.
5. **Tests:** write one generic test across all fifteen registries instead of one test per registry. On light, the rendered colour is `(min(L, 0.58), C, H)`. Assert `>= 3.0` against `Surface(dark: false)` and against the composited `/ 0.16` tint, and keep the dark assertion. Delete `The_light_theme_shortfall_is_a_known_pre_existing_state_of_the_whole_family`, which will start failing once the cap lands, as designed.

**Fallback:** a browser without relative colour syntax drops the declaration, so the glyph inherits the text colour. Contrast stays high, but the hue is lost.

**Out of scope:** chart series colours (lines, tooltips, legend values). They follow the chart token rules.

Reference: `docs/glyph-contrast-decision.md` and specimen `preview/glyph-contrast.html` in the design system.
