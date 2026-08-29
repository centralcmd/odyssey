export interface BadgeProps {
  /** error = notification (default) · primary · neutral. */
  tone?: 'error' | 'primary' | 'neutral';
  /** Render a bare dot instead of a count/label. */
  dot?: boolean;
  /** Numeric count; values above `max` render as `${max}+`. */
  count?: number;
  /** Cap for the count display. Default 99. */
  max?: number;
  children?: React.ReactNode;
}

/** A compact count or status indicator. */
export declare function Badge(props: BadgeProps): JSX.Element;
