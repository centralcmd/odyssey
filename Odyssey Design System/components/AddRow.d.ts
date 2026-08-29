import * as React from 'react';

export interface AddRowProps {
  /** The verb — e.g. "Add account", "Add budget". */
  title: React.ReactNode;
  /** Optional one-line explainer under the verb. */
  sub?: React.ReactNode;
  /** Material Icons ligature in the leading tile. Default 'add'. */
  icon?: string;
  onClick?: (e: React.MouseEvent) => void;
  /** Extra class on the row button. */
  className?: string;
}

/**
 * The persistent create affordance that closes every record list (Accounts,
 * Budgets, …): a full-width dashed row with a muted square icon tile, the verb,
 * and an optional one-line sub. Sits after the last item, before pagination.
 */
export declare function AddRow(props: AddRowProps): JSX.Element;
