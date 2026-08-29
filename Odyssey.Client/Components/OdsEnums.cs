namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Enum vocabularies for the Odyssey design-system component library (the Ods*
//  components) — the size steps, tones, variants and directions their parameters
//  take. Each mirrors the matching design-system contract in
//  "Odyssey Design System/components/*.d.ts".
//
//  Enums only. A type that carries data belongs with the family it serves:
//  OdsTableModels.cs (record/files tables), OdsSortHelpers.cs, OdsPagerMath.cs,
//  OdsTypeRegistries.cs, or OdsModels.cs for the chart / form / upload records.
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
//  Shared option / data types for the Odyssey design-system component library
//  (the Ods* components). One file so the small records stay discoverable and
//  the components themselves stay focused on markup. Each type mirrors the
//  matching design-system contract in "Odyssey Design System/components/*.d.ts".
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Size step shared by Avatar, IconButton and Spinner (sm · md · lg).</summary>
public enum OdsSize { Sm, Md, Lg }

/// <summary>OdsInfoTile value typography — Mono (numbers/dates) · Text (names/labels) · Sm (dates).</summary>
public enum OdsInfoTileVariant { Mono, Text, Sm }

/// <summary>Button intent — filled (primary CTA) · outlined (secondary) · text (tertiary/nav) · danger (destructive).</summary>
public enum OdsButtonVariant { Filled, Outlined, Text, Danger }

/// <summary>
/// Chip semantic color. Never use brand tide/sea to encode money — use income/expense.
/// The Status* tones map to the account/budget lifecycle palette (--status-* tokens):
/// active (live), closed (settled history), archived (out of circulation — hollow dot).
/// </summary>
public enum OdsChipTone { Default, Income, Expense, Pending, Info, Tag, Warning, Error, Outline }

/// <summary>Badge tone — error (notification, default) · primary · neutral.</summary>
public enum OdsBadgeTone { Error, Primary, Neutral }

/// <summary>Money tint for a headline figure that flips sign — income (mint) / expense (coral).</summary>
public enum OdsValueTone { Income, Expense }

/// <summary>Avatar tone — neutral (default) or tide (brand-tinted).</summary>
public enum OdsAvatarTone { Neutral, Tide }

/// <summary>
/// Determinate fill tone — default (brand) · income · expense (muted coral, e.g. an
/// under-budget spend) · pending · over (emphatic solid coral, e.g. over-budget).
/// </summary>
public enum OdsProgressTone { Default, Income, Expense, Pending, Over }

/// <summary>Skeleton shape — text (a line; use Lines for a paragraph) · circle (avatars) · block (cards/tiles/charts).</summary>
public enum OdsSkeletonVariant { Text, Circle, Block }

/// <summary>Direction of a StatTile delta — Up tints income green, Down tints expense coral.</summary>
public enum OdsDeltaDirection { Up, Down }

/// <summary>
/// The change/difference encoding an <see cref="OdsDelta"/> renders.
/// Variance — a reconciliation result (0 reconciled mint ✓, non-zero amber discrepancy, null disabled).
/// Directional — a period-over-period change (↑/↓/– arrow + magnitude; muted with <c>Neutral</c>).
/// Signed — a signed amount (+/− + magnitude, mint up / coral down).
/// </summary>
public enum OdsDeltaMode { Variance, Directional, Signed }

/// <summary>Donut layout — Row (ring beside legend) or Stack (ring above legend, two-up).</summary>
public enum OdsDonutLayout { Row, Stack }

/// <summary>Column alignment — Start (default) or End (right-aligned, monospace tabular figures).</summary>
public enum OdsAlign { Start, End }

/// <summary>Sort direction for a controlled <see cref="OdsTable{TRow}"/> column.</summary>
public enum OdsSortDirection { Asc, Desc }

/// <summary>
/// Data type of a sort field (Odyssey Design System · components/SortSelect). Drives the field's
/// natural default direction and its typed direction label (§4.4/§5 of the per-page sorting spec):
/// Text → Asc (A → Z) · Number → Desc (High → Low) · Date → Desc (Newest first) ·
/// Status → Asc (Defined order, over the enum's declared member order).
/// </summary>
public enum OdsSortType { Text, Number, Date, Status }

/// <summary>Lead-tile tint for an <c>OdsModal</c> header icon. Brand-tide by
/// default; Warning / Error for destructive or confirm dialogs.</summary>
public enum OdsModalTone { Brand, Warning, Error }

/// <summary>Signal level for an <c>OdsSeverityIcon</c> — the glyph shared by Alert
/// blocks and the PageHeader problem-rollup toggle. Renders in currentColor so the
/// parent supplies the tint (amber / coral / sea).</summary>
public enum OdsSeverity { Warning, Error, Info }

/// <summary>Provenance state for an <c>OdsMatchIndicator</c> — where a matched
/// merchant/category value came from, conveyed as text. <c>Suggestion</c> is the
/// interactive sub-threshold chip; the rest are static annotations.</summary>
public enum OdsMatchState { None, Ai, Created, Manual, Suggestion }

/// <summary>
/// The direction an <see cref="OdsSettingField"/> setting may move, shown as a marker in the field's
/// outline beside the label. For a setting whose opposite direction is refused rather than merely
/// discouraged — a cap whose cost survives being lowered back, or a control that fails open when its
/// table fills, so a smaller number weakens it instead of tightening it.
/// </summary>
public enum OdsSettingBound { None, LowerOnly, RaiseOnly }

/// <summary>
/// <see cref="OdsCapacityField"/> shape — Stacked (the OdsSettingRow control column: input over a
/// "No limit" switch) · Inline (the OdsSettingField frame: one line, value then an inverse-action pill).
/// </summary>
public enum OdsCapacityFieldVariant { Stacked, Inline }
