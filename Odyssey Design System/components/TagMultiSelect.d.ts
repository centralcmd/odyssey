import * as React from 'react';

export interface TagMultiSelectOption {
  value: string;
  label: string;
}

export interface TagMultiSelectProps {
  /** Field label. */
  label?: React.ReactNode;
  /** Selected tag ids. */
  value?: string[];
  /** Fires with the full next array of selected ids on every add / remove. */
  onChange?: (ids: string[]) => void;
  /** Tag options as {value,label} objects or plain strings. */
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
  error?: React.ReactNode;
  required?: boolean;
  /** Show an "Optional" hint beside the label. */
  optional?: boolean;
  disabled?: boolean;
  /** Text shown when the search matches nothing and create is unavailable. */
  emptyText?: string;
  className?: string;
  id?: string;
}

/** Multi-tag picker for the transaction forms — removable chips + searchable, creatable list. */
export declare function TagMultiSelect(props: TagMultiSelectProps): JSX.Element;
