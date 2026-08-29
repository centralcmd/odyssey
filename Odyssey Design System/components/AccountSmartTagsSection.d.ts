import * as React from 'react';

/** A tag as configured on the account (or a plain label string). */
export interface SmartTag {
  /** TransactionTag id. */
  id?: string;
  /** Display name; `name` is also accepted (mirrors the DTO). */
  label?: string;
  name?: string;
}

/** A selectable option in the add-tag checklist. */
export interface SmartTagOption {
  value?: string;
  id?: string;
  label?: string;
  name?: string;
}

export interface AccountSmartTagsSectionProps {
  /** Tags currently watched on this account. Strings or {id,label|name}. */
  tags?: Array<SmartTag | string>;
  /** Every selectable TransactionTag for the add/manage checklist. */
  tagOptions?: Array<SmartTagOption | string>;
  /** The already-filtered matching transactions (drives the header count + the
   *  NoTransactions vs HasTransactions split). The consumer filters by
   *  `account === id && txn.tags ∩ smartTagIds`. */
  transactions?: any[];
  /** Associate a tag (the `POST …/smart-tags/{tagId}` action). */
  onAddTag?: (tagId: string) => void;
  /** Remove an association (the `DELETE …/smart-tags/{tagId}` action). */
  onRemoveTag?: (tagId: string) => void;
  /** Gate every add/remove control. Read-only viewers still see chips + table. */
  canWrite?: boolean;
  /** Transactions are being (re)fetched — shows the progress state. */
  loading?: boolean;
  /** Inline error message (failed load). Shown with a Retry when `onRetry` set. */
  error?: string | null;
  onRetry?: () => void;
  /** Renders the transaction table for the matching set. Pass `<TxnTable
   *  hideAccount …/>`. Kept as a callback so the section stays decoupled from
   *  TxnTable's render contract. */
  renderTable?: (transactions: any[]) => React.ReactNode;
  /** Extracts the signed amount from a transaction for the net total. Default
   *  reads `t.amount` (income positive, expense negative). */
  amountOf?: (transaction: any) => number;
  /** Formats the net total figure shown on the bar. Default: a signed "$ x.xx".
   *  Pass a currency-aware formatter (e.g. the account's `signedMoney`). */
  formatAmount?: (total: number) => React.ReactNode;
  /** Soft cap on watched tags (v1 = 20). The adder blocks new checks at the cap. */
  maxTags?: number;
  /** Section title. Default "Smart tags". */
  title?: string;
  /** Leading Material Icons ligature. Default "sell". */
  icon?: string;
  /** Controlled open state (with `onToggle`); else uncontrolled via `defaultOpen`. */
  open?: boolean;
  defaultOpen?: boolean;
  onToggle?: (open: boolean) => void;
  className?: string;
}

/**
 * AccountSmartTagsSection — the per-account "Smart tags" disclosure shown in the
 * expanded account record, below the Transactions section. It pins a curated
 * set of existing TransactionTags to an account as a saved filter and surfaces
 * every transaction on that account carrying any of them.
 *
 * Self-contained: renders its own `.odc-collapsible` shell, a tag-management
 * bar (removable chips + an "Add tag" checklist popover whose check→add /
 * uncheck→remove maps to the individual smart-tag endpoints), the matching
 * count in the header, and the NoSmartTags / Loading / NoTransactions /
 * HasTransactions / error states. The OdsTxnTable is injected via
 * `renderTable(transactions)`. Maps to an OdsCollapsible + OdsTxnTable in the
 * Blazor `AccountSmartTagsSection`.
 */
export declare function AccountSmartTagsSection(props: AccountSmartTagsSectionProps): JSX.Element;
