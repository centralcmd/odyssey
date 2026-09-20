import * as React from 'react';

export interface EmptyLineProps {
  /** The sentence. Equivalent to passing children. */
  text?: React.ReactNode;
  children?: React.ReactNode;
  /** Centre it when the line replaces a whole list rather than one section. */
  align?: 'start' | 'center';
  /** Vertical breathing room. `lg` for a whole-list empty, `sm` inside a dense frame. */
  pad?: 'sm' | 'md' | 'lg';
  className?: string;
}

/** The thin muted "nothing here yet" sentence — <EmptyState variant="line">. */
export declare function EmptyLine(props: EmptyLineProps): JSX.Element;
