export interface PasswordChangeSubmit {
  /** The current password the user typed. */
  currentPassword: string;
  /** The chosen new password (already satisfies PASSWORD_POLICY when this fires). */
  newPassword: string;
}

export interface PasswordChangeFormProps {
  /** Called once the form is valid and submitted. The host owns the request + outcome. */
  onSubmit?: (change: PasswordChangeSubmit) => void;
  /** Failure banner text — rendered as an Alert severity="error" (role="alert"). */
  error?: string | null;
  /** Disables the fields + submit and shows the busy label while a request is in flight. */
  busy?: boolean;
  /** Submit button label. Default "Update password". */
  submitLabel?: string;
  /** Label shown while `busy`. Default "Updating…". */
  busyLabel?: string;
  /** Submit button leading Material icon. Default "lock_reset". */
  submitIcon?: string;
  /** PasswordRules checklist layout — 1 (stacked, auth card) or 2 (grid, wide form). */
  columns?: 1 | 2;
  /** Focus the current-password field on mount. */
  autoFocus?: boolean;
  className?: string;
}

/**
 * The shared current → new → confirm password triad + live PasswordRules
 * checklist + error banner + submit. Consumed by both /account's change-password
 * section and the admin forced-reset gate so the two never drift. Self-manages
 * field state; the host owns the request outcome (`error` / `busy`). Remount with
 * a changed `key` to clear it after a success.
 */
export declare function PasswordChangeForm(props: PasswordChangeFormProps): JSX.Element;
