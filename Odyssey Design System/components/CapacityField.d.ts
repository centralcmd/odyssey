import * as React from 'react';

export interface CapacityFieldProps {
  /** The finite limit, or `null` when empty. Retained (not cleared) while `unlimited` is true. */
  value?: number | null;
  /** Fires with the parsed number (or `null` when cleared). */
  onValueChange?: (value: number | null) => void;
  /** True = no limit. The number input is disabled but its value is retained. */
  unlimited?: boolean;
  /** Fires when the "No limit" switch is toggled. */
  onUnlimitedChange?: (unlimited: boolean) => void;
  /** The setting's title — used to compose the switch's accessible name. */
  label?: string;
  /** `id` of the row title — labels the number input (`aria-labelledby`). */
  ariaLabelledBy?: string;
  /** `id` of the row description/hint — describes the number input (`aria-describedby`). */
  ariaDescribedBy?: string;
  /** Error message — shown on the number input, alongside (never instead of) the row hint. */
  error?: string;
  /** Minimum finite value. Default 1. */
  min?: number;
  /** Maximum finite value. Default 1,000,000. */
  max?: number;
  /** Disables both the input and the switch (e.g. the caller lacks the write claim). */
  disabled?: boolean;
  /**
   * `stacked` (default) — input over a "No limit" switch, for a `SettingRow`
   * control column. `inline` — one line for a `SettingField` frame: the value,
   * then a pill carrying the inverse action ("No limit" / "Set a limit") so the
   * pill never repeats the words already showing as the value.
   */
  variant?: 'stacked' | 'inline';
  className?: string;
}

/**
 * A capacity-limit control: a right-aligned numeric input paired with a
 * "No limit" switch. Either a finite number or explicitly unbounded. The
 * control behind the count caps on the System settings import/export groups.
 */
export declare function CapacityField(props: CapacityFieldProps): JSX.Element;
