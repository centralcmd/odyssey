export interface BreakdownRow {
  /** Stable key for the row (falls back to the array index). */
  key?: string | number;
  /** Material Icons ligature name for the leading glyph. */
  icon?: string;
  /** Icon color (any CSS color) — usually the category's accent. */
  iconColor?: string;
  /** Row label. */
  label: React.ReactNode;
  /** Right-aligned count, shown in tabular monospace. */
  count: React.ReactNode;
}

export interface BreakdownTileProps {
  /** Overline caption above the rows (e.g. "By type", "By status"). */
  label?: React.ReactNode;
  /** The distribution rows. */
  rows?: BreakdownRow[];
  /** Message shown when `rows` is empty. */
  empty?: string;
  /**
   * The ruled total row, ON by default.
   * - `true` (default): sum the rows' counts, when every count is numeric.
   *   Rows whose counts are nodes (money, a pair) render no total rather than
   *   a wrong one.
   * - a number or node: show exactly that (a net, a caller-computed figure).
   * - `false`: no total — for buckets that overlap or do not sum meaningfully.
   */
  total?: boolean | React.ReactNode;
  /** Label on the total row. Default "Total". */
  totalLabel?: React.ReactNode;
  /** Leading glyph on the total row; pass "" for none. Default "functions". */
  totalIcon?: string;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A labelled summary tile listing a distribution as icon · label · count rows
 * (By type, By status, By currency…), closing with a ruled total row. The
 * generic form of the Contracts overview breakdown.
 */
export declare function BreakdownTile(props: BreakdownTileProps): JSX.Element;
