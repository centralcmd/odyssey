import * as React from 'react';

export interface RadioOption {
  value: string;
  label: string;
  disabled?: boolean;
}

export interface RadioGroupProps {
  /** Shared input name. Auto-generated when omitted. */
  name?: string;
  value?: string;
  /** Fires with the chosen value and the change event. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** Options as {value,label} objects or plain strings. */
  options: Array<RadioOption | string>;
  /** Lay options out horizontally instead of stacked. */
  row?: boolean;
  disabled?: boolean;
  /** Visible group label (preferred) — renders the standard field label,
   *  wired to the group via aria-labelledby. */
  label?: string;
  /** Accessible name for the group when the surrounding UI already labels it
   *  visually. Prefer `label`. */
  ariaLabel?: string;
}

/** Single choice from a small mutually-exclusive set. */
export declare function RadioGroup(props: RadioGroupProps): JSX.Element;
