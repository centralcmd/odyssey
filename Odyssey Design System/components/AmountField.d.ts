export interface AmountFieldProps {
  /** Visible label, rendered above the control and tied to it via htmlFor. */
  label?: string;
  /** Controlled string value (kept as a string so partial entries aren't clobbered). */
  value?: string;
  /** Fires with the sanitized next string value first, the native event second. Parse to a number on submit. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** Leading adornment inside the box — typically a currency symbol ("$", "€", "kr"). */
  prefix?: string;
  /** Trailing adornment inside the box — typically a unit ("%", "bps", "/mo"). */
  suffix?: string;
  placeholder?: string;
  /** "md" for data-entry rows (default); "lg" for a hero amount input. */
  size?: 'md' | 'lg';
  /** Text alignment of the numeric value. Default "left". */
  align?: 'left' | 'right';
  /** Allow a leading minus — for rates/deltas that can go below zero. Default false. */
  allowNegative?: boolean;
  /** Helper text shown below the input. */
  help?: string;
  /** Error message — flips the control to its error state and replaces the helper. */
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label. Mutually exclusive with `required`. */
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
}

/** A labelled money / numeric input with a currency-or-unit adornment and helper/error states. */
export declare function AmountField(props: AmountFieldProps): JSX.Element;
