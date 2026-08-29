import * as React from 'react';

export interface CoverageStatusMeta {
  /** Status key — the derived InsurancePolicy.CoverageStatus, or Archived (lifecycle). */
  key: 'Active' | 'ExpiringSoon' | 'Lapsed' | 'Upcoming' | 'NoCoverage' | 'Archived';
  /** Visible label — the status meaning, conveyed as text (a11y). */
  label: string;
  /** Chip tone → semantic color. */
  tone: 'income' | 'pending' | 'expense' | 'info' | 'outline';
  /** Lead with a status dot when no icon is requested. */
  dot: boolean;
  /** Material Icons ligature, shown when `showIcon` is set. */
  icon: string;
}

/** Canonical coverage-status vocabulary, in display order (Archived last). */
export declare const COVERAGE_STATUSES: CoverageStatusMeta[];

export interface CoverageStatusChipProps {
  /** Derived status key (or Archived for an archived policy). Defaults to "NoCoverage". */
  status?: 'Active' | 'ExpiringSoon' | 'Lapsed' | 'Upcoming' | 'NoCoverage' | 'Archived';
  /** Optional muted trailing segment, e.g. "12 days left" / "ended Jun 1". */
  detail?: React.ReactNode;
  /** Lead with the status glyph instead of the status dot. */
  showIcon?: boolean;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * The derived coverage status of an insurance policy as a chip — tone-colored
 * dot/icon + the status word as visible text (the meaning never rides on colour
 * alone) + an optional muted detail segment. Insurance's sibling of
 * AccountStatusChip.
 */
export declare function CoverageStatusChip(props: CoverageStatusChipProps): JSX.Element;
