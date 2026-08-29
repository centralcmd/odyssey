import * as React from 'react';
import { AccountFileType } from './AccountFileTypeSelect';

export interface AccountFileTypeMultiSelectProps {
  value?: string[];
  onChange?: (values: string[]) => void;
  /** Trigger label. Defaults to "Any type". */
  label?: string;
  /** Leading Material Icons ligature on the trigger. Defaults to "folder". */
  icon?: string;
  align?: 'start' | 'end';
  types?: AccountFileType[];
}

/**
 * Checkbox-list filter pre-wired to the AccountFileType vocabulary — each row in
 * its category color, count badge on the trigger. A typed wrapper over `MultiSelect`.
 */
export declare function AccountFileTypeMultiSelect(props: AccountFileTypeMultiSelectProps): JSX.Element;
