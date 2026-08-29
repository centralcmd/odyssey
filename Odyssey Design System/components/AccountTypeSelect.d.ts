import * as React from 'react';

export interface AccountTypeDef {
  /** Enum key — e.g. 'CheckingAccount'. */
  key: string;
  label: string;
  /** 'asset' | 'liability' — drives the picker's two groups. */
  group: 'asset' | 'liability';
  /** Material Icons ligature. */
  icon: string;
  /** Fixed icon color (oklch foreground) and soft tinted background. */
  color: string;
  soft: string;
}

/** The canonical AccountType registry — assets first, then liabilities. */
export declare const ACCOUNT_TYPES: AccountTypeDef[];

export interface AccountTypeSelectProps {
  /** Selected AccountType key, or '' / undefined when empty. */
  value?: string;
  /** Fires with the next AccountType key. */
  onChange?: (key: string) => void;
  /** Field label. Default 'Account type'. */
  label?: React.ReactNode;
  placeholder?: string;
  /** Error message — tints the trigger and renders below. */
  error?: React.ReactNode;
  /** Helper line below the trigger (error wins). */
  help?: React.ReactNode;
  /** Restrict / reorder the offered types. Defaults to the full registry. */
  types?: AccountTypeDef[];
  disabled?: boolean;
}

/**
 * The Account-type picker: a select-sized trigger showing the chosen type's
 * colored glyph + label, opening a popover grouped into Assets and
 * Liabilities — exactly how the registry groups them. Value is the enum key.
 */
export declare function AccountTypeSelect(props: AccountTypeSelectProps): JSX.Element;
