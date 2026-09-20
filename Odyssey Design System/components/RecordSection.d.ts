import * as React from 'react';

/** The tinted band that states why a section refuses writes. */
export interface RecordSectionNotice {
  text: React.ReactNode;
  /** `default` = neutral (a cap, an explanation). `warning` = reversible state (restore it). */
  tone?: 'default' | 'warning';
  /** Material icon name. Defaults to `info` / `inventory_2` by tone. */
  icon?: string;
}

export interface RecordSectionProps {
  /** Section name for the divider. Rendered uppercase — write it in sentence case. */
  label?: React.ReactNode;
  /** Right-aligned mono note: a count, a limit, a date. */
  meta?: React.ReactNode;
  /** A string, or `{ text, tone, icon }`. Sits between divider and view. */
  notice?: React.ReactNode | RecordSectionNotice;
  /** True when the section has nothing to show — renders `emptyText` as a muted line. */
  empty?: boolean;
  /** The sentence shown when `empty`. State the absence and what fills it. */
  emptyText?: React.ReactNode;
  emptyAlign?: 'start' | 'center';
  emptyPad?: 'sm' | 'md' | 'lg';
  /** The section's own view — rendered whenever `empty` is false. */
  children?: React.ReactNode;
  className?: string;
  id?: string;
}

/** A record-body band: divider + optional notice + custom view, with the empty line built in. */
export declare function RecordSection(props: RecordSectionProps): JSX.Element;
