import * as React from 'react';

export interface TimelineProps {
  className?: string;
  /** `TimelineItem` children, newest first. */
  children?: React.ReactNode;
}

export interface TimelineItemProps {
  /** Primary text — what changed (term kind, event name). */
  label?: React.ReactNode;
  /** Effective date, rendered in the mono face. */
  date?: React.ReactNode;
  /** Trailing nodes on the top row — a status pill, a billing tag. */
  meta?: React.ReactNode;
  /** Secondary line under the top row. */
  note?: React.ReactNode;
  /** Headline figure in the right-hand column. */
  value?: React.ReactNode;
  /** Under the value — typically a `Delta`. */
  aside?: React.ReactNode;
  /** Typically a `RowActions`; reveals on hover of this item. */
  actions?: React.ReactNode;
  /** Rail node color. Defaults to the income hue when `current`, else muted. */
  color?: string;
  /** Marks the in-force entry — tints the value. */
  current?: boolean;
  className?: string;
  /** Extra body content below `note`. */
  children?: React.ReactNode;
}

/**
 * Vertical rail history list — the alternative rendering of any
 * effective-dated record table (terms, estimates, renewals).
 * DS-tab card: components/timeline.html.
 */
export declare function Timeline(props: TimelineProps): JSX.Element;
export declare function TimelineItem(props: TimelineItemProps): JSX.Element;
