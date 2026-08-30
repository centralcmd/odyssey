import * as React from 'react';

export interface SecretClearOnSaveDialogProps {
  /** Controls visibility. Default true. */
  open?: boolean;
  /**
   * Which change triggered the clear, and therefore which copy variant is used.
   * `host` — the credential would otherwise reach a relay it was not entered for.
   * `starttls` — the credential would otherwise go over an unencrypted connection.
   */
  reason?: 'host' | 'starttls';
  /** The host being moved away from (host variant) or losing encryption (starttls variant). */
  fromHost?: string;
  /** The host being moved to. Host variant only. */
  toHost?: string;
  /** Labels of the secrets this save clears, e.g. ['SMTP username', 'SMTP password']. */
  secrets?: string[];
  /** Where the credential is re-entered afterwards. Default 'Credentials'. */
  reEnterAt?: string;
  /** Pending edits on the page — named on the confirm button, because this gates a whole-page batch save. */
  pendingCount?: number;
  /** Override the confirm label. */
  confirmLabel?: string;
  busy?: boolean;
  onCancel?: () => void;
  onConfirm?: () => void;
}

/**
 * Confirmation in front of a page SAVE that clears a stored secret as a side
 * effect of changing another value. Unlike `SecretClearDialog` (an immediate,
 * user-asked, single-field clear) this gates a whole-page batch save, so the
 * copy states that Confirm submits every pending edit and Cancel discards none.
 */
export declare function SecretClearOnSaveDialog(props: SecretClearOnSaveDialogProps): JSX.Element;
