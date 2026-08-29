/* ConfirmEmail — the /confirm-email landing page (mirrors Pages/Auth/ConfirmEmail.razor).

   Identity emails a confirmation link after registration (and after an email
   change); this page is where that link lands. It verifies the userId + code in
   the query string, then shows one of three phases:

     verifying  — a spinner while the code is checked
     confirmed  — success; "Go to sign in"
     failed     — link invalid/expired; collect an email and resend a fresh link

   When the link came from the email-CHANGE flow it carries a changedEmail param,
   and the confirmed copy reflects the new address (pass `changed`).

   Renders on AuthLayout — the same centered 420px card as Login / Register. */

const ConfirmEmail = ({ phase = 'verifying', changed = false, onGoLogin }) => {
  const { useState } = React;
  const [resendEmail, setResendEmail] = useState('');
  const [resent, setResent] = useState(false);

  return (
    <div className="auth-shell">
      <Card className="auth-card">
        <CardBody>
          <AuthLockup />

          {phase === 'verifying' && (
            <div className="auth-confirm">
              <span className="auth-confirm-spin" role="status" aria-label="Confirming" />
              <div style={{ font: '400 15px/1.4 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>
                Confirming your email…
              </div>
            </div>
          )}

          {phase === 'confirmed' && (
            <div className="col gap-4">
              <div style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center' }}>
                Email confirmed
              </div>
              <Alert severity="success">
                {changed
                  ? 'Your new email address has been confirmed and is now your sign-in identity. You can sign in with it.'
                  : 'Your email address has been confirmed. You can now sign in.'}
              </Alert>
              <Button variant="filled" color="primary" full onClick={onGoLogin}>Go to sign in</Button>
            </div>
          )}

          {phase === 'failed' && (
            <div className="col gap-4">
              <div style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center' }}>
                Confirmation failed
              </div>
              <div className="muted" style={{ font: '400 13px/1.5 var(--font-sans)', textAlign: 'center' }}>
                This confirmation link is invalid or has expired. Enter your email to receive a new one.
              </div>
              <Field label="Email" value={resendEmail} onChange={setResendEmail} placeholder="you@example.com" />
              {resent && (
                <Alert severity="success">
                  If that email matches an unconfirmed account, a new confirmation link is on its way.
                </Alert>
              )}
              <Button variant="filled" color="primary" full
                disabled={!resendEmail.trim()} onClick={() => setResent(true)}>
                Resend confirmation email
              </Button>
              <div className="muted" style={{ font: '400 13px/1 var(--font-sans)', textAlign: 'center' }}>
                <a onClick={onGoLogin} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Back to sign in</a>
              </div>
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { ConfirmEmail });
