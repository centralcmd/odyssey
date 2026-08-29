/* =============================================================
   AcceptTerms.jsx — the /accept-terms interstitial (spec §3 state 2–4, §5).

   Full-screen, blocking, rendered under its OWN dedicated layout (no drawer /
   module rail / nav shell) — the surface a signed-in but non-compliant user is
   routed to, either at login (the MainLayout gate) or mid-session (a 451 from
   any typed client, intercepted by LegalComplianceHandler, carrying returnUrl).

   Shows ONLY the document(s) actually outstanding. Each has independent
   Accept / Decline. ToS's Accept echoes the tosVersionId from /current.
   Accepting every outstanding document forwards to returnUrl; declining any one
   signs the user out and returns to /login?reason=legal-declined (no lockout).

   The dashed "Preview state" bar is a design-review aid (mirrors SystemSettings)
   — it drives which documents are outstanding, the load phase, and the terminal
   outcome. It is NOT part of the shipped page.

   Built from DS atoms + the shared onboarding/gate shell vocabulary.
   ============================================================= */

// A scrollable, keyboard-reachable read-only document block. Tracks whether the
// reader has reached the bottom so the "more below" fade can retract (a11y hint
// only — Accept is enabled as soon as the text has loaded, per spec).
function LegalScrollDoc({ text, prose, labelledBy, onEnd }) {
  const { useRef, useState, useEffect } = React;
  const ref = useRef(null);
  const [atEnd, setAtEnd] = useState(false);
  const check = () => {
    const el = ref.current; if (!el) return;
    const done = el.scrollTop + el.clientHeight >= el.scrollHeight - 4;
    const end = done || el.scrollHeight <= el.clientHeight;
    setAtEnd(end);
    if (end && onEnd) onEnd();
  };
  useEffect(() => { check(); }, [text]);
  return (
    <div className="lg-scroll-wrap">
      <div ref={ref} className={`lg-scroll${prose ? ' prose' : ''}`} tabIndex={0}
        role="region" aria-labelledby={labelledBy} onScroll={check}>{text}</div>
      <div className={`lg-fade${atEnd ? ' hidden' : ''}`} aria-hidden="true" />
    </div>
  );
}

const AT_DOC_META = {
  License: { icon: 'gavel', title: 'Software License' },
  TermsOfService: { icon: 'description', title: 'Terms of Service' },
};

