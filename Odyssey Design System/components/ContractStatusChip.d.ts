import * as React from 'react';

export type ContractStatusKey = 'Active' | 'Upcoming' | 'Expired' | 'Archived' | 'Paused';

export interface ContractStateMeta {
  key: string;
  /** Visible label — the status meaning, conveyed as text (a11y). */
  label: string;
  tone: 'income' | 'info' | 'expense' | 'pending' | 'outline';
  dot: boolean;
  icon: string;
  /** True when the member is not in this client's vocabulary (a newer server enum). */
  unknown?: boolean;
}

/** Canonical contract-status vocabulary, in precedence order (Archived → Upcoming → Expired → Paused → Active). */
export declare const CONTRACT_STATES: ContractStateMeta[];

/**
 * Resolve a ContractStatus member to its display row. An unrecognised member
 * returns a NEUTRAL row carrying the member's own name — never Active.
 */
export declare function contractStatusMeta(status: string): ContractStateMeta;

export interface ContractStatusChipProps {
  /** The derived status from the API (`ContractListItem.status` / `ExistingContract.status`). */
  status?: ContractStatusKey | string;
  /** Lead with the glyph instead of the status dot. */
  showIcon?: boolean;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A contract's derived lifecycle status as ONE chip, meaning conveyed as
 * visible text. Contracts' sibling of SubscriptionStatusChip. Paused replaces
 * Active only — a terminal status (Archived / Upcoming / Expired) always wins.
 */
export declare function ContractStatusChip(props: ContractStatusChipProps): JSX.Element;
