import * as React from 'react';
import { TransactionFileType } from './TransactionFileTypeSelect';

export interface TransactionFileTypeMultiSelectProps {
  value?: string[];
  onChange?: (values: string[]) => void;
  /** Trigger label. Defaults to "Any type". */
  label?: string;
  /** Leading Material Icons ligature on the trigger. Defaults to "receipt_long". */
  icon?: string;
  align?: 'start' | 'end';
  types?: TransactionFileType[];
}

/**
 * Checkbox-list filter pre-wired to the TransactionFileType vocabulary — each row
 * in its category color, count badge on the trigger. A typed wrapper over `MultiSelect`.
 */
export declare function TransactionFileTypeMultiSelect(props: TransactionFileTypeMultiSelectProps): JSX.Element;
