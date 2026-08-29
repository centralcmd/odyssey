import * as React from 'react';
import { TaxStatementFileType } from './TaxStatementFileTypeSelect';

export interface TaxStatementFileTypeMultiSelectProps {
  /** Selected enum keys. */
  value?: string[];
  onChange?: (value: string[]) => void;
  /** Trigger label when nothing is selected. Default "Any type". */
  label?: string;
  /** Trigger glyph. Default "request_quote". */
  icon?: string;
  align?: 'left' | 'right';
  /** Override / subset the registry. */
  types?: TaxStatementFileType[];
  className?: string;
}

/**
 * Checkbox-list filter pre-wired to the TaxStatementFileType vocabulary — each
 * row carries its Material icon in its category color. A typed wrapper over
 * `MultiSelect`.
 */
export declare function TaxStatementFileTypeMultiSelect(props: TaxStatementFileTypeMultiSelectProps): JSX.Element;
