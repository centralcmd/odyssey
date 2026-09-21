import * as React from 'react';

export interface SectionDividerProps {
  /** The section name. Rendered uppercase — write it in sentence case. */
  label: React.ReactNode;
  /** Optional right-aligned mono note: a count, a date, "in force since 12 Mar 2021". */
  meta?: React.ReactNode;
  className?: string;
  id?: string;
  /**
   * Makes the LABEL a programmatic focus target: emits this as the label's
   * `id`, plus `tabindex="-1"` and heading semantics. Set it when something in
   * the section deletes the element holding focus and needs a destination
   * (`document.getElementById(headingId).focus()`). Omit otherwise — the label
   * then stays a plain, unfocusable span.
   */
  headingId?: string;
  /** `aria-level` for the heading. Only applies with `headingId`. Default 3. */
  headingLevel?: number;
}

/** Uppercase label + hairline rule + mono meta. The one section divider inside record bodies. */
export declare function SectionDivider(props: SectionDividerProps): JSX.Element;
