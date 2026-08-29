import * as React from 'react';

export interface LineChartPoint {
  /** Category-axis label (e.g. a year or month). Rendered as-is. */
  label: React.ReactNode;
  /** The y value at this point. */
  value: number;
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
  /** Sub-line under the title. */
  sub?: React.ReactNode;
  /** Formats the headline figure + (by default) the y-axis ticks. Default `toLocaleString`. */
  format?: (n: number) => React.ReactNode;
  /** Compact y-axis tick formatter; falls back to `format`. */
  axisFormat?: (n: number) => React.ReactNode;
  /** Show a latest-vs-first delta beside the figure (mint up / coral down). */
  showDelta?: boolean;
  /** Trailing text on the delta, e.g. "all-time" or "vs 2024". */
  deltaSuffix?: string;
  /** Override the headline figure node (else the latest point, via `format`). */
  figure?: React.ReactNode;
  /** Render every Nth category label (the last is always shown). Default 1. */
  xTickEvery?: number;
  /** Fill the area under the line. Default true. */
  area?: boolean;
  ariaLabel?: string;
  className?: string;
  /** Shown when the series has no plottable points. */
  emptyLabel?: React.ReactNode;
}

/**
 * The axis'd trend chart (vs. the axis-less `Sparkline`): a card with a
 * title/figure head over an SVG line+area plot with gridlines and axis labels.
 * Backs the Dashboard net-worth chart and the Tax Statements overview. DS-tab
 * card: components/linechart.html.
 */
export declare function LineChart(props: LineChartProps): JSX.Element;
