import * as React from 'react';

export interface SettingRowProps {
  /** Leading Material Icons ligature, rendered in a tide-tinted tile. */
  icon: string;
  /** The setting's label. */
  title: React.ReactNode;
  /** One-line description under the label. */
  desc?: React.ReactNode;
  /** `id` for the description element — so a control can point `aria-describedby` at the row hint. */
  descId?: string;
  /** `id` for the title element — so a label-less control can point `aria-labelledby` at it. */
  titleId?: string;
  /** Coral-tint the icon tile for destructive settings. */
  danger?: boolean;
  /** Show the unsaved-change dot beside the title (not in the control column, so it survives a footer control). */
  dirty?: boolean;
  /**
   * Advisory message in an amber band below the row, plus a glyph beside the
   * title. `role="status"` — for a value that saved but looks wrong (a check the
   * server can only make heuristically). Never gates the primary action; a value
   * that cannot be accepted is an `error` on the control instead.
   */
  warning?: React.ReactNode;
  /**
   * Full-width tinted well below the row. Use it for a control whose width is
   * set by its content (a text input for a URL or a name) — the control column
   * is `flex: none` and doesn't wrap — and for a cross-field / round-trip error
   * that can't fit the column. Mutually exclusive with `children`.
   */
  footer?: React.ReactNode;
  /** Exactly one control, right-aligned — for fixed-width controls only (Switch, NumberField, CapacityField, Select, Button). Free-text inputs belong in `footer`. */
  children?: React.ReactNode;
}

/**
 * One setting as an outlined card row: icon + label + one-line description on
 * the left, exactly one control on the right. The scaffold behind every row on
 * the Preferences and System settings pages.
 */
export declare function SettingRow(props: SettingRowProps): JSX.Element;
