import * as React from 'react';

export interface EmptyStateProps {
  /** Material icon name for the icon tile. Panel variant only. */
  icon?: string;
  /** The sentence. In the line variant this IS the whole component. */
  title?: React.ReactNode;
  /** Supporting sentence. Panel variant only (the line falls back to it if no title). */
  desc?: React.ReactNode;
  /** A single CTA — pass a <Button>. Panel variant only. */
  action?: React.ReactNode;
  /** Dim the icon tile — for "no match" rather than "nothing yet". Panel only. */
  mutedIcon?: boolean;
  /** `panel` = centered icon/title/desc/action. `line` = thin muted sentence. */
  variant?: 'panel' | 'line';
  /** Line variant: text alignment. Centre it when the line replaces a whole list. */
  align?: 'start' | 'center';
  /** Line variant: vertical breathing room. `lg` for a whole-list empty. */
  pad?: 'sm' | 'md' | 'lg';
  className?: string;
}

/** The "nothing here yet" content — a centered panel, or a thin inline line. */
export declare function EmptyState(props: EmptyStateProps): JSX.Element;
