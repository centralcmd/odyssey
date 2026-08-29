export interface DatePickerProps {
  /** Selected date as an ISO `YYYY-MM-DD` string, or null/undefined when empty. */
  value?: string | null;
  /** Fires with the next ISO date string, or null when cleared. */
  onChange?: (iso: string | null) => void;
  placeholder?: string;
  disabled?: boolean;
  /** Earliest selectable date (ISO). Earlier days are disabled. */
  min?: string;
  /** Latest selectable date (ISO). Later days are disabled. */
  max?: string;
  /** Which edge the popover anchors to. */
  align?: 'start' | 'end';
  /** Stretch the trigger to the container width. */
  full?: boolean;
  /** Explicit id; auto-generated if omitted. */
  id?: string;
}

/** Single-date calendar popover. Value is an ISO YYYY-MM-DD string. */
export declare function DatePicker(props: DatePickerProps): JSX.Element;
