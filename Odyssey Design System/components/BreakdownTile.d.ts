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
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A labelled summary tile listing a distribution as icon · label · count rows
 * (By type, By status, By currency…). The generic form of the Contracts
 * overview breakdown.
 */
export declare function BreakdownTile(props: BreakdownTileProps): JSX.Element;
