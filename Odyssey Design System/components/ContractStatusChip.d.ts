import * as React from 'react';

export type ContractStatusKey = 'Active' | 'Upcoming' | 'Expired' | 'Archived' | 'Paused' | 'Draft' | 'Ready';

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

/** Canonical contract-status vocabulary, in precedence order (Archived → Draft/Ready → Upcoming → Expired → Paused → Active). */
export declare const CONTRACT_STATES: ContractStateMeta[];

/**
 * Lifecycle reading order — Draft → Ready → Upcoming → Active → Paused →
 * Expired → Archived. Sort a status column on THIS, never on the enum ordinal:
 * Draft and Ready are appended members (5, 6) and would otherwise sort last.
 */
export declare const CONTRACT_STATUS_RANK: ContractStatusKey[];

/** Lifecycle rank of a member. An unrecognised member sorts last. */
export declare function contractStatusRank(status: string): number;

/** True for Draft and Ready — on file, not in force, excluded from every money roll-up. */
export declare function contractStatusIsUnsigned(status: string): boolean;

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
 * visible text. Paused replaces
 * Active only — a terminal status (Archived / Upcoming / Expired) always wins,
 * and the signature states (Draft / Ready) outrank the whole date chain.
 */
export declare function ContractStatusChip(props: ContractStatusChipProps): JSX.Element;
