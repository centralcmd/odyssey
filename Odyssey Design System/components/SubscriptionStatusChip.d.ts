import * as React from 'react';

export interface SubscriptionStateMeta {
  key: 'Paused' | 'Ended' | 'Archived' | 'Active';
  /** Visible label — the state meaning, conveyed as text (a11y). */
  label: string;
  tone: 'pending' | 'expense' | 'outline' | 'income';
  dot: boolean;
  icon: string;
}

/** Canonical subscription-state vocabulary (Paused · Ended · Archived · Active). */
export declare const SUBSCRIPTION_STATES: SubscriptionStateMeta[];

export interface SubscriptionStatusChipProps {
  /** Paused = temporarily not billing but still visible. Boolean or a timestamp. */
  paused?: boolean | string | null;
  /** Ended = its term has lapsed (derived: endDate ≤ today). Supersedes Paused. Boolean or a timestamp. */
  ended?: boolean | string | null;
  /** Archived = retired and hidden. Only an ended subscription can be archived, so this supersedes Ended. Boolean or a timestamp. */
  archived?: boolean | string | null;
  /** Render the Active chip when neither state is set. Default false. */
  showActive?: boolean;
  /** Lead each chip with its glyph instead of the status dot. */
  showIcon?: boolean;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A subscription's lifecycle state as ONE chip, meaning conveyed as visible
 * text. A subscription has exactly one state; precedence is Archived → Ended →
 * Paused → Active (only an ended subscription can be archived). Renders nothing
 * for a plain active row unless `showActive` is set.
 * Subscriptions' sibling of CoverageStatusChip.
 */
export declare function SubscriptionStatusChip(props: SubscriptionStatusChipProps): JSX.Element;
