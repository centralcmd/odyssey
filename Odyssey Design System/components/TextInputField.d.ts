import * as React from 'react';

export interface TextInputFieldProps {
  /** Visible label. Omit when an external element labels the input via `ariaLabelledBy`. */
  label?: string;
  /** Controlled string value. */
  value?: string;
  /** Fires with the next string value first, the native event second. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  placeholder?: string;
  /** Hard character ceiling on the input. */
  maxLength?: number;
  /** Show a live `n/max` counter in the label row's aside slot. Requires `maxLength`. */
  showCount?: boolean;
  inputMode?: 'text' | 'url' | 'email' | 'tel' | 'search' | 'numeric' | 'decimal';
  autoComplete?: string;
  spellCheck?: boolean;
  /** Helper text shown below the input. */
  help?: string;
  /** Error message — flips to the error state and adds a `role="alert"` line. */
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
  /** `id` of an external label element (e.g. a SettingRow title). Sets `aria-labelledby` on the input. */
  ariaLabelledBy?: string;
  /** `id` of an external description (e.g. a row description). APPENDED to the internal help/error id in `aria-describedby`, never replacing it. */
  ariaDescribedBy?: string;
}

/**
 * A labelled single-line text input on a native `<input type="text">` inside
 * FieldShell. Reach for it when the control must be labelled or described by
 * elements it does not own (a SettingRow title/description); use `Field` for
 * ordinary form entry and `SearchField` for filter boxes.
 */
export declare function TextInputField(props: TextInputFieldProps): JSX.Element;
