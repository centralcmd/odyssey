export interface NumberFieldProps {
  /** Visible label. */
  label?: string;
  /** Controlled numeric value, or `null`/`undefined` when empty. */
  value?: number | null;
  /** Fires with the parsed number (or `null` when cleared) first, the native event second. */
  onChange?: (value: number | null, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** Placeholder shown when empty. Default "—". */
  placeholder?: string;
  min?: number;
  max?: number;
  step?: number;
  /** Helper text shown below the input. */
  help?: string;
  /** Error message — flips to the error state and replaces the helper. */
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  /** Text alignment of the value. Default "left". */
  align?: 'left' | 'right';
  /**
   * Static unit rendered inside the input's trailing edge (`%`, `MB`, `days`)
   * and appended to `aria-describedby`. Use instead of a helper line whenever
   * the number is meaningless without its unit. For a stored 0.0–1.0 fraction
   * use `min={0} max={100} step={1} unit="%"` and scale at the page boundary.
   */
  unit?: string;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
  /** `id` of an external label element (e.g. a settings-row title). Sets `aria-labelledby` on the input. */
  ariaLabelledBy?: string;
  /** `id` of an external description (e.g. a row-owned hint). APPENDED to the internal help/error id in `aria-describedby`, never replacing it. */
  ariaDescribedBy?: string;
}

/** A labelled numeric input (native type="number") that emits a number or null — for counts, years and figures. */
export declare function NumberField(props: NumberFieldProps): JSX.Element;
