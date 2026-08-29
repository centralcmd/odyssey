import * as React from 'react';

/**
 * Derived per-cycle billing position for an interval, from an ISO
 * (YYYY-MM-DD) first-billing date: "day 15" (Monthly), "15 Jan" (Yearly),
 * "Wed" (Weekly), or null (Daily). Parsed as UTC so it never drifts by a zone.
 */
export declare function billingAnchorLabel(interval: string, firstBillingDate?: string | null): string | null;

/**
 * Cadence label honouring the "every N" multiplier: count 1 → the plain enum
 * label ("Monthly"); count > 1 → "Every N months / years / weeks / days".
 */
export declare function billingIntervalLabel(interval: string, count?: number, fallbackLabel?: string): string;

export interface BillingIntervalChipProps {
  /** BillingInterval key. Default "Monthly". */
  interval?: 'Daily' | 'Weekly' | 'Monthly' | 'Yearly';
  /** The "every N" multiplier (int ≥ 1). 1 → "Monthly"; 2 → "Every 2 months". */
  count?: number;
  /** ISO YYYY-MM-DD anchor date; the per-cycle position is derived from it. */
  firstBillingDate?: string | null;
  /** Override the derived anchor string (skips the internal derivation). */
  anchor?: React.ReactNode;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A subscription's billing cadence as a chip — colored interval glyph + label +
 * the DERIVED billing anchor as a muted trailing segment ("Monthly · day 15",
 * "Yearly · 15 Jan", "Weekly · Wed", "Daily"). Subscriptions' sibling of
 * AccountTypeChip; the anchor is computed, never stored.
 */
export declare function BillingIntervalChip(props: BillingIntervalChipProps): JSX.Element;
