import * as React from 'react';

export interface SparklineProps {
  /** The series. Auto-scaled to its own min/max. Needs ≥ 2 points to draw. */
  data: number[];
  width?: number;
  height?: number;
  /** Line color — a --chart-* token or any CSS color. Default --chart-1 (tide). */
  stroke?: string;
  /** Fill the area under the line (stroke color at low opacity). Default true. */
  area?: boolean;
  strokeWidth?: number;
  /** Dot on the last point. Default true. */
  showDot?: boolean;
  /** Accessible summary of the trend. */
  ariaLabel?: string;
}

/** Compact axis-less trend line — net-worth strips, stat-tile sparklines. */
export declare function Sparkline(props: SparklineProps): JSX.Element;
