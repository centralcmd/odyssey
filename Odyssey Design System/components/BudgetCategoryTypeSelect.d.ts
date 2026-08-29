export interface BudgetCategoryTypeEntry {
  key: string;
  label: string;
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
}

/** Canonical BudgetCategoryType registry — mirrors the C# BudgetCategoryType enum (Expense = 0, Income = 1). */
export declare const BUDGET_CATEGORY_TYPES: BudgetCategoryTypeEntry[];

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
