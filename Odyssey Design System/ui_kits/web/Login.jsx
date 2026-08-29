/* Login + Register — mirrors Pages/Auth/Login.razor + Register.razor.

   Login is now a TWO-PHASE flow, matching the live LoginPhase enum:
     1. Password    — username/email + password → Sign in
     2. TwoFactor   — "Two-step verification": a 6-digit authenticator code OR a
                      recovery code, with an opt-in "Remember this device" on the
                      authenticator path. Reached when Identity returns
                      RequiresTwoFactor after the password is accepted.

   The LockedOut outcome (too many attempts, disabled by an admin, or awaiting
   approval) surfaces as a single combined error per Login.razor.

   Copy is lifted verbatim from the .razor pages. */

const AuthLockup = () => (
  <div className="col gap-3" style={{ padding: '16px 0 12px', marginBottom: 20, alignItems: 'center' }}>
    <BrandMark size={124} />
    <div style={{ font: '500 24px/1 var(--font-sans)', letterSpacing: '0.32em', color: '#00F5D4' }}>ODYSSEY</div>
  </div>
);

const LOGIN_LOCKED_MSG =
  "Your account isn't active. It may be awaiting administrator approval, disabled, "
  + "or temporarily locked after too many attempts. Contact an administrator if this persists.";

/* startPhase: 'password' | 'twofactor' — lets a specimen open straight on the
   second factor. startRecovery flips the 2FA card to its recovery-code mode.
   initialError: 'creds' | 'locked' | 'code' seeds an error for the specimen. */
