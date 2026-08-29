/* ForgotPassword — the /forgot-password page (mirrors Pages/Auth/ForgotPassword.razor).

   Anonymous, AuthLayout — the same centered 420px card as Login / Register /
   ConfirmEmail. Two phases:

     request — one Email field + "Send reset link"
     sent    — a neutral confirmation panel shown on EVERY 200 (unknown,
               unconfirmed and throttled addresses included), so the copy is
               phrased conditionally and discloses nothing. A 429 is the one
               exception: it keeps the user in `request` with an inline message.

   The emailed link lands on /reset-password. This page never reveals whether an
   address is registered.

   Specimen props: startPhase ('request' | 'sent') opens straight on a phase;
   initialError ('rate' | 'transport') seeds the inline request-phase error. */

const ForgotPassword = ({ onGoLogin, onGoReset, startPhase = 'request', initialError = null }) => {
  const { useState, useRef, useEffect } = React;
  const [phase, setPhase] = useState(startPhase);
  const [email, setEmail] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(
    initialError === 'rate' ? 'Too many attempts. Please wait a few minutes and try again.'
      : initialError === 'transport' ? 'Unable to send the reset link right now. Please try again.'
      : null
  );
  const sentHeadingRef = useRef(null);

  useEffect(() => { if (phase === 'sent' && sentHeadingRef.current) sentHeadingRef.current.focus(); }, [phase]);

  const submit = (e) => {
    e && e.preventDefault();
    if (!email.trim()) return;
    setError(null);
    setSubmitting(true);
    setTimeout(() => { setSubmitting(false); setPhase('sent'); }, 400);
  };

  return (
    <div className="auth-shell">
      <Card className="auth-card">
        <CardBody>
          <AuthLockup />

          {phase === 'request' && (
            <React.Fragment>
              <h1 style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: '0 0 8px' }}>Reset your password</h1>
              <p className="muted" style={{ font: '400 13px/1.5 var(--font-sans)', textAlign: 'center', margin: '0 0 18px' }}>
                Enter the email address for your account and we'll send you a link to set a new password.
              </p>
              <form onSubmit={submit} className="col gap-4"
                onKeyDown={(e) => { if (e.key === 'Enter') submit(e); }}>
                <Field label="Email" type="email" value={email} onChange={setEmail}
                  placeholder="you@example.com" autoFocus required autoComplete="email" />
                {error && <Alert severity="error">{error}</Alert>}
                <Button variant="filled" color="primary" full type="submit"
                  disabled={submitting || !email.trim()} onClick={submit}>
                  {submitting ? 'Sending…' : 'Send reset link'}
                </Button>
                <div className="muted" style={{ font: '400 13px/1 var(--font-sans)', textAlign: 'center' }}>
                  <a onClick={onGoLogin} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Back to sign in</a>
                </div>
              </form>
            </React.Fragment>
          )}

          {phase === 'sent' && (
            <div className="col gap-4">
              <h2 ref={sentHeadingRef} tabIndex={-1} style={{ outline: 'none', font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: 0 }}>
                Check your email
              </h2>
              <Alert severity="success">
                If an account exists for that address, we've sent a link to reset your password. The link expires in 1 hour.
              </Alert>
              <Button variant="filled" color="primary" full onClick={onGoLogin}>Back to sign in</Button>
              <div className="muted" style={{ font: '400 13px/1.4 var(--font-sans)', textAlign: 'center' }}>
                Didn't get it?{' '}
                <a onClick={() => { setEmail(''); setError(null); setPhase('request'); }}
                  style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Send again</a>
              </div>
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { ForgotPassword });
