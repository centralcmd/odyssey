export interface SelectOption {
  value: string;
  label: string;
  /** Optional leading Material Icons ligature, shown in the trigger and the option row. */
  icon?: string;
  /** Optional color for that icon (e.g. an oklch category hue). Defaults to currentColor. */
  iconColor?: string;
}

export interface SelectProps {
  label?: string;
  /** Inline muted label rendered INSIDE the trigger before the value (e.g. "View  Board ▾"), matching the SortSelect "Sort by" prefix. Use instead of `label` for a compact toolbar control with no stacked label above. */
  prefix?: React.ReactNode;
  value?: string;
  /** Fires with the selected value first, the native event second. */
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Options as {value,label} objects or plain strings. */
  options?: Array<SelectOption | string>;
  /** Placeholder shown on the trigger when nothing is selected. */
  placeholder?: string;
  help?: string;
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label (the canonical optional marker). Mutually exclusive with `required`. */
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/** A single-select with a fully-themed popover menu (not a native select). */
export declare function Select(props: SelectProps): JSX.Element;
