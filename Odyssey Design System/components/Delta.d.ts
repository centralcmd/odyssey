import * as React from 'react';

export interface DeltaProps {
  /** The number to render. `null`/`undefined` → the `naLabel`. */
  value: number | null | undefined;
  /** Formats the magnitude (sign + glyph are added by the component). */
  format?: (n: number) => React.ReactNode;
  /**
   * variance — 0 reconciled (mint ✓), non-zero discrepancy (amber), null disabled.
   * directional — ↑/↓/– arrow + magnitude (mint/coral, or muted with `neutral`).
   * signed — +/− + magnitude (mint up / coral down). Default 'signed'.
   */
  mode?: 'variance' | 'directional' | 'signed';
  /** directional: mute the color when direction isn't good-or-bad (e.g. rates). */
  neutral?: boolean;
  /** variance: how to render the reconciled 0 (default `format(0)`). */
  zeroFormat?: () => React.ReactNode;
  /** Text shown when `value` is null. Default "Unavailable". */
  naLabel?: React.ReactNode;
  /** Trailing context, e.g. "vs 2024" or "all-time". */
  suffix?: React.ReactNode;
  className?: string;
}

/**
 * Unifies the product's change/difference encodings: reconciliation variance,
 * period-over-period directional change, and signed amounts. DS-tab card:
 * components/delta.html.
 */
export declare function Delta(props: DeltaProps): JSX.Element;
