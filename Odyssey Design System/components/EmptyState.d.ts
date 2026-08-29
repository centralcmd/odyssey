export interface EmptyStateProps {
  /** Material Icons ligature for the centered glyph. Defaults to "inbox". */
  icon?: string;
  /** One sentence stating the absence. */
  title?: React.ReactNode;
  /** Optional supporting line. */
  desc?: React.ReactNode;
  /** A single CTA — pass a <Button>. */
  action?: React.ReactNode;
  /** Dim the icon tile — for a "no results match your search" state. */
  mutedIcon?: boolean;
  className?: string;
}

/** Centered empty-state surface (icon · title · description · one action). */
export declare function EmptyState(props: EmptyStateProps): JSX.Element;
