export interface DateFieldProps {
  /** Field label shown above the control. */
  label?: string;
  /** Selected date as an ISO `YYYY-MM-DD` string, or null/undefined when empty. */
  value?: string | null;
  /** Fires with the next ISO date string, or null when cleared. */
  onChange?: (iso: string | null) => void;
  /** Placeholder shown in the trigger when no date is chosen. */
  placeholder?: string;
  /** Earliest selectable date (ISO). Earlier days are disabled. */
  min?: string;
  /** Latest selectable date (ISO). Later days are disabled. */
  max?: string;
  /** Which edge the calendar popover anchors to. */
  align?: 'start' | 'end';
  /** Helper text below the control. Suppressed when `error` is set. */
  help?: string;
  /** Error message — flips the field to its error state and shows below it. */
  error?: string;
  /** Show the required `*` marker on the label. */
  required?: boolean;
  /** Show the muted "Optional" marker on the label. */
  optional?: boolean;
  /** Disable the trigger. */
  disabled?: boolean;
  /** Stretch the control to the container width. Default true. */
  full?: boolean;
  className?: string;
  /** Explicit id; auto-generated if omitted. Wires label ⇄ control ⇄ helper. */
  id?: string;
}

/**
 * Labelled single-date field — the calendar `DatePicker` wrapped in the shared
 * `FieldShell` label / optional-required / helper-error chrome, so dates read
 * identically to `Field`, `AmountField`, `NumberField` and `NoteField` in a form.
 * Value is an ISO `YYYY-MM-DD` string.
 */
export declare function DateField(props: DateFieldProps): JSX.Element;
