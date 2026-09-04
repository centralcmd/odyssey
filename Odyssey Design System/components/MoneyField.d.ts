export interface MoneyCurrencyOption {
  /** ISO 4217 code — what is shown in the box and returned by onCurrencyChange. */
  value: string;
  /** Optional descriptive name shown next to the code in the list ("Norwegian krone"). */
  label?: string;
}

export interface MoneyFieldProps {
  /** Visible label, rendered above the control and tied to it via htmlFor. */
  label?: string;
  /** Controlled amount, kept as a string so partial entries aren't clobbered. A second decimal separator (or a non-leading minus) is blocked as typed, never rewritten. */
  value?: string;
  /** Fires with the sanitized next amount string first, the native event second. Parse on submit. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** ISO 4217 code shown on the right, inside the same box ("NOK", "USD"). */
  currency?: string;
  /** Fires with the picked ISO code. Omit to render the code as static text. */
  onCurrencyChange?: (currency: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Selectable currencies — ISO code strings or {value, label} objects. */
  currencyOptions?: Array<string | MoneyCurrencyOption>;
  /** Set false to lock the currency (account currency, base currency) — the code renders as static text. Default true. */
  currencyEditable?: boolean;
  /** Locks only the currency while the amount stays editable. Default false. */
  currencyDisabled?: boolean;
  /** Shown in place of the code when `currency` is empty. Default "—". */
  currencyPlaceholder?: string;
  /** Option count above which the picker shows a search box (matches code or name). Default 8; set 0 to always search, Infinity to never. */
  currencySearchThreshold?: number;
  placeholder?: string;
  /** "md" for data-entry rows (default); "lg" for a hero amount input. */
  size?: 'md' | 'lg';
  /** Leading sign glyph inside the box ("−", "+") — for signed amounts whose direction is set elsewhere in the form. */
  sign?: string;
  /** Current direction. With `onDirectionChange` the leading segment becomes a button that flips expense ↔ income, and drives the sign and tone itself. */
  direction?: 'income' | 'expense';
  /** Fires with the next direction when the leading segment is clicked — or when − / + is typed in the amount. Omit for a read-only sign. */
  onDirectionChange?: (direction: 'income' | 'expense', event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Turns the leading segment into a − / + toggle over the value's own sign — for signed amounts with no income/expense meaning (a correction, an adjustment). The minus is picked, not typed; `value` stays signed. */
  signEditable?: boolean;
  /** Colors the sign and amount by direction, using the finance income / expense hues. */
  tone?: 'income' | 'expense';
  /** Text alignment of the amount. Default "left". */
  align?: 'left' | 'right';
  /** Accept a leading minus — refunds, corrections, negative adjustments. Default true; set false where a negative is meaningless. */
  allowNegative?: boolean;
  /** Helper text shown below the control. */
  help?: React.ReactNode;
  /** Error message — flips the control to its error state and replaces the helper. */
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label. Mutually exclusive with `required`. */
  optional?: boolean;
  /** Disables the whole control (amount and currency). */
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
}

/**
 * The canonical money editor: amount plus its ISO currency code as one control.
 * The code sits on the right inside the same box and is either a picker or
 * static text (`currencyEditable={false}` / no options). Use this instead of
 * pairing AmountField with a separate currency Select; AmountField remains the
 * right choice for non-money numerics (rates, percentages, units).
 */
export declare function MoneyField(props: MoneyFieldProps): JSX.Element;
