export interface CardHeaderProps {
  /** Heading text shown on the left (ignored when `children` is given). */
  title?: React.ReactNode;
  /** Right-aligned action cluster — buttons, a menu, a chip. */
  action?: React.ReactNode;
  /** Custom heading node in place of the default `title` treatment. */
  children?: React.ReactNode;
  className?: string;
}

/**
 * The titled header row at the top of a card: `title` (or custom `children`) on
 * the left, an optional `action` cluster on the right, over a bottom divider.
 */
export declare function CardHeader(props: CardHeaderProps): JSX.Element;