const Login = ({
  onLogin, onGoRegister, onGoForgot,
  startPhase = 'password', startRecovery = false, initialError = null, requiresTwoFactor = false,
  reason = null,
}) => {
  const { useState } = React;
  const [phase, setPhase] = useState(startPhase);
  const [email, setEmail] = useState('jane@odyssey.app');
  const [password, setPassword] = useState('demo');
  const [code, setCode] = useState('');
  const [useRecovery, setUseRecovery] = useState(startRecovery);
  const [remember, setRemember] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(
    initialError === 'creds' ? 'Unable to sign in. Please check your username/email and password.'
      : initialError === 'locked' ? LOGIN_LOCKED_MSG
      : initialError === 'code' ? 'Incorrect code. Please try again.'
      : null
  );

  const submitPassword = (e) => {
    e && e.preventDefault();
    setError(null);
    setSubmitting(true);
    setTimeout(() => {
      setSubmitting(false);
      if (!email || !password) {
        setError('Unable to sign in. Please check your username/email and password.');
      } else if (requiresTwoFactor) {
        setPhase('twofactor');
      } else {
        onLogin && onLogin();
      }
    }, 350);
  };

  const submitCode = (e) => {
    e && e.preventDefault();
    const entered = code.trim();
    if (!entered) return;
    setError(null);
    setSubmitting(true);
    setTimeout(() => {
      setSubmitting(false);
      const ok = useRecovery ? entered.length >= 8 : /^\d{6}$/.test(entered);
      if (ok) onLogin && onLogin();
      else setError('Incorrect code. Please try again.');
      if (!ok) setCode('');
    }, 350);
  };

  const backToPassword = () => {
    setPhase('password'); setCode(''); setUseRecovery(false); setRemember(false); setError(null);
  };
  const toggleRecovery = () => { setUseRecovery(v => !v); setCode(''); setError(null); };

  /* ── Phase 2 · Two-step verification ────────────────────────────── */
  if (phase === 'twofactor') {
    return (
      <div className="auth-shell">
        <Card className="auth-card">
          <CardBody>
            <AuthLockup />
            <div style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', marginBottom: 8 }}>
              Two-step verification
            </div>
            <div className="muted" style={{ font: '400 13px/1.5 var(--font-sans)', textAlign: 'center', marginBottom: 16 }}>
              {useRecovery
                ? 'Enter one of the recovery codes you saved when you set up two-factor.'
                : 'Enter the 6-digit code from your authenticator app.'}
            </div>
            <form onSubmit={submitCode} className="col gap-4">
              <Field
                label={useRecovery ? 'Recovery code' : 'Authentication code'}
                value={code}
                onChange={(v) => setCode(useRecovery ? v : v.replace(/\D/g, '').slice(0, 6))}
                placeholder={useRecovery ? 'xxxxx-xxxxx' : '000000'}
                autoFocus
              />

              {!useRecovery && (
                <div className="auth-remember">
                  <Checkbox checked={remember} onChange={(next) => setRemember(next)}
                    label={
                      <span className="auth-remember-text">
                        <span className="auth-remember-ttl">Remember this device</span>
                        <span className="auth-remember-sub">Skip the verification code on this browser next time. Avoid this on shared computers.</span>
                      </span>
                    } />
                </div>
              )}

              {error && <Alert severity="error">{error}</Alert>}

              <Button variant="filled" color="primary" full type="submit"
                disabled={submitting || !code.trim()} onClick={submitCode}>
                {submitting ? 'Verifying…' : 'Verify'}
              </Button>

              <div className="col gap-1" style={{ alignItems: 'center', marginTop: 2 }}>
                <Button variant="text" color="primary" onClick={toggleRecovery}>
                  {useRecovery ? 'Use your authenticator app instead' : "Can't access your app? Use a recovery code"}
                </Button>
                <Button variant="text" onClick={backToPassword}>Back to sign in</Button>
              </div>
            </form>
          </CardBody>
        </Card>
      </div>
    );
  }

  /* ── Phase 1 · Password ─────────────────────────────────────────── */
  return (
    <div className="auth-shell">
      <Card className="auth-card">
        <CardBody>
          <AuthLockup />
          <h1 style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: '0 0 16px' }}>Sign in</h1>
          {reason === 'legal-declined' && !error && (
            <div style={{ marginBottom: 14 }}>
              <Alert severity="info">
                You declined the License or Terms of Service and were signed out. Sign in again to review and accept them — your account isn’t locked.
              </Alert>
            </div>
          )}
          <form onSubmit={submitPassword} className="col gap-4">
            <Field label="Username or Email" value={email} onChange={setEmail} autoFocus />
            <Field label="Password" type="password" value={password} onChange={setPassword} />
            <div style={{ marginTop: -6, textAlign: 'right' }}>
              <a onClick={onGoForgot} style={{ font: '400 13px/1 var(--font-sans)', color: 'var(--sea-400)', cursor: 'pointer' }}>Forgot your password?</a>
            </div>
            {error && <Alert severity="error">{error}</Alert>}
            <Button variant="filled" color="primary" full disabled={submitting} type="submit" onClick={submitPassword}>
              {submitting ? 'Signing in…' : 'Sign in'}
            </Button>
            <div className="muted" style={{ font: '400 13px/1 var(--font-sans)' }}>
              Need an account?{' '}
              <a onClick={onGoRegister} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Register</a>
            </div>
          </form>
        </CardBody>
      </Card>
    </div>
  );
};

/* Registration informational review — the License and current ToS shown for
   transparency at sign-up (spec §3 state 1). The checkboxes are UX-only: the
   authoritative acceptance is recorded at first login, and a failed text load
   must NOT block Submit (this preview is informational). */
const RegisterLegalReview = ({ agreeLicense, agreeTos, onLicense, onTos }) => {
  const { useState } = React;
  const [openDoc, setOpenDoc] = useState(null); // 'License' | 'TermsOfService' | null
  const L = window.OdysseyLegal || {};
  const tos = L.currentTos || null;
  const link = (label, onClick) => (
    <a role="button" tabIndex={0} onClick={(e) => { e.preventDefault(); e.stopPropagation(); onClick(); }}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } }}
      style={{ color: 'var(--sea-400)', cursor: 'pointer', textDecoration: 'underline', textUnderlineOffset: 2 }}>{label}</a>
  );
  return (
    <div className="lg-reg-checks">
      <Checkbox checked={agreeLicense} onChange={onLicense}
        label={<span>I have read and agree to the {link('software license', () => setOpenDoc('License'))}</span>} />
      {tos ? (
        <Checkbox checked={agreeTos} onChange={onTos}
          label={<span>I have read and agree to the {link('Terms of Service', () => setOpenDoc('TermsOfService'))}</span>} />
      ) : (
        <div className="lg-reg-nopub"><span className="material-icons" aria-hidden="true">description</span>Terms of Service has not been published yet.</div>
      )}

      {openDoc === 'License' && (
        <Modal title="Software License" subtitle="Repository LICENSE" icon="gavel"
          onClose={() => setOpenDoc(null)}
          footer={<Button variant="filled" onClick={() => setOpenDoc(null)}>Close</Button>}>
          <div className="lg-ver-view" tabIndex={0} style={{ fontFamily: 'var(--font-mono)', fontSize: 12.5, lineHeight: 1.7 }}>{L.licenseText || 'License text unavailable right now — you can still create your account and review it at first login.'}</div>
        </Modal>
      )}
      {openDoc === 'TermsOfService' && tos && (
        <Modal title="Terms of Service" subtitle={`Version ${tos.id} · Effective ${tos.effective}`} icon="description"
          onClose={() => setOpenDoc(null)}
          footer={<Button variant="filled" onClick={() => setOpenDoc(null)}>Close</Button>}>
          <div className="lg-ver-view" tabIndex={0}>{tos.content}</div>
        </Modal>
      )}
    </div>
  );
};

