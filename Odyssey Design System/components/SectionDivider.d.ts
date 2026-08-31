import * as React from 'react';

export interface SectionDividerProps {
  /** The section name. Rendered uppercase — write it in sentence case. */
  label: React.ReactNode;
  /** Optional right-aligned mono note: a count, a date, "in force since 12 Mar 2021". */
  meta?: React.ReactNode;
  className?: string;
  id?: string;
}

/** Uppercase label + hairline rule + mono meta. The one section divider inside record bodies. */
export declare function SectionDivider(props: SectionDividerProps): JSX.Element;
