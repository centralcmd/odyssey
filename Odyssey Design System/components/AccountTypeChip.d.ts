import * as React from 'react';

export interface AccountTypeMeta {
  key: string;
  label: string;
  group?: 'asset' | 'liability';
  icon: string;
  color: string;
  soft?: string;
}

/** Resolve an AccountType key to its registry meta (icon · color · label), or null. */
export declare function accountTypeMeta(typeKey?: string): AccountTypeMeta | null;

export interface AccountTypeChipProps {
  /** AccountType enum key (e.g. "CheckingAccount"). */
  type?: string;
  /** A pre-resolved registry object (skips the lookup). */
  accountType?: AccountTypeMeta;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  /** Show the trailing Asset / Liability group segment. Default true. */
  showGroup?: boolean;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Read display of an account's type as a chip — colored type glyph + label,
 * drawn from the ACCOUNT_TYPES registry. The sibling of CustodianChip for the
 * account detail metadata grid.
 */
export declare function AccountTypeChip(props: AccountTypeChipProps): JSX.Element;
