import * as React from 'react';
import { TypeOption, TypeGroup } from './TypeSelect';

export interface RegistrySelectProps {
  /** The registry to render — { key|value, label, icon, color } rows. */
  types: TypeOption[];
  /** Selected enum key. */
  value?: string;
  onChange?: (key: string, event: React.MouseEvent) => void;
  /** Optional ordered sections; options are grouped by their `group` field. */
  groups?: TypeGroup[];
  label?: string;
  placeholder?: string;
  help?: string;
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Shared engine behind every registry-backed single select — renders `TypeSelect`
 * (or the base `Select` as a fallback) from a registry. Domain wrappers
 * (AccountFileTypeSelect, InsurancePolicyTypeSelect, …) delegate to it.
 */
export declare function RegistrySelect(props: RegistrySelectProps): JSX.Element;
