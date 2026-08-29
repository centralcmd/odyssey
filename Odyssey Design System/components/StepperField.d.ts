import * as React from 'react';

export interface StepperFieldProps {
  /** Optional label. When omitted (and no help/error), renders just the bare control for embedding. */
  label?: string;
  /** Controlled numeric value, or null/empty when unset. */
  value?: number | string | null;
  /** Fires with the parsed number (or null when cleared) first, the native event second. */
  onChange?: (value: number | null, event?: React.ChangeEvent<HTMLInputElement>) => void;
  /** Unit in its singular form (e.g. "week", "occurrence"). Auto-pluralizes when the value ≠ 1. */
  unit?: string;
  /** Explicit plural, when the unit isn't a simple `+s` (e.g. "day"/"days" is fine; "day"→"dais" is not). */
  unitPlural?: string;
  /** Minimum value. Default 1. */
  min?: number;
  /** Maximum value. */
  max?: number;
  /** Step increment. Default 1. */
  step?: number;
  help?: string;
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  id?: string;
}

/**
 * A compact integer input paired with a trailing unit that auto-pluralizes with
 * the value ("every 2 weeks", "after 10 occurrences") — the count sibling of
 * `AmountField`. Emits a number or null.
 */
export declare function StepperField(props: StepperFieldProps): JSX.Element;
