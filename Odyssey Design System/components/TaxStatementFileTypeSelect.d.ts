import * as React from 'react';

export interface TaxStatementFileType {
  /** Enum key — matches the C# TaxStatementFileType member and the stored value. */
  key: string;
  label: string;
  /** Numeric enum value. */
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
}

/**
 * Canonical TaxStatementFileType registry — name · icon · color · soft tint ·
 * enumValue, in enum order with `Other` (the default) last. The consumable
 * layer's source of truth for tax-statement file types; mirrors the C# enum and
 * `OdysseyData.taxStatementFileTypes`.
 */
export declare const TAX_STATEMENT_FILE_TYPES: TaxStatementFileType[];

export interface TaxStatementFileTypeSelectProps {
  /** Selected enum key (e.g. "TaxReturn"). */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  label?: string;
  placeholder?: string;
  types?: TaxStatementFileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the TaxStatementFileType vocabulary (files attached
 * to a tax statement) — each option carries its Material icon in its category
 * color. A typed wrapper over `Select`.
 */
export declare function TaxStatementFileTypeSelect(props: TaxStatementFileTypeSelectProps): JSX.Element;
