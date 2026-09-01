import * as React from 'react';

export interface MultiSelectOption {
  value: string;
  label: string;
  /** Optional leading Material Icons ligature, shown on the option row. */
  icon?: string;
  /** Optional color for that icon (e.g. an oklch category hue). Defaults to currentColor. */
  iconColor?: string;
}

export interface MultiSelectProps {
  /** Trigger label (the filter's name, e.g. "Status"). */
  label?: string;
  /** Selected values. */
  value?: string[];
  /** Fires with the next array of selected values. */
  onChange?: (values: string[]) => void;
  /** Options as {value,label} objects or plain strings. */
  options: Array<MultiSelectOption | string>;
  /** Leading Material Icons ligature on the trigger. */
  icon?: string;
  /** Which edge the popover anchors to. */
  align?: 'start' | 'end';
  /** Show the search field. Defaults to on once there are more than 8 options. */
  searchable?: boolean;
  /** Accessible name of the search field. Defaults to `Search {label}`. */
  searchLabel?: string;
  /** Visible placeholder of the search field. Default "Search…". */
  searchPlaceholder?: string;
  /** Options are still loading — an announced row, distinct from "no matches". */
  loading?: boolean;
  /** Copy for that row. Default "Loading…". */
  loadingText?: string;
  /** Row shown when the search matches nothing. Default "No matches". */
  emptyText?: string;
}

/** Checkbox-list filter with a count badge — the ledger header filters. */
export declare function MultiSelect(props: MultiSelectProps): JSX.Element;