function AcceptTerms({ onDone, returnLabel = 'where you left off' }) {
  const { useState, useMemo, useRef, useEffect } = React;
  const L = window.OdysseyLegal || {};

  // ---- Review controls (design aid; not shipped) ----
  const [scope, setScope] = useState('both');   // both | license | tos | none-pub
  const [phase, setPhase] = useState('ready');  // ready | loading | error
  const [outcome, setOutcome] = useState(null); // null | accepted | declined

  // ---- Interaction state ----
  const [accepted, setAccepted] = useState({ License: false, TermsOfService: false });
  const [reviewed, setReviewed] = useState({ License: false, TermsOfService: false });
  const [skipped, setSkipped] = useState({ License: false, TermsOfService: false });
  const [busy, setBusy] = useState(null);           // doc key currently saving
  const [confirmDecline, setConfirmDecline] = useState(null); // doc key or null
  const headingRef = useRef(null);

  // Which documents this session actually owes, per the review scope. The
  // wizard always keeps a step per document; a document with no content to
  // accept (ToS not yet published, or a License that failed to load — a
  // fallback that shouldn't normally happen) becomes an acknowledge-and-
  // Continue step instead of an Accept step.
  const tosPublished = scope !== 'none-pub';
  const wantLicense = scope === 'both' || scope === 'license' || scope === 'none-pub';
  const wantTos = scope === 'both' || scope === 'tos' || scope === 'none-pub';
  const docs = [wantLicense && 'License', wantTos && 'TermsOfService'].filter(Boolean);

  const isAvailable = (key) => key === 'TermsOfService'
    ? (tosPublished && !!(L.currentTos && L.currentTos.content))
    : !!L.licenseText;
  const isDone = (key) => accepted[key] || skipped[key];

  const allAccepted = docs.length > 0 && docs.every(isDone);
  const doneCount = docs.filter(isDone).length;
  const currentKey = docs.find((d) => !isDone(d)) || null;

  useEffect(() => { if (headingRef.current) headingRef.current.focus(); }, []);

  // Reset interaction when the reviewer flips scope/phase/outcome.
  const resetTo = (fn) => { setAccepted({ License: false, TermsOfService: false }); setReviewed({ License: false, TermsOfService: false }); setSkipped({ License: false, TermsOfService: false }); setBusy(null); setConfirmDecline(null); setOutcome(null); fn(); };

  // Accepting the last outstanding document forwards to returnUrl.
  useEffect(() => {
    if (allAccepted && !outcome) {
      const t = setTimeout(() => { setOutcome('accepted'); onDone && onDone('accepted'); }, 480);
      return () => clearTimeout(t);
    }
  }, [allAccepted]); // eslint-disable-line

  const accept = (key) => {
    if (phase !== 'ready' || busy || !reviewed[key]) return;
    setBusy(key);
    // POST /api/legal/respond { accepted:true, tosVersionId } → RefreshSignInAsync
    setTimeout(() => { setBusy(null); setAccepted((a) => ({ ...a, [key]: true })); }, 460);
  };

  const doDecline = () => {
    setConfirmDecline(null);
    setOutcome('declined');
    // server signs the session out; client clears cached auth → /login?reason=legal-declined
  };

  // Acknowledge a step that has nothing to accept (unavailable document) and
  // advance the wizard, without recording an acceptance.
  const skip = (key) => { if (busy) return; setSkipped((s) => ({ ...s, [key]: true })); };

  const docMeta = (key) => {
    if (key === 'License') {
      return (
        <span className="lg-doc-meta">
          <span>Repository <b>LICENSE</b></span>
          <span className="lg-meta-dot" aria-hidden="true" />
          <span className="mono" title="SHA-256 of the accepted content">sha256:{(L.licenseSha || '').slice(0, 12)}…</span>
        </span>
      );
    }
    const v = L.currentTos || {};
    return (
      <span className="lg-doc-meta">
        <span>Version {v.id}</span>
        <span className="lg-meta-dot" aria-hidden="true" />
        <span>Effective {v.effective}</span>
      </span>
    );
  };

  const docText = (key) => key === 'License' ? (L.licenseText || '') : ((L.currentTos && L.currentTos.content) || '');

  const renderDoc = (key) => {
    const meta = AT_DOC_META[key];
    const done = isDone(key);
    const titleId = `lg-ttl-${key}`;

    // Unavailable-document step: nothing to accept — acknowledge and Continue.
    if (isAvailable(key) === false && phase === 'ready') {
      const isTos = key === 'TermsOfService';
      return (
        <div key={key} className={`lg-doc${done ? ' done' : ''}`}>
          <div className="lg-doc-head">
            <span className="lg-doc-ic"><span className="material-icons" aria-hidden="true">{meta.icon}</span></span>
            <div className="lg-doc-titles">
              <div className="lg-doc-ttl" id={titleId}>{meta.title}</div>
              <span className="lg-doc-meta"><span>{isTos ? 'Not published yet' : 'Currently unavailable'}</span></span>
            </div>
          </div>
          <div style={{ padding: '18px' }}>
            <div className="lg-notpub" role="note">
              <span className="material-icons" aria-hidden="true">{isTos ? 'description' : 'gavel'}</span>
              <div className="lg-notpub-txt">{isTos
                ? <><b>No Terms of Service has been published yet.</b> There’s nothing to accept for this step. Continue for now — you’ll be asked to accept the Terms once an administrator publishes them.</>
                : <><b>The software license couldn’t be loaded.</b> There’s nothing to accept for this step right now. You can continue, and you’ll be asked again once it’s available.</>}</div>
            </div>
          </div>
          <div className="lg-doc-actions">
            {done ? (
              <span className="lg-accepted"><span className="material-icons" aria-hidden="true">check_circle</span>Acknowledged</span>
            ) : (
              <Button variant="filled" iconRight="arrow_forward" onClick={() => skip(key)}>Continue</Button>
            )}
          </div>
        </div>
      );
    }

    return (
      <div key={key} className={`lg-doc${done ? ' done' : ''}`}>
        <div className="lg-doc-head">
          <span className="lg-doc-ic"><span className="material-icons" aria-hidden="true">{meta.icon}</span></span>
          <div className="lg-doc-titles">
            <div className="lg-doc-ttl" id={titleId}>{meta.title}</div>
            {docMeta(key)}
          </div>
        </div>

        {phase === 'loading' && (
          <div className="lg-sk" aria-hidden="true">
            {[68, 92, 80, 88, 54].map((w, i) => <div key={i} className="lg-sk-bar" style={{ width: `${w}%` }} />)}
          </div>
        )}
        {phase === 'error' && (
          <div className="lg-doc-error" role="alert">
            <span className="material-icons" aria-hidden="true">cloud_off</span>
            <div className="lg-doc-error-txt">We couldn’t load this document. You can’t accept until it loads — please try again.</div>
            <Button variant="outlined" icon="refresh" onClick={() => { setPhase('loading'); setTimeout(() => setPhase('ready'), 900); }}>Retry</Button>
          </div>
        )}
        {phase === 'ready' && <LegalScrollDoc text={docText(key)} prose={key === 'TermsOfService'} labelledBy={titleId}
          onEnd={() => setReviewed((r) => (r[key] ? r : { ...r, [key]: true }))} />}

        <div className="lg-doc-actions">
          {done ? (
            <span className="lg-accepted"><span className="material-icons" aria-hidden="true">check_circle</span>Accepted</span>
          ) : (
            <>
              <span className="lg-hint">
                <span className="material-icons" aria-hidden="true">{reviewed[key] ? 'check_circle' : 'south'}</span>
                {reviewed[key] ? 'You’ve reached the end' : 'Scroll to the end to accept'}
              </span>
              <Button variant="text" disabled={phase !== 'ready' || !!busy} onClick={() => setConfirmDecline(key)}>Decline</Button>
              <Button variant="filled" icon="check" loading={busy === key}
                disabled={phase !== 'ready' || !reviewed[key] || (!!busy && busy !== key)} onClick={() => accept(key)}>Accept</Button>
            </>
          )}
        </div>
      </div>
    );
  };

  // ---- Terminal outcomes ----
  const renderOutcome = () => {
    if (outcome === 'accepted') {
      return (
        <div className="lg-result" role="status">
          <div className="lg-result-ic ok"><span className="material-icons" aria-hidden="true">check_circle</span></div>
          <div className="lg-result-ttl">You’re all set</div>
          <div className="lg-result-desc">Your acceptance has been recorded. Taking you back to {returnLabel}.</div>
          <div className="lg-redirect"><span className="lg-spin" aria-hidden="true" />Redirecting…</div>
        </div>
      );
    }
    return (
      <div className="lg-result" role="status">
        <div className="lg-result-ic stop"><span className="material-icons" aria-hidden="true">logout</span></div>
        <div className="lg-result-ttl">You declined the terms</div>
        <div className="lg-result-desc">You’ve been signed out. Your account isn’t locked — you can sign in again and respond differently whenever you’re ready.</div>
        <div className="lg-result-actions">
          <Button variant="filled" icon="login" onClick={() => resetTo(() => {})}>Back to sign in</Button>
        </div>
        <div className="lg-redirect"><span className="lg-spin" aria-hidden="true" />Returning to /login?reason=legal-declined</div>
      </div>
    );
  };

  return (
    <div className="lg-shell">
      <div className="lg-card">
        <ATReviewBar scope={scope} phase={phase} outcome={outcome}
          onScope={(s) => resetTo(() => setScope(s))}
          onPhase={(p) => resetTo(() => setPhase(p))}
          onOutcome={(o) => resetTo(() => setOutcome(o))} />

        {outcome ? renderOutcome() : (
          <>
            <div className="lg-brand"><BrandMark size={60} /></div>
            <div className="lg-head">
              <h1 className="lg-title" tabIndex={-1} ref={headingRef}>Review and accept to continue</h1>
              <p className="lg-sub">
                {docs.length > 1
                  ? 'Two documents govern your account and need your acceptance before you continue.'
                  : 'One document governing your account needs your acceptance before you continue.'}
                {' '}You likely reviewed {docs.length > 1 ? 'these' : 'this'} at sign-up — this is the recorded step.
              </p>
            </div>

            <ol className="lg-stepper" aria-label={`Step ${Math.min(doneCount + 1, docs.length)} of ${docs.length}`}>
              {docs.map((key, i) => {
                const done = isDone(key);
                const current = !done && docs.slice(0, i).every(isDone);
                const state = done ? 'done' : current ? 'current' : 'upcoming';
                const stepState = done ? (skipped[key] ? 'Acknowledged' : 'Accepted')
                  : current ? (isAvailable(key) === false ? 'Continue' : 'Review now') : 'Up next';
                return (
                  <li key={key} className={`lg-step ${state}`} aria-current={current ? 'step' : undefined}>
                    <span className="lg-step-dot" aria-hidden="true">
                      {done ? <span className="material-icons">{skipped[key] ? 'remove' : 'check'}</span> : i + 1}
                    </span>
                    <span className="lg-step-body">
                      <span className="lg-step-label">{AT_DOC_META[key].title}</span>
                      <span className="lg-step-state">{stepState}</span>
                    </span>
                    {i < docs.length - 1 && <span className={`lg-step-line${done ? ' filled' : ''}`} aria-hidden="true" />}
                  </li>
                );
              })}
            </ol>

            {currentKey && renderDoc(currentKey)}

            <div className="lg-foot">
              <span className="material-icons" aria-hidden="true">lock</span>
              <span>Your response is recorded with a timestamp against the exact text shown above.</span>
            </div>
          </>
        )}
      </div>

      {confirmDecline && (
        <Modal
          title="Decline and sign out?"
          icon="logout" iconTone="warning"
          onClose={() => setConfirmDecline(null)}
          footer={(
            <>
              <Button variant="text" onClick={() => setConfirmDecline(null)}>Keep reviewing</Button>
              <Button variant="danger" icon="logout" onClick={doDecline}>Decline and sign out</Button>
            </>
          )}>
          <p style={{ margin: 0, font: '400 14px/1.6 var(--font-sans)', color: 'var(--mud-palette-text-secondary)' }}>
            Declining the {confirmDecline === 'License' ? 'software license' : 'Terms of Service'} signs you out of Odyssey. Your account won’t be locked — you can sign in again and accept whenever you’re ready.
          </p>
        </Modal>
      )}
    </div>
  );
}

