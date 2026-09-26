import * as React from 'react';
import { PropertyFileType } from './PropertyFileTypeSelect';

export interface PropertyFileTypeMultiSelectProps {
  /** Selected enum keys. */
  value?: string[];
  onChange?: (value: string[]) => void;
  /** Trigger label when nothing is selected. Default "Any type". */
  label?: string;
  /** Trigger glyph. Default "home_work". */
  icon?: string;
  align?: 'left' | 'right';
  /** Override / subset the registry. */
  types?: PropertyFileType[];
  className?: string;
}

/**
 * Checkbox-list filter pre-wired to the PropertyFileType vocabulary — each row
 * carries its Material icon in its category color. A typed wrapper over
 * `MultiSelect`.
 */
export declare function PropertyFileTypeMultiSelect(props: PropertyFileTypeMultiSelectProps): JSX.Element;
