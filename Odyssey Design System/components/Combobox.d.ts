import * as React from 'react';

export interface ComboboxOption {
  value: string;
  label: string;
  /** Optional leading Material Icons glyph, shown in the row + beside the value. */
  icon?: string;
  /** Color for `icon`. */
  iconColor?: string;
}

export interface ComboboxProps {
  value?: string;
  /** Fires with the chosen value and the full option. */
  onChange?: (value: string, option: ComboboxOption) => void;
  /** Options as {value,label} objects or plain strings. */
  options: Array<ComboboxOption | string>;
  placeholder?: string;
  /**
   * Enables an inline "Create …" row when the query matches no option.
   * Receives the typed text; return the new value or a {value,label} option.
   */
  onCreate?: (text: string) => string | ComboboxOption | undefined;
  /** Prefix for the create row label. Default "Create". */
  createLabel?: string;
  /** Text shown when nothing matches and create is unavailable. */
  emptyText?: string;
  /** Show a keyboard-operable clear (×) button once a value is selected; clears to ''. */
  clearable?: boolean;
  /** Render an announced "Loading…" row instead of results. */
  loading?: boolean;
  /** Accessible name for the input when there is no visible label wrapping it. */
  ariaLabel?: string;
  /** id of an element describing the input (help / error line) — aria-describedby. */
  ariaDescribedBy?: string;
  /** Flip the input to aria-invalid (paired with an error message). */
  invalid?: boolean;
  disabled?: boolean;
  id?: string;
}

/** Searchable single-select with optional inline create — contact / tag pickers. */
export declare function Combobox(props: ComboboxProps): JSX.Element;
