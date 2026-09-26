import * as React from 'react';
import { StepChartPoint } from './StepChart';

/** One history the reader can plot — plain data, already resolved. */
export interface TermHistorySeries {
  /** Stable key. Colour leases are held against it for the life of the view. */
  key: string;
  /** Display name, e.g. "Monthly rent". Names the legend row and the option. */
  label: string;
  /** The value in force, already formatted ("2,250.00 USD", "9.25%"). */
  value?: string;
  /** Direction, stated after the value in the option and colouring it. */
  tone?: { label: string; color: string };
  /** Preferred line hue — normally the direction hue. Used unless another
   *  line on screen already holds it. */
  color?: string;
  /** Axis-compatibility key (unit + currency). Series in different groups
   *  never share an axis: picking one replaces the selection. */
  group?: string;
  points: StepChartPoint[];
  /** Value formatter (legend, tooltip, text table). Taken from the first
   *  selected series, so series in one group should agree. */
  format?: (v: number) => string;
  /** Tick formatter for the absolute axis. */
  axisFormat?: (v: number) => string;
}

export interface TermHistoryChartProps {
  /** Ordered histories; the FIRST is the default selection (normally the one
   *  changed most recently). Series without points are ignored. */
  series: TermHistorySeries[];
  /** Picker label, standing in for the card title. Default "Terms". */
  pickerLabel?: string;
  /** Picker glyph. Default "§". */
  glyph?: string;
  /** Fallback hues for a line whose own colour is taken. Its length is also
   *  the comparison cap. Default chart-1/2/4/6 — excludes the hues equal to
   *  income/expense so a fallback never asserts the opposite direction. */
  palette?: string[];
  searchLabel?: string;
  emptyText?: string;
  /** Header of the date column in the text-equivalent table. */
  textEquivalentLabel?: string;
  /** Passed to StepChart. `"step"` (default) for terms; `"smooth"` or
   *  `"linear"` for values that drift between entries, e.g. estimates. */
  curve?: 'step' | 'linear' | 'smooth';
  className?: string;
}

/**
 * A `StepChart` whose head is its controls: a quiet multi-select picker
 * (what is plotted) on the left, a Change / Value axis toggle on the right.
 * Owns the comparison rules — one unit per axis, stable per-series colours,
 * a cap of `palette.length` lines, never an empty plot.
 */
export declare function TermHistoryChart(props: TermHistoryChartProps): JSX.Element | null;
