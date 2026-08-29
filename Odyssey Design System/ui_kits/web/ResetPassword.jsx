/* ResetPassword — the /reset-password?code=<token> page (mirrors
   Pages/Auth/ResetPassword.razor).

   Anonymous, AuthLayout — the same centered 420px card. Four phases:

     invalidLink — reached when `code` is absent/empty. No form, no request.
     form        — Email (NOT pre-filled from the URL) + New password + the
                   shared OdsPasswordRules checklist + Confirm. Submit is
                   disabled until all five rules pass and the two entries match.
     done        — success; "will be signed out shortly" (the 30-min security-
                   stamp interval means other sessions end soon, not instantly).
     failed      — the token was rejected (invalid / expired / used / email
                   mismatch). A password-policy rejection instead keeps the user
                   in `form` with the message inline.

   The email is deliberately kept out of the URL and retyped here; the live token
   is scrubbed from the address bar on mount (represented by `code` being read
   into state). Specimen props: startPhase, initialError ('policy' | 'rate'). */

const ResetPassword = ({ onGoLogin, onGoForgot, startPhase = 'form', initialError = null }) => {
  const { useState, useRef, useEffect } = React;
  const [phase, setPhase] = useState(startPhase);
  const [email, setEmail] = useState('');
  const [pw, setPw] = useState('');
  const [confirm, setConfirm] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(
    initialError === 'policy' ? 'That password doesn\'t meet the requirements. Please choose another.'
      : initialError === 'rate' ? 'Too many attempts. Please wait a few minutes and try again.'
      : null
  );
  const panelHeadingRef = useRef(null);

  const rulesOk = PASSWORD_POLICY.isSatisfied(pw);
  const matches = confirm.length > 0 && pw === confirm;
  const canSubmit = email.trim() && rulesOk && matches;

  useEffect(() => {
    if ((phase === 'done' || phase === 'failed' || phase === 'invalidLink') && panelHeadingRef.current) {
      panelHeadingRef.current.focus();
    }
  }, [phase]);

  const submit = (e) => {
    e && e.preventDefault();
    if (!canSubmit) return;
    setError(null);
    setSubmitting(true);
    setTimeout(() => { setSubmitting(false); setPhase('done'); }, 400);
  };

  /* ── invalidLink / done / failed — outcome panels ─────────────── */
  if (phase === 'invalidLink' || phase === 'done' || phase === 'failed') {
    const panel = {
      invalidLink: {
        heading: 'Link incomplete',
        severity: 'error',
        body: 'This reset link is incomplete. Request a new one to continue.',
        cta: 'Request a new link', onCta: onGoForgot,
      },
      done: {
        heading: 'Password updated',
        severity: 'success',
        body: 'Your password has been updated. You can now sign in with your new password. Any other devices you were signed in on will be signed out shortly.',
        cta: 'Go to sign in', onCta: onGoLogin,
      },
      failed: {
        heading: 'Link no longer valid',
        severity: 'error',
        body: 'This reset link is no longer valid. It may have expired or already been used.',
        cta: 'Request a new link', onCta: onGoForgot,
      },
    }[phase];
    return (
      <div className="auth-shell">
        <Card className="auth-card">
          <CardBody>
            <AuthLockup />
            <div className="col gap-4">
              <h2 ref={panelHeadingRef} tabIndex={-1} style={{ outline: 'none', font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: 0 }}>
                {panel.heading}
              </h2>
              <Alert severity={panel.severity}>{panel.body}</Alert>
              <Button variant="filled" color="primary" full onClick={panel.onCta}>{panel.cta}</Button>
            </div>
          </CardBody>
        </Card>
      </div>
    );
  }

  /* ── form ─────────────────────────────────────────────────────── */
  return (
    <div className="auth-shell">
      <Card className="auth-card">
        <CardBody>
          <AuthLockup />
          <h1 style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: '0 0 18px' }}>Choose a new password</h1>
          <form onSubmit={submit} className="col gap-4"
            onKeyDown={(e) => { if (e.key === 'Enter') submit(e); }}>
            <Field label="Email" type="email" value={email} onChange={setEmail}
              placeholder="you@example.com" autoFocus required autoComplete="email" />
            <Field label="New password" type="password" value={pw} onChange={setPw}
              placeholder="••••••••" required autoComplete="new-password" />
            <PasswordRules password={pw} />
            <Field label="Confirm new password" type="password" value={confirm} onChange={setConfirm}
              placeholder="••••••••" required autoComplete="new-password"
              error={confirm.length > 0 && !matches ? 'Passwords do not match.' : ''}
              helper={matches ? 'Passwords match.' : ''} />
            {error && <Alert severity="error">{error}</Alert>}
            <Button variant="filled" color="primary" full type="submit"
              disabled={submitting || !canSubmit} onClick={submit}>
              {submitting ? 'Setting…' : 'Set new password'}
            </Button>
            <div className="muted" style={{ font: '400 13px/1 var(--font-sans)', textAlign: 'center' }}>
              <a onClick={onGoLogin} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Back to sign in</a>
            </div>
          </form>
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { ResetPassword });
