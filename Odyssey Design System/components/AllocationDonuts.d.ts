import * as React from 'react';
import { DonutSlice } from './Donut';

export interface AllocationDonutsProps {
  /** Asset slices, one currency. Sorted largest-first; zero values drop. */
  assets?: DonutSlice[];
  /** Liability slices; values may be negative (absolute value is plotted). */
  liabilities?: DonutSlice[];
  assetsTitle?: React.ReactNode;
  liabilitiesTitle?: React.ReactNode;
  /** Sub-line — say what is counted and anything excluded (e.g. no FX rate). */
  assetsSub?: React.ReactNode;
  liabilitiesSub?: React.ReactNode;
  /** Total-row labels. Default "Total assets" / "Total owed". */
  assetsTotalLabel?: React.ReactNode;
  liabilitiesTotalLabel?: React.ReactNode;
  /** Ring watermark icons. */
  assetsIcon?: string;
  liabilitiesIcon?: string;
  /** Fallback palettes for slices without their own color. */
  assetColors?: string[];
  liabilityColors?: string[];
  /** Money formatter for slice + total amounts. */
  format?: (value: number, slice?: DonutSlice) => React.ReactNode;
  showAssets?: boolean;
  showLiabilities?: boolean;
  className?: string;
}

/** Asset / liability allocation pair — two recessed wells, each a stacked Donut. Collapses to one column under 760px. */
export declare function AllocationDonuts(props: AllocationDonutsProps): JSX.Element;
