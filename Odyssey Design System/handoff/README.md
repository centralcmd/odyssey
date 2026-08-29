# MudBlazor theme handoff

The theme in this design system is **live in the Blazor client**. This page records where it landed, what it deliberately doesn't cover, and the known gaps.

## What's in the client today

| This design system | Blazor client |
|---|---|
| `handoff/OdysseyTheme.cs` | `Odyssey.Client/Theme/OdysseyTheme.cs` — `MudTheme` with `PaletteDark` + `PaletteLight`, typography, layout, and the 26-step dark-tuned elevation stack |
| — | `Odyssey.Client/Layout/OdysseyThemeProvider.razor` — wraps `MudThemeProvider` with `Theme="OdysseyTheme.Theme"`, bound to `IUserPreferenceService` for dark/light |
| `assets/odyssey-logomark.svg` | `wwwroot/odyssey-logomark.svg` — the primary `rel="icon"` |
| `assets/odyssey-favicon-16.png` | `wwwroot/odyssey-favicon-16.png` |
| `assets/odyssey-favicon-32.png` | `wwwroot/favicon.png` |
| `assets/odyssey-favicon-192.png` | `wwwroot/icon-192.png` (also `apple-touch-icon`) |
| `assets/odyssey-favicon-512.png` | `wwwroot/icon-512.png` (also `apple-touch-icon`) |
| `components.css` | `wwwroot/css/odyssey-components.css` |
| supplementary rules below | `wwwroot/css/app.css` |

The client copy of `OdysseyTheme.cs` is the one that ships. `handoff/OdysseyTheme.cs` is the design-system reference copy — **when tokens change in `colors_and_type.css`, update both.**

## What the theme does NOT cover

- **Scoped CSS.** `.razor.css` files (`AccountsCard.razor.css`, `Users.razor.css`, and ~40 others) hold per-component styles. The theme can't override hardcoded values there — those need per-component edits.
- **Custom MudBlazor utility extensions** — anything that hardcoded color in `app.css`.
- **The `--finance-income` / `--finance-expense` / `--chart-*` tokens.** These aren't part of `MudTheme`; they're exposed in `app.css` as plain CSS custom properties so non-MudBlazor consumers (custom components, SVG charts) can read them.

## `app.css` supplement

`MudTheme` can't express every visual concern. These rules live in `Odyssey.Client/wwwroot/css/app.css`:

### Tabular numbers — global

`MudTheme.Typography` has no `FontVariantNumeric` setting, so amounts in `<MudTableCell>` and `<MudText>` won't line up unless it's forced globally. The `.ods-mono` / `.ods-money` classes apply it, but relying on developers to remember them is fragile — so it's forced everywhere ledger data lives:

```css
.mud-table-cell,
[class*="mud-typography"] {
  font-variant-numeric: tabular-nums;
  font-feature-settings: 'tnum';
}
```

### Light-mode shadows

`MudTheme.Shadows` is a single static `Shadow` instance — MudBlazor v8 has no separate light-mode shadow stack. The dark-tuned shadows in `OdysseyTheme.cs` include a `0 0 0 1px rgba(255,255,255,0.04) inset` highlight that reads as a faint bright stroke on light surfaces, so it's neutralised in light mode:

```css
html:not([data-theme='dark']) .mud-elevation-1 { box-shadow: 0 1px 2px rgba(14,21,37,0.08) !important; }
html:not([data-theme='dark']) .mud-elevation-2 { box-shadow: 0 2px 6px rgba(14,21,37,0.10) !important; }
html:not([data-theme='dark']) .mud-elevation-4 { box-shadow: 0 6px 18px rgba(14,21,37,0.10) !important; }
html:not([data-theme='dark']) .mud-elevation-8 { box-shadow: 0 14px 36px rgba(14,21,37,0.14) !important; }
html:not([data-theme='dark']) .mud-elevation-16 { box-shadow: 0 24px 60px rgba(14,21,37,0.18) !important; }
```

The selector is `html:not([data-theme='dark'])` rather than `[data-theme='light']` so it also covers the pre-hydration default. The matching `--mud-elevation-*` custom properties are already overridden in `colors_and_type.css`; these `app.css` rules are needed only because MudBlazor injects its own `.mud-elevation-*` selectors at higher specificity.

## `SettingField` → a MudBlazor wrapper

The System settings page is built on `SettingField`: the label notched into the field's outline, the control inside, and one always-visible helper line carrying the description plus the "last changed" stamp. That shape **is** MudBlazor's `Variant.Outlined`, so the Blazor side is a thin wrapper over the Mud controls rather than new CSS:

