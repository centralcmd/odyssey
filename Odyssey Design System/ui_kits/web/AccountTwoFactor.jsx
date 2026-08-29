/* =============================================================
   AccountTwoFactor.jsx — the authenticator-app 2FA surface of the
   self-service Account area. Fully clickable mock:

     OFF  → Set up → [1] scan QR / copy key → [2] enter 6-digit code
            → [3] save recovery codes → ON
     ON   → status + recovery-code regeneration + danger zone
            (reset authenticator key, turn off 2FA), each gated by an
            inline confirmation step (the Users "manage access" pattern).

   State (enabled / recoveryCodes / codesRemaining / enabledAt) is lifted
   to AccountPage so the Overview tab reflects it. Styling in account.css.
   ============================================================= */

/* ---- Deterministic faux-QR (squares only — a stand-in, not a real code) ---- */
const accSeeded = (seed) => {
  let s = 0;
  for (let i = 0; i < seed.length; i++) s = (s * 31 + seed.charCodeAt(i)) >>> 0;
  return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
};
const AccQR = ({ seed = 'odyssey', size = 164 }) => {
  const N = 25, cell = size / N;
  const rnd = accSeeded(seed);
  const inFinder = (x, y) => {
    const f = (cx, cy) => x >= cx && x < cx + 7 && y >= cy && y < cy + 7;
    return f(0, 0) || f(N - 7, 0) || f(0, N - 7);
  };
  const rects = [];
  for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
    if (inFinder(x, y)) continue;
    if (rnd() > 0.55) rects.push(<rect key={`${x}-${y}`} x={x * cell} y={y * cell} width={cell} height={cell} />);
  }
  const finder = (cx, cy) => (
    <React.Fragment key={`f-${cx}-${cy}`}>
      <rect x={cx * cell} y={cy * cell} width={7 * cell} height={7 * cell} />
      <rect x={(cx + 1) * cell} y={(cy + 1) * cell} width={5 * cell} height={5 * cell} fill="#fff" />
      <rect x={(cx + 2) * cell} y={(cy + 2) * cell} width={3 * cell} height={3 * cell} fill="#0E1525" />
    </React.Fragment>
  );
  return (
    <svg viewBox={`0 0 ${size} ${size}`} role="img" aria-label="Authenticator QR code" fill="#0E1525">
      {rects}
      {finder(0, 0)}{finder(N - 7, 0)}{finder(0, N - 7)}
    </svg>
  );
};

/* ---- Deterministic recovery codes + shared key ---- */
const accGenCodes = (seed) => {
  const rnd = accSeeded(seed);
  const chars = 'abcdefghjkmnpqrstuvwxyz23456789';
  const block = (n) => Array.from({ length: n }, () => chars[Math.floor(rnd() * chars.length)]).join('');
  return Array.from({ length: 10 }, () => `${block(4)}-${block(4)}`);
};
const accSharedKey = 'JBSWY3DPEHPK3PXP';
const accFmtKey = (k) => k.replace(/(.{4})/g, '$1 ').trim();

/* ---- 6-digit code entry with auto-advance ---- */
const AccCodeInput = ({ value, onChange, error }) => {
  const { useRef } = React;
  const refs = useRef([]);
  const set = (i, ch) => {
    const digit = ch.replace(/\D/g, '').slice(-1);
    const next = value.split('');
    next[i] = digit || '';
    onChange(next.join('').slice(0, 6));
    if (digit && i < 5 && refs.current[i + 1]) refs.current[i + 1].focus();
  };
  const onKey = (i, e) => {
    if (e.key === 'Backspace' && !value[i] && i > 0 && refs.current[i - 1]) refs.current[i - 1].focus();
  };
  const onPaste = (e) => {
    const txt = (e.clipboardData.getData('text') || '').replace(/\D/g, '').slice(0, 6);
    if (txt) { e.preventDefault(); onChange(txt); const last = Math.min(txt.length, 5); if (refs.current[last]) refs.current[last].focus(); }
  };
  return (
    <div className={`acc-code ${error ? 'err' : ''}`} onPaste={onPaste}>
      {Array.from({ length: 6 }).map((_, i) => (
        <input key={i} ref={el => refs.current[i] = el}
          inputMode="numeric" autoComplete="one-time-code" maxLength={1}
          aria-label={`Digit ${i + 1}`}
          value={value[i] || ''}
          onChange={(e) => set(i, e.target.value)}
          onKeyDown={(e) => onKey(i, e)} />
      ))}
    </div>
  );
};

