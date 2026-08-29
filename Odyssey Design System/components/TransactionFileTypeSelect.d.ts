import * as React from 'react';

export interface TransactionFileType {
  /** Enum key — matches the C# TransactionFileType member and the stored value. */
  key: string;
  label: string;
  /** Numeric enum value. */
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
}

/**
 * Canonical TransactionFileType registry — name · icon · color · soft tint ·
 * enumValue, in enum order with `Other` (the default) last. The consumable layer's
 * source of truth for transaction file types; mirrors the C# enum and
 * `OdysseyData.transactionFileTypes`.
 */
export declare const TRANSACTION_FILE_TYPES: TransactionFileType[];

export interface TransactionFileTypeSelectProps {
  /** Selected enum key (e.g. "Receipt"). */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  label?: string;
  placeholder?: string;
  types?: TransactionFileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the TransactionFileType vocabulary (files attached to
 * a transaction) — each option carries its Material icon in its category color. A
 * typed wrapper over `Select`.
 */
export declare function TransactionFileTypeSelect(props: TransactionFileTypeSelectProps): JSX.Element;