// Design-review-only state switcher (not shipped with the page).
const ATReviewBar = ({ scope, phase, outcome, onScope, onPhase, onOutcome }) => {
  const seg = (opts, cur, on) => (
    <div className="ss-seg" role="group">
      {opts.map((o) => (
        <button key={o.id} type="button" className={`ss-seg-btn${cur === o.id ? ' active' : ''}`}
          aria-pressed={cur === o.id} onClick={() => on(o.id)}>{o.label}</button>
      ))}
    </div>
  );
  return (
    <div className="ss-review" style={{ marginBottom: 24 }}>
      <span className="ss-review-lbl"><MIcon name="visibility" size={15} /> Preview state</span>
      {seg([
        { id: 'both', label: 'License + ToS' },
        { id: 'license', label: 'License only' },
        { id: 'tos', label: 'ToS only' },
        { id: 'none-pub', label: 'No ToS published' },
      ], scope, onScope)}
      <span className="ss-seg-sep" />
      {seg([
        { id: 'ready', label: 'Loaded' },
        { id: 'loading', label: 'Loading' },
        { id: 'error', label: 'Load error' },
      ], phase, onPhase)}
      <span className="ss-seg-sep" />
      {seg([
        { id: 'declined', label: 'Declined outcome' },
      ], outcome === 'declined' ? 'declined' : '', (id) => onOutcome(outcome === 'declined' ? null : id))}
    </div>
  );
};

Object.assign(window, { AcceptTerms });
