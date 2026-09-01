import * as React from 'react';

export interface TagMultiSelectOption {
  value: string;
  label: string;
  /** Material icon rendered before the label in the list and on a default chip. */
  icon?: string;
  /** Colour for that icon (a type/category accent). */
  iconColor?: string;
  /** Secondary text after the label in the list (e.g. an account number). */
  sub?: string;
}

export interface TagMultiSelectProps {
  /** Field label. Also names the popover's checkbox group. */
  label?: React.ReactNode;
  /** Selected ids. */
  value?: string[];
  /** Fires with the full next array of selected ids on every add / remove. */
  onChange?: (ids: string[]) => void;
  /** Options as {value,label,icon?,iconColor?,sub?} objects or plain strings. */
  options: Array<TagMultiSelectOption | string>;
  /** Text shown in the control when nothing is selected. Default "No tags". */
  placeholder?: string;
  /** Label beside the add glyph when empty. Default "Add tag". */
  addLabel?: string;
  /**
   * Enables an inline "Create …" row when the search matches no option.
   * Receives the typed text; return the new value or a {value,label} option —
   * it's added to the selection.
   */
  onCreate?: (text: string) => string | TagMultiSelectOption | undefined;
  /** Prefix for the create row label. Default "Create". */
  createLabel?: string;
  help?: React.ReactNode;
  /** Marks the field invalid: aria-invalid on the trigger + role="alert" message. */
  error?: React.ReactNode;
  required?: boolean;
  /** Show an "Optional" hint beside the label. */
  optional?: boolean;
  disabled?: boolean;
  /** Text shown when the search matches nothing and create is unavailable. */
  emptyText?: string;
  /** Options are still loading — an announced row, distinct from "no match". */
  loading?: boolean;
  /** Copy for that row. Default "Loading…". */
  loadingText?: string;
  /** Accessible name of the search field. Default "Search tags". */
  searchLabel?: string;
  /** Visible placeholder of the search field. */
  searchPlaceholder?: string;
  /** Label for a selected id absent from `options`, so no raw id is shown. Default "Unknown". */
  unknownLabel?: string;
  /**
   * Renders the chip BODY for a member (e.g. ContactChip with its Archived /
   * Unavailable states). The picker keeps owning the remove <button>, and the
   * default `.odc-chip` wrapper is not emitted.
   */
  chipTemplate?: (id: string) => React.ReactNode;
  /**
   * True for a member the picker must not remove: the bulk Clear keeps it (and
   * reports how many were kept) AND no remove control is rendered for it.
   */
  preserveOnClear?: (id: string) => boolean;
  /** Receives `{ focus() }` so a host can move focus to an invalid picker. */
  apiRef?: React.MutableRefObject<{ focus: () => void } | null>;
  /** Singular noun used in live-region announcements. Default "tag". */
  noun?: string;
  className?: string;
  id?: string;
}

/** Multi-member picker — removable chips + searchable, checkable list. */
export declare function TagMultiSelect(props: TagMultiSelectProps): JSX.Element;
