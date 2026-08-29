import * as React from 'react';

export interface SecretClearDialogProps {
  /** Controls visibility. Default true. */
  open?: boolean;
  /** The credential's label, used in the title: "Clear {label}?". */
  label: React.ReactNode;
  /** The settings key (e.g. `Email:Password`), shown as a mono chip under the title. */
  secretKey?: string;
  /**
   * Selects the copy variant. `derivation` adds the coral callout stating that
   * the key cannot be re-issued and that anything already derived with it can
   * never be re-derived.
   */
  kind?: 'credential' | 'derivation';
  /** One sentence naming what stops working (e.g. "Transactional mail stops sending until a new password is entered."). */
  affects?: string;
  /** The row being cleared is already `unreadable` — the copy then says clearing breaks nothing currently working. */
  unreadable?: boolean;
  /** Label on the destructive button. Default "Clear value". */
  confirmLabel?: string;
  busy?: boolean;
  onCancel?: () => void;
  onConfirm?: () => void;
  /** Extra per-secret detail, rendered above the derivation-key callout. */
  children?: React.ReactNode;
}

/**
 * Confirmation for clearing a stored secret, in two copy variants: a rotatable
 * credential (recoverable — re-issue it at the provider) and a derivation key
 * (permanent — nothing already derived with it can be re-derived). One confirm
 * button; a value that cannot be read back cannot be re-typed to prove intent.
 */
export declare function SecretClearDialog(props: SecretClearDialogProps): JSX.Element;
