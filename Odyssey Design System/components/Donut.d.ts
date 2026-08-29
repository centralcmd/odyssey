import * as React from 'react';

export interface DonutSlice {
  /** Slice / legend-row name. */
  label: string;
  value: number;
  /** Override the auto-assigned --chart-* color for this slice. */
  color?: string;
}

export interface DonutProps {
  /** Slices in draw order (largest-first by convention); zero-values are dropped. */
  data: DonutSlice[];
  /** Panel heading (e.g. "Asset allocation"). Omit to hide the head. */
  title?: React.ReactNode;
  /** Sub-line under the title (e.g. "Where your money sits · 4 accounts"). */
  sub?: React.ReactNode;
  /** Muted Material Icons watermark in the ring hole (e.g. "savings"). Never a number. */
  centerIcon?: string;
  /** Label on the total row at the foot of the legend. Default "Total". */
  totalLabel?: React.ReactNode;
  /** Formats slice + total values (e.g. (v) => H.money(v)). Default identity. */
  format?: (value: number, slice?: DonutSlice) => React.ReactNode;
  /** row = ring beside legend (default, single-donut look) · stack = ring above legend (two-up). */
  layout?: 'row' | 'stack';
  /** Outer diameter in px. */
  size?: number;
  /** Ring thickness in px. */
  thickness?: number;
  /** Slice palette. Defaults to the categorical --chart-1…6 tokens. */
  colors?: string[];
  /** Gap between slices in px (same-family hues stay distinct). */
  gap?: number;
  /** Unfilled track color behind the slices. */
  trackColor?: string;
  /** Accessible summary of the breakdown. */
  ariaLabel?: string;
}

/** Allocation panel — ring (watermark icon) beside/above a legend ledger + total row. The sum lives in the legend, never the hole. */
export declare function Donut(props: DonutProps): JSX.Element;
export declare namespace Donut {
  /** Two panels side by side with a hairline divider (assets/liabilities). Use stacked panels inside. */
  function Pair(props: { children?: React.ReactNode }): JSX.Element;
}

export interface DonutLegendProps {
  /** Same data passed to <Donut> — colors line up by order. */
  data: DonutSlice[];
  colors?: string[];
  /** Label on the total row. Default "Total". */
  totalLabel?: React.ReactNode;
  /** Value formatter, e.g. (v) => H.money(v). */
  format?: (value: number, slice?: DonutSlice) => React.ReactNode;
  /** Show the total row. Default true. */
  showTotal?: boolean;
}

/** The "ledger" under/beside a Donut — slice rows (swatch · name · % · amount) plus the total row. */
export declare function DonutLegend(props: DonutLegendProps): JSX.Element;
