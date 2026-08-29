import * as React from 'react';

export interface SegmentedOption {
  value: string;
  label: React.ReactNode;
  /** Leading Material Icons ligature. */
  icon?: string;
  /** Tints the option when selected — income (mint) / expense (coral). */
  tone?: 'income' | 'expense';
  disabled?: boolean;
}

export interface SegmentedControlProps {
  /** Options as {value,label,icon?,tone?} objects or plain strings. */
  options: Array<SegmentedOption | string>;
  /** Selected value (controlled). */
  value?: string;
  onChange?: (value: string) => void;
  /** Stretch each segment to fill the container width. */
  full?: boolean;
  ariaLabel?: string;
}

/** Compact single-select toggle — a button-bar sibling of RadioGroup. */
export declare function SegmentedControl(props: SegmentedControlProps): JSX.Element;