/* Register — on success the user must confirm their email before signing in
   (the email-confirmation flow), so the success copy points them at their inbox
   rather than the old "you can now log in". Primary reads "Create account" per
   the New/Create wording convention. */
const Register = ({ onDone, onGoLogin, startState = null }) => {
  const { useState } = React;
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [msg, setMsg] = useState(
    startState === 'success' ? 'Account created. Check your email for a confirmation link — you\'ll need to confirm before signing in.'
      : startState === 'mismatch' ? 'Passwords do not match.'
      : null
  );
  const [ok, setOk] = useState(startState === 'success');
  const [done, setDone] = useState(startState === 'success');
  const [agreeLicense, setAgreeLicense] = useState(false);
  const [agreeTos, setAgreeTos] = useState(false);

  const submit = (e) => {
    e && e.preventDefault();
    const emailOk = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim());
    if (!emailOk) {
      setOk(false); setDone(false);
      setMsg('Enter a valid email address.');
      return;
    }
    if (!PASSWORD_POLICY.isSatisfied(password)) {
      setOk(false); setDone(false);
      setMsg(`Your password must be at least ${PASSWORD_POLICY.minLength} characters and include an uppercase letter, a lowercase letter, a number and a symbol.`);
      return;
    }
    if (password !== confirm) {
      setOk(false); setDone(false);
      setMsg('Passwords do not match.');
      return;
    }
    setOk(true); setDone(true);
    setMsg('Account created. Check your email for a confirmation link — you\'ll need to confirm before signing in.');
  };

  return (
    <div className="auth-shell">
      <Card className="auth-card">
        <CardBody>
          <AuthLockup />
          <h1 style={{ font: '500 22px/1 var(--font-sans)', letterSpacing: '-0.01em', textAlign: 'center', margin: '0 0 16px' }}>Create account</h1>
          <form onSubmit={submit} className="col gap-4">
            <Field label="Email" value={email} onChange={setEmail} autoFocus disabled={done} />
            <Field label="Password" type="password" value={password} onChange={setPassword} disabled={done} />
            {!done && <PasswordRules password={password} />}
            <Field label="Confirm Password" type="password" value={confirm} onChange={setConfirm} disabled={done} />
            {!done && (
              <RegisterLegalReview
                agreeLicense={agreeLicense} agreeTos={agreeTos}
                onLicense={setAgreeLicense} onTos={setAgreeTos} />
            )}
            {msg && <Alert severity={ok ? 'success' : 'error'}>{msg}</Alert>}
            {!done && <Button variant="filled" color="primary" full type="submit" disabled={!email.trim() || !PASSWORD_POLICY.isSatisfied(password) || password !== confirm || !agreeLicense || (!!(window.OdysseyLegal && window.OdysseyLegal.currentTos) && !agreeTos)} onClick={submit}>Create account</Button>}
            <div className="muted" style={{ font: '400 13px/1 var(--font-sans)' }}>
              Already have an account?{' '}
              <a onClick={onGoLogin} style={{ color: 'var(--sea-400)', cursor: 'pointer' }}>Login</a>
            </div>
          </form>
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { Login, Register, AuthLockup, LOGIN_LOCKED_MSG, RegisterLegalReview });
