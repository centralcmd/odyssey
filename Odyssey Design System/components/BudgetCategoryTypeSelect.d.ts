export interface BudgetCategoryTypeEntry {
  key: string;
  label: string;
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
  /** Short word for a narrow slot — the money field's direction lead ("out" / "in"). */
  short?: string;
  /** Finance tone the value takes on when this direction is active. */
  tone?: 'income' | 'expense';
  /** What this direction means, for helper copy ("money out of the budget"). */
  sentence?: string;
}

/** Canonical BudgetCategoryType registry — mirrors the C# BudgetCategoryType enum (Expense = 0, Income = 1). */
export declare const BUDGET_CATEGORY_TYPES: BudgetCategoryTypeEntry[];

/**
 * The same two values shaped for `MoneyField` / `AmountField`'s
 * `directionOptions` — the left-edge lead that flips the value's direction
 * where a sign would be. A budget item records a direction, not a sign, so its
 * planned amount can carry the question and the form needs no separate type
 * picker beside it.
 */
export declare const BUDGET_CATEGORY_DIRECTION_OPTIONS: Array<{ value: string; label: string; short?: string; tone?: 'income' | 'expense' }>;

export interface BudgetCategoryTypeSelectProps {
  /** Selected BudgetCategoryType enum key ('Expense' | 'Income'). */
  value?: string;
  /** Fires with the picked key first, the native event second. */
  onChange?: (key: string, event: React.MouseEvent) => void;
  label?: string;
  placeholder?: string;
  /** Subset / reorder the registry; defaults to BUDGET_CATEGORY_TYPES. */
  types?: BudgetCategoryTypeEntry[];
  help?: string;
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/** Single-select pre-wired to the BudgetCategoryType vocabulary; delegates to the shared TypeSelect. */
export declare function BudgetCategoryTypeSelect(props: BudgetCategoryTypeSelectProps): JSX.Element;