/* ---- Recovery-code panel (grid + copy / download / print) ---- */
const AccRecoveryCodes = ({ codes }) => {
  const { useState } = React;
  const [copied, setCopied] = useState(false);
  const copy = () => {
    if (navigator.clipboard) navigator.clipboard.writeText(codes.join('\n'));
    setCopied(true); setTimeout(() => setCopied(false), 1800);
  };
  const download = () => {
    const body = 'Odyssey — two-factor recovery codes\n'
      + 'Each code can be used once if you lose your authenticator device.\n\n'
      + codes.join('\n') + '\n';
    const blob = new Blob([body], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = 'odyssey-recovery-codes.txt';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  return (
    <div className="col gap-4">
      <div className="acc-codes-grid">
        {codes.map((c, i) => <div key={i} className="acc-rc">{c}</div>)}
      </div>
      <div className="row gap-2" style={{ flexWrap: 'wrap' }}>
        <Button variant="outlined" icon={copied ? 'check' : 'content_copy'} onClick={copy}>
          {copied ? 'Copied' : 'Copy codes'}
        </Button>
        <Button variant="outlined" icon="download" onClick={download}>Download</Button>
      </div>
    </div>
  );
};

/* ============================================================= */
function AccountTwoFactor({ tfa, setTfa, initialPhase }) {
  const { useState } = React;
  // wizard: 'idle' | 'scan' | 'verify' | 'codes'   (codes also used post-enable for regen display)
  // initialPhase lets a specimen open straight on a given step (the live page always starts 'idle').
  const [phase, setPhase] = useState(initialPhase || 'idle');
  const [showKey, setShowKey] = useState(initialPhase === 'scan' || initialPhase === 'verify');
  const [code, setCode] = useState('');
  const [codeErr, setCodeErr] = useState(false);
  const [draftCodes, setDraftCodes] = useState(
    (initialPhase === 'codes' || initialPhase === 'codes-regen') ? accGenCodes('specimen-codes') : []
  );
  const [confirm, setConfirm] = useState(null); // 'disable' | 'reset' | 'regen'
  const [phrase, setPhrase] = useState('');

  const startSetup = () => { setPhase('scan'); setShowKey(false); setCode(''); setCodeErr(false); };
  const cancelSetup = () => { setPhase('idle'); setCode(''); setCodeErr(false); };

  const verify = () => {
    if (code.length < 6) { setCodeErr(true); return; }
    setDraftCodes(accGenCodes('enable-' + code));
    setPhase('codes');
  };
  const finishEnable = () => {
    setTfa({ enabled: true, recoveryCodes: draftCodes, codesRemaining: draftCodes.length, enabledAt: 'Just now' });
    setPhase('idle'); setCode('');
  };

  const doRegen = () => {
    const fresh = accGenCodes('regen-' + Date.now());
    setDraftCodes(fresh);
    setTfa(t => ({ ...t, recoveryCodes: fresh, codesRemaining: fresh.length }));
    setConfirm(null); setPhase('codes-regen');
  };
  const doReset = () => { setConfirm(null); setTfa(t => ({ ...t, enabled: false, recoveryCodes: [], codesRemaining: 0 })); startSetup(); };
  const doDisable = () => { setConfirm(null); setPhrase(''); setTfa({ enabled: false, recoveryCodes: [], codesRemaining: 0, enabledAt: null }); setPhase('idle'); };

  const Stepper = ({ active }) => {
    const steps = [['Scan', 'scan'], ['Verify', 'verify'], ['Save codes', 'codes']];
    const idx = steps.findIndex(s => s[1] === active);
    return (
      <div className="acc-steps">
        {steps.map(([label, key], i) => (
          <React.Fragment key={key}>
            {i > 0 && <span className="acc-step-line" />}
            <span className={`acc-step ${i === idx ? 'active' : ''} ${i < idx ? 'done' : ''}`}>
              <span className="acc-step-dot">{i < idx ? <MIcon name="check" size={16} /> : i + 1}</span>
              <span className="acc-step-label">{label}</span>
            </span>
          </React.Fragment>
        ))}
      </div>
    );
  };

  /* ---------- DISABLED + setup wizard ----------
     Enter this block only for the enrollment wizard (scan/verify/codes) or when
     2FA is off. The enabled-state 'codes-regen' phase is handled by the ENABLED
     render below, so it must NOT fall in here (that would show the OFF landing). */
  const ACC_SETUP_PHASES = ['scan', 'verify', 'codes'];
  if (!tfa.enabled || ACC_SETUP_PHASES.includes(phase)) {
    // Wizard active?
    if (phase === 'scan' || phase === 'verify') {
      return (
        <div className="acc-section">
          <Card outlined><CardBody style={{ padding: 20 }}>
            <div className="acc-cardhead bordered">
              <span className="acc-ic"><MIcon name="qr_code_2" /></span>
              <div className="acc-sec-titles">
                <div className="acc-sec-title">Set up authenticator app</div>
                <div className="acc-sec-sub">Use Google Authenticator, 1Password, Authy, or any TOTP app.</div>
              </div>
            </div>
            <Stepper active={phase} />
            <div className="acc-divider" style={{ margin: '18px 0' }} />
            <div className="acc-setup">
              <div className="acc-qr-wrap">
                <div className="acc-qr"><AccQR seed="odyssey-jane-2fa" /></div>
                <div className="acc-qr-note">otpauth://totp/Odyssey:jane</div>
              </div>
              <div className="acc-setup-side">
                <div className="acc-setup-step-ttl"><b>1.</b> Scan this QR code with your authenticator app.</div>
                <div>
                  <div className="ods-caption" style={{ marginBottom: 6 }}>Can't scan? Enter this key manually:</div>
                  <div className="acc-key-row">
                    <code className={`acc-key ${showKey ? '' : 'masked'}`}>{showKey ? accFmtKey(accSharedKey) : '•••• •••• •••• ••••'}</code>
                    <Button variant="text" icon={showKey ? 'visibility_off' : 'visibility'} onClick={() => setShowKey(s => !s)}>
                      {showKey ? 'Hide' : 'Show key'}
                    </Button>
                    {showKey && (
                      <Button variant="text" icon="content_copy"
                        onClick={() => navigator.clipboard && navigator.clipboard.writeText(accSharedKey)}>Copy</Button>
                    )}
                  </div>
                </div>
                <div className="acc-divider" />
                <div className="acc-setup-step-ttl"><b>2.</b> Enter the 6-digit code your app shows.</div>
                <AccCodeInput value={code} onChange={(v) => { setCode(v); setCodeErr(false); }} error={codeErr} />
                {codeErr && <div className="ods-caption" style={{ color: 'var(--mud-palette-error)' }}>Enter all 6 digits from your authenticator app.</div>}
              </div>
            </div>
            <div className="acc-form-actions" style={{ marginTop: 20 }}>
              <Button variant="text" onClick={cancelSetup}>Cancel</Button>
              <Button variant="filled" color="primary" icon="check" onClick={verify} disabled={code.length < 6}>
                Verify &amp; enable
              </Button>
            </div>
          </CardBody></Card>
        </div>
      );
    }

    if (phase === 'codes') {
      return (
        <div className="acc-section">
          <Card outlined><CardBody style={{ padding: 20 }}>
            <div className="acc-cardhead bordered">
              <span className="acc-ic ok"><MIcon name="verified_user" /></span>
              <div className="acc-sec-titles">
                <div className="acc-sec-title">Save your recovery codes</div>
                <div className="acc-sec-sub">Two-factor is almost on — store these somewhere safe first.</div>
              </div>
            </div>
            <Stepper active="codes" />
            <div className="acc-divider" style={{ margin: '18px 0' }} />
            <Alert severity="warning">
              <b>These codes are shown only once.</b> Each one lets you sign in if you lose your authenticator device. Keep them somewhere only you can reach.
            </Alert>
            <div style={{ height: 16 }} />
            <AccRecoveryCodes codes={draftCodes} />
            <div className="acc-form-actions" style={{ marginTop: 20 }}>
              <Button variant="filled" color="primary" icon="lock" onClick={finishEnable}>I've saved my codes — finish</Button>
            </div>
          </CardBody></Card>
        </div>
      );
    }

    /* phase === 'idle' && disabled → the OFF landing */
    return (
      <div className="acc-section">
        <div className="acc-status">
          <span className="acc-status-ic"><MIcon name="gpp_maybe" /></span>
          <div className="acc-status-txt">
            <div className="acc-status-ttl">Two-factor is off</div>
            <div className="acc-status-sub">Your account is protected by your password alone. Turn on 2FA for stronger protection.</div>
          </div>
          <Button variant="filled" color="primary" icon="add_moderator" onClick={startSetup}>Set up two-factor</Button>
        </div>
      </div>
    );
  }

  /* ---------- ENABLED ---------- */
  return (
    <div className="acc-section">
      <div className="acc-status on">
        <span className="acc-status-ic"><MIcon name="gpp_good" /></span>
        <div className="acc-status-txt">
          <div className="acc-status-ttl">Two-factor is on</div>
          <div className="acc-status-sub">Authenticator app · enabled {tfa.enabledAt || 'recently'} · {tfa.codesRemaining} recovery codes remaining</div>
        </div>
        <Chip tone="income" icon="check_circle">Protected</Chip>
      </div>

      {/* Recovery codes */}
      <Card outlined><CardBody>
        <div className="acc-card-head"><MIcon name="vpn_key" /><span className="acc-card-ttl">Recovery codes</span></div>
        {phase === 'codes-regen' ? (
          <div className="col gap-4">
            <Alert severity="warning"><b>New recovery codes generated.</b> Your previous codes no longer work. Save these now — they're shown only once.</Alert>
            <AccRecoveryCodes codes={draftCodes} />
            <div><Button variant="filled" color="primary" icon="check" onClick={() => setPhase('idle')}>Done, I've saved them</Button></div>
          </div>
        ) : (
          <div className="col gap-4">
            <div className="acc-sec-sub" style={{ marginTop: 0 }}>
              You have <b style={{ color: 'var(--mud-palette-text-primary)' }}>{tfa.codesRemaining}</b> unused recovery codes. Generate a fresh set if you've run low or think they may be exposed.
            </div>
            {confirm === 'regen' ? (
              <Alert severity="warning">
                <b>Generate new recovery codes?</b> Your current {tfa.codesRemaining} codes will stop working immediately.
                <div className="row gap-2" style={{ marginTop: 10 }}>
                  <Button variant="text" onClick={() => setConfirm(null)}>Cancel</Button>
                  <Button variant="filled" color="primary" icon="autorenew" onClick={doRegen}>Yes, regenerate</Button>
                </div>
              </Alert>
            ) : (
              <div><Button variant="outlined" icon="autorenew" onClick={() => setConfirm('regen')}>Generate new recovery codes</Button></div>
            )}
          </div>
        )}
      </CardBody></Card>

      {/* Danger zone */}
      <div className="acc-danger">
        <div className="acc-danger-head"><MIcon name="warning" /><span className="acc-danger-head-ttl">Danger zone</span></div>

        <div className="acc-danger-row">
          <div className="acc-danger-txt">
            <div className="acc-danger-ttl">Reset authenticator key</div>
            <div className="acc-danger-sub">Disconnects your current app and walks you through setup again. 2FA stays required.</div>
          </div>
          {confirm === 'reset'
            ? <div className="row gap-2"><Button variant="text" onClick={() => setConfirm(null)}>Cancel</Button><Button variant="filled" className="danger" color="" icon="restart_alt" onClick={doReset}>Reset key</Button></div>
            : <Button variant="outlined" icon="restart_alt" onClick={() => setConfirm('reset')}>Reset key</Button>}
        </div>

        <div className="acc-danger-row" style={{ alignItems: confirm === 'disable' ? 'flex-start' : 'center' }}>
          <div className="acc-danger-txt">
            <div className="acc-danger-ttl">Turn off two-factor</div>
            <div className="acc-danger-sub">Removes the second step at sign-in. Your account will be protected by its password only.</div>
            {confirm === 'disable' && (
              <div style={{ marginTop: 12, maxWidth: 320 }}>
                <Field label={'Type DISABLE to confirm'} value={phrase} onChange={setPhrase} placeholder="DISABLE" />
              </div>
            )}
          </div>
          {confirm === 'disable'
            ? <div className="row gap-2"><Button variant="text" onClick={() => { setConfirm(null); setPhrase(''); }}>Cancel</Button><Button variant="filled" className="danger" color="" icon="gpp_bad" disabled={phrase.trim().toUpperCase() !== 'DISABLE'} onClick={doDisable}>Turn off 2FA</Button></div>
            : <Button variant="outlined" icon="gpp_bad" onClick={() => setConfirm('disable')}>Turn off</Button>}
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { AccountTwoFactor, AccRecoveryCodes, AccQR });
