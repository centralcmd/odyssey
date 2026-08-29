import * as React from 'react';
import { ContactType } from './ContactTypeSelect';

export interface ContactTypeMultiSelectProps {
  /** Selected enum keys. */
  value?: string[];
  /** Fires with the next array of selected keys. */
  onChange?: (values: string[]) => void;
  /** Trigger label. Defaults to "Any type". */
  label?: string;
  /** Leading Material Icons ligature on the trigger. Defaults to "store". */
  icon?: string;
  /** Which edge the popover anchors to. */
  align?: 'start' | 'end';
  /** Override / subset the registry (e.g. drop "Other"). */
  types?: ContactType[];
}

/**
 * Checkbox-list filter pre-wired to the ContactType vocabulary — each row
 * in its category color, count badge on the trigger. A typed wrapper over `MultiSelect`.
 */
export declare function ContactTypeMultiSelect(props: ContactTypeMultiSelectProps): JSX.Element;
