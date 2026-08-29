import * as React from 'react';
import { ActionMenuItem } from './ActionMenu';

/** One denormalized transaction row — plain data, no store lookups. */
export interface TxnTableRow {
  /** Stable id — row identity, Copy-ID target, onSave/onDelete key. */
  id: string;
  /** Description, e.g. "Spotify · Monthly". */
  desc: string;
  /** TransactionStatus enum value — keys `statusTones`. */
  status: string;
  /** Signed amount: income ≥ 0, expense < 0. */
  amount: number;
  /** ISO date (YYYY-MM-DD). */
  date: string;
  /** Money direction. Defaults from the sign of `amount`. */
  dir?: 'income' | 'expense';
  /** Leading-avatar Material icon. Defaults per `dir`. */
  icon?: string;
  /** ISO currency for the default amount formatter. Default "USD". */
  currency?: string;
  /** Contact display name. Defaults to the leading "·" segment of `desc`. */
  contact?: string;
  /** Owning account name, e.g. "Everyday Checking". */
  accountLabel?: string;
  /** Masked account number, e.g. "··4521". */
  accountNumber?: string;
  /** Tag display name — rendered as the tag chip when present. */
  tagLabel?: string;
}

/** Context passed to `actions(txn, ctx)`. */
export interface TxnActionContext {
  expanded: boolean;
  editing: boolean;
  /** Expand / collapse this row. */
  toggle: () => void;
  /** Open this row and switch it into edit mode. */
  startEdit: () => void;
  /** Delete this row (clears its open/edit state, then calls `onDelete`). */
  remove: () => void;
}

/** Context passed to `renderEdit(txn, ctx)`. */
export interface TxnEditContext {
  /** Commit a patch — calls `onSave(id, patch)`, exits edit, flashes "Saved". */
  save: (patch: any) => void;
  /** Leave edit mode without saving (row stays expanded on its detail view). */
  cancel: () => void;
}

export interface TxnTableProps {
  /** Rows to render (already filtered by the parent). No pagination — the list renders whole. */
  txns: TxnTableRow[];
  /** Drop the Account column (redundant inside one account). */
  hideAccount?: boolean;
  /** Status → Chip tone. Default { New:'info', Approved:'income', Flagged:'expense' }. */
  statusTones?: Record<string, string>;
  /** Amount cell renderer. Default: signed Intl currency via `txn.currency`. */
  formatAmount?: (txn: TxnTableRow) => React.ReactNode;
  /** Date cell renderer. Default: "Apr 12, 2026". */
  formatDate?: (iso: string) => React.ReactNode;
  /** Initial sort. Default { key:'date', dir:'desc' }. */
  defaultSort?: { key: 'desc' | 'contact' | 'account' | 'tag' | 'status' | 'amount' | 'date'; dir: 'asc' | 'desc' };
  /** Replace the row overflow menu. Default: View / Edit / Copy transaction ID / Delete. */
  actions?: (txn: TxnTableRow, ctx: TxnActionContext) => ActionMenuItem[];
  /** Read-only panel shown when a row is expanded. Omit for non-expanding rows. */
  renderDetail?: (txn: TxnTableRow, ctx: { expanded: boolean }) => React.ReactNode;
  /** Edit panel shown when a row is in edit mode. Omit for read-only tables. */
  renderEdit?: (txn: TxnTableRow, ctx: TxnEditContext) => React.ReactNode;
  /** Persist a row edit. */
  onSave?: (id: string, patch: any) => void;
  /** Remove a row. */
  onDelete?: (id: string) => void;
  /** How long the "Saved" flash stays up (ms). Default 2200. */
  savedFlashMs?: number;
  /** Full-width content shown when `txns` is empty (e.g. an <EmptyState>). */
  empty?: React.ReactNode;
  /** Extra class on the `<table>`. */
  className?: string;
}

/** The transactions ledger — sortable, expandable, editable; the one transaction-row surface. */
export declare function TxnTable(props: TxnTableProps): JSX.Element;
