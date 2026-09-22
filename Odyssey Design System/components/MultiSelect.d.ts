import * as React from 'react';

export interface MultiSelectOption {
  value: string;
  /**
   * The row's content. A plain string for an ordinary option; a node when the
   * row needs more than one voice (a coloured figure beside muted qualifying
   * text) — pair that with `text` so search has something to match.
   */
  label: React.ReactNode;
  /** Plain-text form of `label`, used for search when `label` is a node. */
  text?: string;
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
  /**
   * A literal character used as the trigger's mark instead of an icon
   * ligature — for marks the icon font has no glyph for (§, №, ‰). Typeset in
   * the UI font at icon size, and it wins over `icon` when both are given.
   */
  glyph?: string;
  /**
   * Withhold the trigger's border and fill until hover, focus or open — for a
   * trigger that is a heading as much as a control (a chart's own series
   * picker). Padding is unchanged, so nothing shifts when the chrome appears.
   */
  quiet?: boolean;
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
