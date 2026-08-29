import * as React from 'react';
import { TypeOption } from './TypeSelect';

export interface RegistryMultiSelectProps {
  /** The registry to render — { key, label, icon, color } rows. */
  types: TypeOption[];
  value?: string[];
  onChange?: (values: string[]) => void;
  label?: string;
  /** Leading Material Icons ligature on the trigger. */
  icon?: string;
  align?: 'start' | 'end';
}

/**
 * Shared engine behind every registry-backed checkbox-list filter — maps a
 * registry to `MultiSelect` options. Domain wrappers (AccountFileTypeMultiSelect,
 * ContactTypeMultiSelect, …) delegate to it.
 */
export declare function RegistryMultiSelect(props: RegistryMultiSelectProps): JSX.Element;
