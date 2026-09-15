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
  /** Current direction — one of `directionOptions`' values (default: 'expense' | 'income'). With `onDirectionChange` the left edge becomes a button that flips between the two states, showing each one's short word where a sign would be. */
  direction?: string;
  /** Fires with the next direction when the lead is clicked. Omit for no lead. */
  onDirectionChange?: (direction: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** The two states the lead flips between — same shape as MoneyField's: `{ value, label, short?, icon?, sign?, tone? }`. Use when the record stores a DIRECTION rather than a sign (a percentage-unit fee that is money in or out). Exactly two options. */
  directionOptions?: Array<{ value: string; label: string; short?: string; icon?: string; sign?: string; tone?: 'income' | 'expense' }>;
  /** Colors the lead and the value by finance semantics; defaults to the active direction's own tone. */
  tone?: 'income' | 'expense';
  /** Helper text shown below the input. */
  help?: string;
  /** Error message — flips the control to its error state and replaces the helper. */
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** @deprecated No-op — the system marks required only, never optional. */
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
}

/** A labelled money / numeric input with a currency-or-unit adornment and helper/error states. */
export declare function AmountField(props: AmountFieldProps): JSX.Element;
