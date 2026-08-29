export interface StatTileProps {
  /** Small uppercase label above the figure. */
  overline?: React.ReactNode;
  /** The headline figure (rendered tabular monospace). */
  value: React.ReactNode;
  /** Optional change line below the figure. */
  delta?: React.ReactNode;
  /** Tints the delta: up = income green, down = expense coral. */
  deltaDir?: 'up' | 'down';
  /** Tints the figure itself — income / expense — for a balance that flips sign. */
  valueClass?: '' | 'income' | 'expense';
  className?: string;
}

/** A headline statistic tile (overline · value · delta). */
export declare function StatTile(props: StatTileProps): JSX.Element;
