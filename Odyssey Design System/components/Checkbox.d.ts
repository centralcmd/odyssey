import * as React from 'react';

export interface CheckboxProps {
  checked?: boolean;
  /** Mixed state for "select all" headers (shown when not fully checked). */
  indeterminate?: boolean;
  /** Fires with the next boolean state and the change event. */
  onChange?: (checked: boolean, event: React.ChangeEvent<HTMLInputElement>) => void;
  label?: string;
  disabled?: boolean;
  id?: string;
}

/** Multi-select boolean with an indeterminate state — row select, batch grids. */
export declare function Checkbox(props: CheckboxProps): JSX.Element;
