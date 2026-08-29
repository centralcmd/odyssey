/* =============================================================
   ChangePasswordRequired.jsx — the admin forced-reset gate
   (/change-password-required).

   Where a user lands when they sign in with their old password AFTER an admin
   triggered "Send password reset" on the /users page (spec §3, target side).
   Rendered under the nav-less blocking OnboardingLayout — no drawer, no module
   rail, no way into the app — so a forced credential change outranks everything
   else. Completing it clears the server MustChangePassword flag; the API keeps
   the block until it does (this client gate is presentation only).

   Reuses the OnboardingLayout shell chrome (onboarding.css .onb-*) and the
   shared DS PasswordChangeForm — the identical triad + live rules + error
   banner as /account. A user who does NOT know their current password can
   Sign out and use Forgot password instead; both escape hatches are present
   and keyboard-reachable without passing through the password fields.

   a11y: the <h1> carries tabindex="-1" and takes focus exactly once after the
   first render (a non-colliding ref guard, never a bool defaulting to false),
   so the involuntary redirect is announced rather than silent.

   `outcome` ('success' | 'wrong' | 'policy') drives the specimen states.
   ============================================================= */

function ChangePasswordRequired({ onDone, onLogout, onGoForgot, outcome = 'success' }) {
  const { useState, useRef, useEffect } = React;
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const headingRef = useRef(null);
  const focusedOnce = useRef(false); // focus-once guard — set only after first render fires

  useEffect(() => {
    if (!focusedOnce.current && headingRef.current) {
      headingRef.current.focus();
      focusedOnce.current = true;
    }
  }, []);

  const submit = ({ currentPassword, newPassword }) => {
    setError(null);
    setBusy(true);
    setTimeout(() => {
      setBusy(false);
      if (outcome === 'wrong') { setError('That current password is incorrect. Please try again.'); return; }
      if (outcome === 'policy') { setError('That password doesn\u2019t meet the requirements. Please choose another.'); return; }
      onDone && onDone();
    }, 550);
  };

  return (
    <div className="onb-shell">
      <div className="onb-card" style={{ maxWidth: 520 }}>
        <div className="onb-brand"><BrandMark size={68} /></div>

        <div className="onb-head">
          <h1 ref={headingRef} tabIndex={-1} className="onb-title" style={{ outline: 'none' }}>
            Choose a new password
          </h1>
          <p className="onb-sub">An administrator asked you to set a new password before continuing.</p>
        </div>

        <PasswordChangeForm
          onSubmit={submit}
          error={error}
          busy={busy}
          columns={2}
          autoFocus
          submitLabel="Set new password"
          busyLabel="Setting…"
          submitIcon="lock_reset"
        />

        <div className="cpr-alt">
          <MIcon name="help_outline" size={15} />
          <span>Don’t know your current password?{' '}
            <a onClick={onGoForgot} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Use Forgot password</a>{' '}
            instead.
          </span>
        </div>

        <div className="onb-actions" style={{ justifyContent: 'flex-end' }}>
          <Button variant="text" icon="logout" onClick={onLogout}>Sign out</Button>
        </div>

        <div className="onb-foot">
          <MIcon name="lock" size={13} />
          <span>You’ve been signed out of your other devices. Your current password keeps working until you set a new one.</span>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { ChangePasswordRequired });
