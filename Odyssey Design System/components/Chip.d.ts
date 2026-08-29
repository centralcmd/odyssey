export interface ChipProps {
  /** Semantic color. Never use brand tide/sea to encode money — use income/expense.
   *  `outline` is a bordered neutral; `warning`/`error` reuse pending/expense accents. */
  tone?: 'default' | 'income' | 'expense' | 'pending' | 'info' | 'tag' | 'warning' | 'error' | 'outline';
  /** Leading Material Icons ligature name. */
  icon?: string;
  /** Show a leading status dot (uses the tone color). */
  dot?: boolean;
  className?: string;
  children?: React.ReactNode;
}

/** A compact status or category pill. */
export declare function Chip(props: ChipProps): JSX.Element;
