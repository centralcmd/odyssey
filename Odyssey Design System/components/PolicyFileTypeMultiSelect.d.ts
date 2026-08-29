import * as React from 'react';

export interface PolicyFileTypeMultiSelectProps {
  /** Selected enum keys. */
  value?: string[];
  /** Fires with the next array of selected keys. */
  onChange?: (values: string[]) => void;
  /** Trigger label when nothing is selected. Defaults to "Any type". */
  label?: string;
  /** Trigger glyph. Defaults to "shield". */
  icon?: string;
  /** Popover alignment. */
  align?: 'left' | 'right';
  /** Override / subset the registry. */
  types?: Array<{ key: string; label: string; icon: string; color: string }>;
  className?: string;
}

/**
 * Multi-select filter pre-wired to the PolicyFileType vocabulary — each row
 * carries its Material icon in its category color. A typed wrapper over
 * `MultiSelect`.
 */
export declare function PolicyFileTypeMultiSelect(props: PolicyFileTypeMultiSelectProps): JSX.Element;
