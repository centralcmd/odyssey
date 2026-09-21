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
  /** Soft cap on watched tags (accounts v1 = 20; contracts = the
   *  `ContractMaxSmartTagsPerContract` setting, ceiling 50). The adder blocks
   *  new checks at the cap. */
  maxTags?: number;
  /** The record the watchlist hangs off, interpolated into the default empty
   *  copy. Default "account"; pass "contract" on the contract record. */
  subject?: string;
  /** Override the empty-state sentence. Needed where the noun substitution
   *  would misstate the match (a contract's smart tags are not contract-scoped). */
  emptyDesc?: React.ReactNode;
  /** Override the no-matching-transactions sentence. */
  noMatchDesc?: React.ReactNode;
  /** The limits read is degraded (`/api/contract-limits` → 503). No number to
   *  pre-check against, so the adder stays open, says the limit is unavailable,
   *  and leaves the refusal to the server's conservative bound. */
  limitsDegraded?: boolean;
  /** A refused add, in the server's words — 422 at the cap (which names the
   *  effective number), 422 archived tag, 409 already linked. Rendered on the
   *  bar, not in the popover, which closes on the click that caused it. */
  addError?: string | null;
  onDismissAddError?: () => void;
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
 * AccountSmartTagsSection — the "Smart tags" watchlist shown in an expanded
 * record. Two hosts: the account record (`subject="account"`) and the contract
 * record (`subject="contract"`), which reads the same saved filter against the
 * `ContractMaxSmartTagsPerContract` cap. It pins a curated
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
