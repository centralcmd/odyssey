import * as React from 'react';

/** Why a point's value is what it is. Shape and stroke carry the distinction —
 *  never fill colour, so the marking survives the chart palette. */
export type LineChartPointKind =
  /** Fully measured from stored data. Filled dot, solid segments. */
  | 'normal'
  /** **Understated** — a contributing account had no exchange rate as of this
   *  point, so it contributed 0. Hollow dot, dashed adjoining segments. As an
   *  endpoint it suppresses the delta. */
  | 'partial'
  /** **A real movement** — an estimate took effect in this period, so the step
   *  is disclosed rather than smoothed. Filled dot with a vertical tick. Does
   *  not suppress the delta. */
  | 'revalued';

export interface LineChartPoint {
  /** Category-axis label (e.g. a year or month). Rendered as-is. */
  label: React.ReactNode;
  /** The y value at this point. May be negative. */
  value: number;
  /** Default `'normal'`. */
  kind?: LineChartPointKind;
}

export interface LineChartProps {
  /** The series, oldest → newest. Points with a null `value` are skipped. */
  series: LineChartPoint[];
  /** Line + area + dot color. Default `var(--chart-1)`. Use a categorical chart token. */
  color?: string;
  /** Plot the running total of `value` instead of each point's own value. */
  cumulative?: boolean;
  /** Card title (left of the head). */
  title?: React.ReactNode;
  /** Sub-line under the title. A node, so a consumer can pass note sentences
   *  (e.g. the understated / revalued disclosures) rather than one caption. */
  sub?: React.ReactNode;
  /** Formats the headline figure + (by default) the y-axis ticks. Default `toLocaleString`. */
  format?: (n: number) => React.ReactNode;
  /** Compact y-axis tick formatter; falls back to `format`. */
  axisFormat?: (n: number) => React.ReactNode;
  /** Show a latest-vs-first delta beside the figure (mint up / coral down).
   *  Withheld when either endpoint is `partial` — an understated endpoint makes
   *  the difference meaningless. A `revalued` endpoint does not withhold it. */
  showDelta?: boolean;
  /** Trailing text on the delta, e.g. "all-time" or "vs 2024". */
  deltaSuffix?: string;
  /** Override the headline figure node (else the latest point, via `format`). */
  figure?: React.ReactNode;
  /** Render every Nth category label (the last is always shown). Default 1.
   *  `'auto'` derives a stride from the point count that never leaves two
   *  adjacent labels at the tail. */
  xTickEvery?: number | 'auto';
  /** Fill the area under the line. Anchors at zero when the value domain
   *  straddles it, at the plot floor otherwise. Default true. */
  area?: boolean;
  /** Show the marker key under the plot when the series contains `partial` or
   *  `revalued` points. Default true; nothing renders for an all-`normal`
   *  series either way. */
  markLegend?: boolean;
  /** Render a visually-hidden table of every point, its value and its state.
   *  **Opt-in** — off by default so existing consumers' assistive-technology
   *  output is unchanged. */
  textEquivalent?: boolean;
  /** Header for the text equivalent's first column. Default "Period". */
  textEquivalentLabel?: string;
  ariaLabel?: string;
  className?: string;
  /** Shown when the series has no plottable points. State the cause — the
   *  reason a history is empty is not the same as "no data yet". */
  emptyLabel?: React.ReactNode;
}

/**
 * The axis'd trend chart (vs. the axis-less `Sparkline`): a card with a
 * title/figure head over an SVG line+area plot with gridlines and axis labels.
 * Backs the Dashboard net-worth chart and the Tax Statements overview.
 *
 * Handles a **reconstructed** series as well as a monotone one: values may be
 * negative (the area then anchors at a labelled zero gridline), and each point
 * may declare a `kind` so an understated figure is visibly and textually
 * distinct from a revaluation step. DS-tab card: components/linechart.html.
 */
export declare function LineChart(props: LineChartProps): JSX.Element;
