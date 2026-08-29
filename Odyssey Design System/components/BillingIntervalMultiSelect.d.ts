import * as React from 'react';
import { BillingIntervalDef } from './BillingIntervalSelect';

export interface BillingIntervalMultiSelectProps {
  /** Selected enum keys. */
  value?: string[];
  /** Fires the next selected-key array. */
  onChange?: (values: string[]) => void;
  /** Trigger label when nothing is selected. Default "Any interval". */
  label?: string;
  /** Trigger glyph. Default "autorenew". */
  icon?: string;
  /** Popover alignment. */
  align?: 'start' | 'end';
  /** Override the registry (defaults to BILLING_INTERVALS). */
  types?: BillingIntervalDef[];
}

/**
 * Multi-select filter pre-wired to the BillingInterval vocabulary — each row
 * carries its Material icon in its category color. A typed wrapper over
 * `MultiSelect`; used for the Subscriptions list's Interval filter.
 */
export declare function BillingIntervalMultiSelect(props: BillingIntervalMultiSelectProps): JSX.Element;
