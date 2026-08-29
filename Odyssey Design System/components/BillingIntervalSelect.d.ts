import * as React from 'react';

/** A billing-interval definition — the canonical registry entry. */
export interface BillingIntervalDef {
  /** Enum key. */
  key: 'Daily' | 'Weekly' | 'Monthly' | 'Yearly';
  /** Visible label. */
  label: string;
  /** Numeric enum value — the sort order (Daily 0 < Weekly 1 < Monthly 2 < Yearly 3). */
  enumValue: number;
  /** Material Icons ligature. */
  icon: string;
  /** Category color (oklch). */
  color: string;
  /** Soft tint for the icon chip background. */
  soft: string;
}

/** Canonical BillingInterval vocabulary, in the enum's numeric order. */
export declare const BILLING_INTERVALS: BillingIntervalDef[];

export interface BillingIntervalSelectProps {
  /** Selected BillingInterval key, or '' / undefined when empty. */
  value?: string;
  /** Fires the newly-selected enum key. */
  onChange?: (key: string, event?: React.SyntheticEvent) => void;
  /** Field label. Default "Billing interval". */
  label?: React.ReactNode;
  /** Placeholder shown when nothing is selected. */
  placeholder?: string;
  /** Override the registry (defaults to BILLING_INTERVALS). */
  types?: BillingIntervalDef[];
  help?: React.ReactNode;
  error?: React.ReactNode;
  required?: boolean;
  disabled?: boolean;
  id?: string;
  className?: string;
}

/**
 * Single-select pre-wired to the Subscriptions BillingInterval vocabulary
 * (Daily · Weekly · Monthly · Yearly), each member with its Material icon in its
 * category color. A typed wrapper over the shared `TypeSelect`.
 */
export declare function BillingIntervalSelect(props: BillingIntervalSelectProps): JSX.Element;