```razor
<MudTextField T="string" @bind-Value="Value"
              Label="@Label" Variant="Variant.Outlined"
              HelperText="@Helper" HelperTextOnFocus="false"
              Error="@HasError" ErrorText="@ErrorText" />
```

`OdsSettingField.razor` should compose that as a fixed set of parameters plus the pieces Mud has no slot for:

- **The helper line is composed, not passed through.** `HelperText` is a single string, so build it as `desc + extra + range + stamp` — the same order the mock uses (`helpFor` / `metaFor` in `SystemSettings.jsx`) — and keep the stamp visually quieter with a `.ss-stamp` span if you switch to `HelperTextContent`.
- **`ErrorText` does not displace the helper.** Mud swaps helper for error by default; the mock renders both (error above, description below) so the reader keeps the definition while fixing the value. Render the error yourself if `MudTextField`'s behaviour can't be configured to keep both.
- **Numeric caps** are `MudNumericField` with `Adornment="Adornment.End"` + `AdornmentText="MB"` / `"days"` / `"%"` for the unit, and `Min`/`Max` from the row's ceiling. A percent row stores a `0.0–1.0` fraction but is entered as whole percent — multiply on display, divide on emit.
- **Capacity caps** (a number **or** "No limit") have no Mud equivalent: keep the mock's inline shape — the value, then a pill carrying the **inverse** action ("No limit" when a number is set, "Set a limit" when unlimited), so the pill never repeats the words already showing as the value. `MudChip` with `OnClick` is the closest primitive; the `.odc-capacity-pill` rule in `components.css` is the reference styling.
- **Switches and actions** get no notch — there is no text value to label. Use the `.odc-sfield-tile` shape: label and helper left, `MudSwitch` or `MudButton` right, spanning both grid columns.
- **The grid** is `.odc-sfield-grid` (two columns, one below 900px). Free-text settings span both columns; so do the tiles.

- **Advisories** are `HelperTextContent` territory, not `ErrorText`: an amber `role="status"` band under the helper line, opening with the literal word "Advisory". Mud's `Error`/`ErrorText` is the wrong channel — it marks the field invalid and gates the form, and an advisory does neither. Compose it as its own element below the `MudTextField`/`MudNumericField` and append its `id` to the control's `aria-describedby`; `.odc-sfield-advisory` in `components.css` is the reference styling. Take its colour split literally: the tint is `--finance-pending-soft` and the icon and border are `--pending-text` (both theme-aware), while the word "Advisory" is `--mud-palette-text-primary` — amber text on an amber tint fails 4.5:1 in both themes, so MudBlazor's `Color.Warning` on the text is the wrong choice here even though it is the obvious one.
- **Single-direction settings** need the bound on the side that cannot move: set `Min` to the shipped default on a raise-only setting and `Max` on a lower-only one, and render the `lower only` / `raise only` marker beside the label (`.odc-sfield-bound`). The validation message should say which direction is refused rather than quote a range whose two ends are the same number — "Can only be lowered — 1,000 is the highest this may be set to" reads as a rule; "must be between 1 and 1,000" reads as a contradiction to someone who just typed 1,001.

The theme already carries the outlined-input border and focus colors, so a wrapper needs no new palette work. The one deliberate deviation from Mud's default: **focus thickens the outline itself** (2px primary, with padding given back so nothing shifts) instead of adding a second ring around an already-bordered control.

## Known gaps

- **No Tags management screen.** The drawer surfaces `Tags` (`local_offer`), and tag chips appear on every transaction row + budget bar, but there is no `Tags.jsx` mock or `Tags.razor` page. Treat as a deferred screen — pick this up before launching tag editing.
- **`h6` and `body1` are visually equivalent in size.** Both resolve to `1rem`; only `font-weight` separates them (500 vs 400). This matches MudBlazor's own scale, but don't reach for `h6` to create heading hierarchy at the same size — use `h5`/`h4` or rely on the weight + `--mud-palette-text-primary`/`--mud-palette-text-secondary` distinction.
- **Contacts icon is `store`.** Matches `NavMenu.razor` today. `business` (or a generic `groups`) would cover the wider contact range (individuals, banks, orgs) better — revisit on the next icon pass.
- **No AppBar.** The product chrome is drawer-only: the brand lockup sits at the top of the drawer (compass + tide-glow caps wordmark, matching the login card), and account actions sit in the footer below Preferences (User Account → Logout → About). Logout is a regular `nav-link`, not a separate styled button. If a search or notifications surface is ever spec'd, add it as a dedicated drawer section — don't reintroduce a top bar.
- **Dashboard greeting date.** The mock hardcodes `Saturday, 23 Nov`. Production should pull from the user's last account-sync timestamp, not `DateTime.UtcNow`.
