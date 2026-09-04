export interface CurrencyOption {
  /** ISO 4217 code — shown first, in mono, and returned by onChange. */
  value: string;
  /** Currency name shown beside the code ("Norwegian krone"). */
  label?: string;
}

export interface CurrencySelectProps {
  /** Visible label. Defaults to "Currency". */
  label?: string;
  /** Selected ISO 4217 code. */
  value?: string;
  /** Fires with the picked ISO code first, the native event second. */
  onChange?: (value: string, event: React.MouseEvent | React.KeyboardEvent) => void;
  /** Selectable currencies — ISO code strings or {value, label} objects. */
  options?: Array<string | CurrencyOption>;
  placeholder?: string;
  /** Option count above which the picker shows a search box (matches code or name). Default 8; 0 to always search. */
  searchThreshold?: number;
  /** Show the currency name beside the code on the trigger. Default true. */
  showName?: boolean;
  help?: React.ReactNode;
  /** Error message — flips the control to its error state and replaces the helper. */
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Currency-only picker — the same list, search and keyboard behaviour as the
 * currency segment of MoneyField, in the standard Select chrome. Use wherever a
 * currency is chosen without an amount (account currency, a budget's or tax
 * statement's base currency); use MoneyField when there IS an amount.
 */
export declare function CurrencySelect(props: CurrencySelectProps): JSX.Element;
