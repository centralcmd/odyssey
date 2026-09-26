import * as React from 'react';

export interface StepChartPoint {
  /** ISO date (yyyy-mm-dd) the value takes effect. Drives the real time axis. */
  date: string;
  /** The value from this date until the next entry. May be negative. */
  value: number;
  /** Stable key for the dot; falls back to the index. */
  id?: string;
  note?: string;
}

export interface StepChartLine {
  /** Stable key. */
  id: string;
  /** Legend name. */
  label: React.ReactNode;
  /** This line's colour. Give overlaid lines DISTINCT colours — two red
   *  expense lines are two unidentifiable lines. */
  color?: string;
  points: StepChartPoint[];
}

export interface StepChartProps {
  /** The entries, any order — sorted by `date` internally. Null values skipped. */
  series: StepChartPoint[];
  /**
   * Compare several histories (supersedes `series`). Plotted as **indexed
   * change** — each series as its percentage move from its own first entry —
   * because on one absolute axis the largest series owns the domain and a
   * lease's 95 parking space collapses onto the floor beside its 2,250 rent. The
   * y-axis then reads in percent and `axisFormat` is not used; the legend
   * carries each line's real value in force plus its move, so the money is
   * still there. A series that has never changed still plots, flat at 0% —
   * that is an answer, not an absence of one. Because indexed mode starts
   * every line at 0%, all lines are fanned by a hair (well under the stroke
   * width) so shared spans never collapse into one visible stroke; a
   * never-changed row reads "no changes yet" in place of a percentage.
   * Only overlay series
   * sharing a unit and a currency — the component cannot know that, and the
   * legend's figures would mix.
   */
  lines?: StepChartLine[];
  /**
   * How the y-axis is plotted. `"auto"` (default) indexes a `lines`
   * comparison and shows real figures for a single series — the useful
   * default in each case. `"indexed"` draws every series as its percentage
   * move from its own first entry, the only legible option when magnitudes
   * differ (a 2,250 rent beside a 95 parking space), and also the way to read
   * one series' shape. `"absolute"` plots the real figures: the magnitudes
   * are true, at the cost of crowding the smaller series. Expose it as a
   * control rather than choosing for the reader.
   */
  scale?: 'auto' | 'indexed' | 'absolute';
  /**
   * How entries are joined. `"step"` (default) holds each value until the
   * next entry, then jumps — right for a price, a rate, a term. `"linear"`
   * draws straight segments between entries and `"smooth"` a monotone curve
   * (no overshoot) — for a value that drifts between readings, such as an
   * estimated market value. The last entry holds flat to the edge in all modes.
   */
  curve?: 'step' | 'linear' | 'smooth';
  /** Line + area + dot color for the single-series form. Default `var(--chart-1)`. */
  color?: string;
  /** Card title (left of the head). */
  title?: React.ReactNode;
  /** Sub-line under the title. A node, so a consumer can pass disclosures. */
  sub?: React.ReactNode;
  /** Formats the headline figure, the delta and (absent `axisFormat`) the ticks. */
  format?: (n: number) => React.ReactNode;
  /** Compact y-axis tick formatter; falls back to `format`. Unused in `lines`
   *  mode, where the axis is indexed percent. */
  axisFormat?: (n: number) => React.ReactNode;
  /**
   * Show the change beside the figure — **the in-force entry vs the one before
   * it**, not vs the first. What a price history is read for is what just
   * changed; the earliest entry is often years of irrelevance away. Withheld
   * where nothing has changed yet, and in `lines` mode (no one series owns the
   * head).
   */
  showDelta?: boolean;
  /** Trailing text on the delta, e.g. "vs Mar ’26". */
  deltaSuffix?: string;
  /**
   * `"signed"` (default) colours the delta income-green / expense-red by its
   * sign. `"neutral"` renders it secondary grey — the right choice whenever
   * the figure already carries a colour meaning (see `figureColor`), or where
   * a rise is not inherently good: on an expense, +100 is not income.
   */
  deltaTone?: 'signed' | 'neutral';
  /** Override the headline figure node (else the value IN FORCE, via `format`). */
  figure?: React.ReactNode;
  /**
   * Colour for the headline figure — for a figure whose own meaning is a hue
   * the product already assigns (a term's incoming/outgoing direction, a
   * kind). Pair with `deltaTone="neutral"` so the two do not compete.
   */
  figureColor?: string;
  /** Fill under the in-force part of the line. Single-series only. Default true. */
  area?: boolean;
  /**
   * Show the head's figure + delta at all. Default true. Turn it off where
   * the legend already carries every line's value in force and its move, so
   * the head is the control and nothing else.
   */
  showFigure?: boolean;
  /**
   * Node placed at the top-right of the head, above the figure — the chart's
   * own controls (a series picker, a range toggle). Lives inside the card so
   * the control and what it changes are one object.
   */
  controls?: React.ReactNode;
  /**
   * Node placed at the top-RIGHT of the head, where the figure sits — for a
   * control that changes how the chart reads (an axis or range toggle) rather
   * than what it plots.
   */
  controlsEnd?: React.ReactNode;
  /** Text on the present-day marker. Default "Today". */
  nowLabel?: string;
  /** Visually-hidden table of every entry, its value and its state. Opt-in. */
  textEquivalent?: boolean;
  /** Header for the text equivalent's first column. Default "Effective from". */
  textEquivalentLabel?: string;
  ariaLabel?: string;
  className?: string;
  /** Shown when the series has no plottable entries. State the cause. */
  emptyLabel?: React.ReactNode;
}

/**
 * The dated-value chart — a price, a rate, a term. Sibling of `LineChart` in
 * the same card, with the same head, gridlines, axis type and typography; what
 * differs is what a trend chart gets wrong about a held value:
 *
 * - **A real time axis** — entries land whenever they were signed, so a rent
 *   held for eight months and then twice in a quarter must not read as three
 *   equal steps.
 * - **Today** — the line holds solid to the present and dashes past it, with a
 *   hollow dot on a scheduled entry, so a future increase is visibly not yet
 *   true.
 *
 * `lines` compares several histories, as indexed change by default or real
 * figures with `scale="absolute"`.
 *
 * The line is a staircase by default: a price does not drift, it holds
 * until the day it changes. `curve="linear" | "smooth"` joins entries directly,
 * for values that drift between readings (estimates). Every figure it states — head, delta, legend, text
 * equivalent — is the value **in force** (the latest entry that has already
 * taken effect), never a scheduled one: a future increase is drawn and
 * disclosed in `sub`, but it is not what the thing costs. Backs the contract &
 * account term charts.
 */
export declare function StepChart(props: StepChartProps): JSX.Element;
