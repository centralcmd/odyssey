export interface FieldProps {
  /** Visible label, rendered above the control and tied to it via htmlFor. */
  label?: string;
  value?: string;
  /** Fires with the next string value first, the native event second. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  placeholder?: string;
  /** Native input type (text, email, password, number, search…). */
  type?: string;
  /** Render a multi-line <textarea> instead of a single-line input — for long values like descriptions and notes. */
  multiline?: boolean;
  /** Visible rows when `multiline` (the textarea stays vertically resizable). Default 3. */
  rows?: number;
  /** Leading Material Icons ligature name. */
  icon?: string;
  /** Helper text shown below the input. */
  help?: string;
  /** Error message — flips the field to its error state and replaces the helper. */
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label (the canonical optional marker). Mutually exclusive with `required`. */
  optional?: boolean;
  disabled?: boolean;
  /** Show a clear (×) button when there's a value — for search fields. Calls onChange(''). */
  clearable?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
}

/** A labelled text input with helper/error states. */
export declare function Field(props: FieldProps): JSX.Element;
