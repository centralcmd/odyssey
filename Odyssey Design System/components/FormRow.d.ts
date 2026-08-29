import * as React from 'react';

export interface FormRowProps {
  /** Number of equal-width columns. Default 2. */
  cols?: number;
  /** Gap between cells, in px. Default 14. */
  gap?: number;
  /** Vertical alignment of cells. Default "start" so helper lines don't drag neighbours down. */
  align?: 'start' | 'center' | 'end' | 'stretch';
  className?: string;
  style?: React.CSSProperties;
  children?: React.ReactNode;
}

/** An equal-width column grid for paired form fields (the component form of .aam-row2). */
export declare function FormRow(props: FormRowProps): JSX.Element;
