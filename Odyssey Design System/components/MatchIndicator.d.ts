import * as React from 'react';

export type MatchState = 'ai' | 'created' | 'manual' | 'none' | 'suggestion';

export interface MatchIndicatorProps {
  /**
   * Where the cell's value came from:
   * - 'ai'         auto-linked LLM suggestion (≥ auto-link threshold)
   * - 'created'    contact created inline by the reviewer
   * - 'manual'     reviewer picked / applied it
   * - 'none'       no match, or cleared
   * - 'suggestion' sub-threshold match — status row + inline Use / dismiss action
   */
  state?: MatchState;
  /** Match confidence 0–1. Renders as a tabular % + an accessible band word. */
  confidence?: number | null;
  /** Suggested record name — required for `state="suggestion"` (shown in the Use action). */
  name?: string;
  /** Label for the Use action (suggestion state). Default "Use". */
  applyLabel?: string;
  /** Apply (Use) the sub-threshold suggestion (suggestion state). Keyboard-operable. */
  onApply?: () => void;
  /** Dismiss the sub-threshold suggestion (suggestion state). */
  onDismiss?: () => void;
  /**
   * 'none' only — the extracted-but-unmatched string to offer creating as a new
   * record (e.g. the raw merchant "Nopa"). With `onCreate`, renders a
   * Create "…" action after "No match" instead of a dead-end.
   */
  createName?: string;
  /** Label for the create action. Default "Create". */
  createLabel?: string;
  /** Create the `createName` value as a new record (none state). Keyboard-operable. */
  onCreate?: () => void;
  size?: 'sm';
  className?: string;
  id?: string;
}

/** Per-cell AI match annotation — source + confidence as text (never colour alone). */
export declare function MatchIndicator(props: MatchIndicatorProps): JSX.Element;
