import * as React from 'react';

export interface SwitchProps {
  checked?: boolean;
  /** Fires with the next boolean state and the change event. */
  onChange?: (checked: boolean, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** Optional trailing label (also the click target). */
  label?: string;
  disabled?: boolean;
  id?: string;
}

/** Binary on/off toggle — instant settings (e.g. Dark mode). */
export declare function Switch(props: SwitchProps): JSX.Element;
