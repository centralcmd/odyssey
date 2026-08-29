import * as React from 'react';

export interface TimeFieldProps {
  /** Visible field label. */
  label?: string;
  /** Selected time as a 24-hour `HH:mm` string, or null/empty when unset. */
  value?: string | null;
  /** Fires with the next `HH:mm` string, or null when cleared. */
  onChange?: (value: string | null) => void;
  placeholder?: string;
  /** Minute granularity of the suggestion list. Default 30. */
  step?: number;
  help?: React.ReactNode;
  error?: React.ReactNode;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  /** Stretch to the container width. Default true. */
  full?: boolean;
  className?: string;
  id?: string;
}

/** Labelled time-of-day entry — the timed sibling of `DateField`. Binds a
 *  24-hour `HH:mm` string (`OdsDateField`/`OdsDatePicker` only carry date
 *  granularity, so timed calendar events need this). Type a time or pick from
 *  the step-interval suggestion list; the value is normalised on blur/commit.
 *  Wrapped in the shared `FieldShell` so it reads identically to every other
 *  labelled control. */
export declare function TimeField(props: TimeFieldProps): JSX.Element;
