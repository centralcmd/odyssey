export interface ButtonProps {
  /** filled = primary CTA · outlined = secondary · text = tertiary/nav · danger = destructive. */
  variant?: 'filled' | 'outlined' | 'text' | 'danger';
  /** Leading Material Icons ligature name. */
  icon?: string;
  /** Trailing Material Icons ligature name. */
  iconRight?: string;
  /** Stretch to the full width of the container. */
  full?: boolean;
  /** Busy state — label hides, a spinner overlays, the button is non-interactive. */
  loading?: boolean;
  disabled?: boolean;
  /** Count pill on the button — a pending quantity this action will commit (unsaved changes, queued uploads). */
  badge?: number | string;
  /** Names what `badge` counts, for the hidden accessible suffix (e.g. "unsaved changes"). */
  badgeLabel?: string;
  /** Accessible name for an icon-only button (when there are no children). */
  ariaLabel?: string;
  type?: 'button' | 'submit' | 'reset';
  onClick?: () => void;
  children?: React.ReactNode;
}

/** Primary action control. Filled / Outlined / Text / Danger, with a loading state. */
export declare function Button(props: ButtonProps): JSX.Element;
