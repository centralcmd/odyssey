export interface SpinnerProps {
  /** sm = 16px · md = 24px (default) · lg = 40px. */
  size?: 'sm' | 'md' | 'lg';
  /** Accessible name for the live status. Default "Loading". */
  ariaLabel?: string;
  className?: string;
}

/** Circular indeterminate indicator — the one sanctioned continuous motion. */
export declare function Spinner(props: SpinnerProps): JSX.Element;

export interface ProgressBarProps {
  /** Fill percentage, 0–100 (clamped). */
  value?: number;
  /** Fill color. default = brand · income · expense · pending. */
  tone?: 'income' | 'expense' | 'pending';
  /** Taller 10px bar. */
  tall?: boolean;
  ariaLabel?: string;
  className?: string;
}

/** Determinate fill bar — budget planned-vs-actual, upload progress. */
export declare function ProgressBar(props: ProgressBarProps): JSX.Element;
