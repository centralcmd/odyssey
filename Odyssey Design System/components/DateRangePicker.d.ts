export interface DateRange {
  /** Range start as an ISO `YYYY-MM-DD` string, or null/undefined when open-ended. */
  from?: string | null;
  /** Range end as an ISO `YYYY-MM-DD` string, or null/undefined when open-ended. */
  to?: string | null;
}

export interface DateRangePickerProps {
  /** The selected range. Either end may be null for an open-ended range. */
  value?: DateRange | null;
  /** Fires with the next `{ from, to }` whenever either end changes or the range is cleared. */
  onChange?: (range: DateRange) => void;
  /** Short uppercase caption shown before the fields (e.g. "Taken", "Due"). Omit for none. */
  label?: string;
  /** Leading material-icon name. Defaults to `event`; pass null to hide. */
  icon?: string | null;
  /** Placeholder for the start field. */
  fromPlaceholder?: string;
  /** Placeholder for the end field. */
  toPlaceholder?: string;
  /** Earliest selectable date (ISO) for both ends. */
  min?: string;
  /** Latest selectable date (ISO) for both ends. */
  max?: string;
  /**
   * Keep the range ordered: the start field caps at `to`, the end field floors
   * at `from`, so an invalid (crossed) range can't be picked. Default true.
   */
  clamp?: boolean;
  /** Which edge each calendar popover anchors to. */
  align?: 'start' | 'end';
  /** Accessible label for the group. Defaults to "Filter by date range". */
  ariaLabel?: string;
  /** Explicit id root; auto-generated if omitted. */
  id?: string;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A compact two-field date-range control: a leading icon + caption, a start
 * and end `DatePicker` joined by a dash, and a clear button that appears once
 * either end is set. Reads as a single pill-shaped input, sized to sit inline
 * in a filter/search bar next to Select and MultiSelect.
 */
export declare function DateRangePicker(props: DateRangePickerProps): JSX.Element;
