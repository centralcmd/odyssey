import * as React from 'react';

export interface TransactionTagOption {
  /** TransactionTagId (the kit's demo data uses `id`). */
  id?: string;
  transactionTagId?: string;
  name: string;
  /** Shown as the field's help line while the tag is selected. */
  description?: string | null;
  /** ISO datetime, null when active. An archived tag reads `· Archived`. */
  archived?: string | null;
}

export interface TransactionTagPickerProps {
  /** Selected TransactionTagId. */
  value?: string;
  onChange?: (value: string, option?: { value: string; label: string }) => void;
  /** The tag list — active tags plus, where relevant, the record's archived tag. */
  tags: TransactionTagOption[];
  /** Tags already planned for elsewhere in this budget: offered, marked `in use`, not selectable. */
  usedTagIds?: string[];
  label?: string;
  /** Required per instance — a grid must not share one id across rows. */
  id?: string;
  /** Default true: the tag is the record's identity, so there is no "none". */
  required?: boolean;
  /** Fallback help line, used when the selected tag has no description. */
  help?: string;
  /** Field-level error (missing / duplicate / archived-on-new-link). */
  error?: string | null;
  disabled?: boolean;
  /** Keep the `<label for>` association, drop it visually — for a table cell. */
  hideLabel?: boolean;
  placeholder?: string;
  /** Gate on the caller's `transactions.tags.create` claim. */
  canCreate?: boolean;
  /**
   * Stage an inline create and return the new tag (or its id). Pass this ONLY
   * on a surface with a submit to resolve the staged id against — never on a
   * grid that saves on change. A duplicate name is refused before this is called.
   */
  onCreateTag?: (name: string) => TransactionTagOption | string | null | undefined;
  /** The tag list failed to load — distinct from "no tags exist". */
  loadFailed?: boolean;
  onRetry?: () => void;
  manageHref?: string;
  manageLabel?: string;
  className?: string;
}

/** Required transaction-tag picker — Combobox + FieldShell, tag-as-identity. */
export declare function TransactionTagPicker(props: TransactionTagPickerProps): JSX.Element;
