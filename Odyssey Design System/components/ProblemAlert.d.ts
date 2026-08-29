import * as React from 'react';

export interface ProblemAlertProps {
  /** warning (amber) · error (coral) · info (sea). Default 'warning'. */
  severity?: 'warning' | 'error' | 'info';
  title?: React.ReactNode;
  detail?: React.ReactNode;
  /** Fix CTA label, pinned top-right. Omit for no action. */
  actionLabel?: React.ReactNode;
  /** Trailing icon on the CTA. Default 'arrow_forward'. */
  actionIcon?: string;
  onAction?: (e: React.MouseEvent) => void;
  className?: string;
  children?: React.ReactNode;
}

/**
 * The expanded-detail surface of the problem/signal pattern (Accounts, Tax
 * Statements): a severity-tinted block with a title, optional navigate-to-fix
 * CTA, and a detail paragraph. DS-tab card: components/problemalert.html.
 */
export declare function ProblemAlert(props: ProblemAlertProps): JSX.Element;
