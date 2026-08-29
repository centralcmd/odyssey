/**
 * Odyssey DS — PasswordChangeForm
 * The single, shared current → new → confirm password triad. One component so
 * that BOTH the self-service /account "Change password" section AND the admin
 * forced-reset gate (/change-password-required) render the identical fields,
 * the same live PasswordRules checklist, one submit button, and one failure
 * banner — the two can never drift, and the accessibility properties below are
 * pinned here rather than re-implemented per surface.
 *
 * Self-manages the three field values; calls `onSubmit({ currentPassword,
 * newPassword })` only once the current field is filled, every PASSWORD_POLICY
 * rule is met, the two new entries match, and the new password differs from the
 * current one. The host owns the outcome: pass `error` for the failure banner
 * and `busy` to disable + show the busy label while the request is in flight.
 * Remount with a changed `key` to clear the fields after a success.
 *
 * a11y, pinned here (WCAG 1.3.5 Identify Input Purpose / 4.1.3 Status Messages):
 *   • autocomplete="current-password" on the current field; "new-password" on
 *     BOTH the new and confirm fields — a password manager helps most on the
 *     involuntary-redirect gate, not less.
 *   • the failure banner is an Alert severity="error" (role="alert"), never a
 *     hand-written politely-announced div.
 *
 * Composes the DS Field / PasswordRules / Alert / Button read off the namespace
 * at render. Styled by .odc-pw-form in components.css.
 */
export function PasswordChangeForm({
  onSubmit,
  error,
  busy = false,
  submitLabel = 'Update password',
  busyLabel = 'Updating…',
  submitIcon = 'lock_reset',
  columns = 1,
  autoFocus = false,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Field = NS.Field;
  const Alert = NS.Alert;
  const Button = NS.Button;
  const PasswordRules = NS.PasswordRules;
  const POLICY = NS.PASSWORD_POLICY;

  const [cur, setCur] = React.useState('');
  const [next, setNext] = React.useState('');
  const [confirm, setConfirm] = React.useState('');

  const allMet = POLICY ? POLICY.isSatisfied(next) : false;
  const matches = confirm.length > 0 && next === confirm;
  const sameAsOld = next.length > 0 && next === cur;
  const canSubmit = cur.length > 0 && allMet && matches && !sameAsOld && !busy;

  const submit = (e) => {
    if (e && e.preventDefault) e.preventDefault();
    if (!canSubmit) return;
    onSubmit && onSubmit({ currentPassword: cur, newPassword: next });
  };

  if (!Field || !Button || !PasswordRules) return null;

  return (
    <form className={`odc-pw-form${className ? ' ' + className : ''}`} onSubmit={submit}>
      <Field label="Current password" type="password" value={cur} onChange={setCur}
        placeholder="••••••••" autoComplete="current-password" autoFocus={autoFocus} required />
      <div className="odc-pw-form-sep" aria-hidden="true" />
      <Field label="New password" type="password" value={next} onChange={setNext}
        placeholder="••••••••" autoComplete="new-password" required
        error={allMet && sameAsOld ? 'Choose a password different from your current one.' : ''} />
      <PasswordRules password={next} columns={columns} />
      <Field label="Confirm new password" type="password" value={confirm} onChange={setConfirm}
        placeholder="••••••••" autoComplete="new-password" required
        error={confirm.length > 0 && !matches ? 'Passwords do not match.' : ''}
        help={matches && !sameAsOld ? 'Passwords match.' : ''} />
      {error && Alert ? <Alert severity="error">{error}</Alert> : null}
      <div className="odc-pw-form-actions">
        <Button variant="filled" color="primary" icon={submitIcon} type="submit"
          disabled={!canSubmit} loading={busy} onClick={submit}>
          {busy ? busyLabel : submitLabel}
        </Button>
      </div>
    </form>
  );
}
