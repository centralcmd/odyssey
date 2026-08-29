import * as React from 'react';

export interface AccountStatusChipProps {
  /** Status word — Open / Closed / Archived. */
  label: string;
  /** Tone → dot color. income (open) · pending (closed) · outline (archived). */
  tone?: 'income' | 'pending' | 'error' | 'warning' | 'info' | 'outline' | 'neutral';
  /** Muted trailing date segment, e.g. "since Mar 14, 2021" / "on Mar 10, 2021". */
  detail?: string;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Read display of an account's status as a chip — tone-colored dot + label +
 * optional muted date segment. The status sibling of AccountTypeChip /
 * CustodianChip for the account detail metadata grid.
 */
export declare function AccountStatusChip(props: AccountStatusChipProps): JSX.Element;
