/* =============================================================
   LegalDocuments.jsx — the admin "Legal Documents" bespoke panel (spec §3
   state 5, §7.5–§7.7). Lives inside the existing /settings page, gated by the
   UsersManage permission claim — but explicitly NOT a SettingItem row in that
   page's one-control-per-row grid (which has no way to host a 50,000-char
   editor or a version table). It is its own panel.

   Contains:
     • a plain-text editor for the current ToS content, character counter
       reflecting the true 50,000 cap, and an "Editing on top of version N" tag
       (or a "No version published yet" empty state);
     • a dedicated "Publish new version" action with a confirmation dialog
       warning that publishing forces every user (including this admin) to
       re-accept within the standard bounded window — distinct from the page's
       existing page-wide "Save changes" stub;
     • a read-only version-history table (published date, publisher display name
       from the API's publishedByDisplayName; null → "deleted user"), with an
       on-demand fetch to view a historical version's full text.

   Entering the panel first checks the admin's own compliance status, routing
   them through /accept-terms first if outstanding (the own-compliance precheck).

   The dashed "Preview state" bar is a design-review aid (mirrors SystemSettings)
   and is NOT shipped.
   ============================================================= */

const TOS_MAX = 50000;

const lgFmtDate = (iso) => {
  const d = new Date(iso);
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
    + ', ' + d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
};

function LegalDocuments({ onGoAcceptTerms }) {
  const { useState, useMemo, useRef, useEffect } = React;
  const L = window.OdysseyLegal || {};

  // ---- Review controls (design aid; not shipped) ----
  const [compliance, setCompliance] = useState('compliant'); // compliant | outstanding
  const [content, setContent] = useState('published');       // published | none-pub
  const [phase, setPhase] = useState('ready');               // ready | loading | error

  // ---- Panel state ----
  const versions = content === 'none-pub' ? [] : (L.tosVersions || []);
  const current = versions[0] || null;
  const [draft, setDraft] = useState('');
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [justPublished, setJustPublished] = useState(false);
  const [viewId, setViewId] = useState(null);       // historical version being viewed
  const [viewLoading, setViewLoading] = useState(false);
  const [copied, setCopied] = useState(false);
  const [announce, setAnnounce] = useState('');

  // Seed the editor from the current version whenever the content scope flips.
  useEffect(() => { setDraft(current ? current.content : ''); setJustPublished(false); }, [content]);

  const trimmedLen = draft.trim().length;
  const overCap = draft.length > TOS_MAX;
  const emptyContent = trimmedLen === 0;
  const dirty = current ? draft !== current.content : draft.length > 0;
  const canPublish = phase === 'ready' && !emptyContent && !overCap && dirty && !publishing;

  const editorError = overCap
    ? `Content exceeds the ${TOS_MAX.toLocaleString()}-character limit by ${(draft.length - TOS_MAX).toLocaleString()}.`
    : undefined;

  const openView = (id) => {
    setViewId(id); setViewLoading(true); setCopied(false);
    // GET /api/legal/terms-of-service/versions/{id} — on-demand full-text fetch.
    setTimeout(() => setViewLoading(false), 520);
  };
  const viewedVersion = versions.find((v) => v.id === viewId) || null;

  const doPublish = () => {
    setConfirmOpen(false);
    setPublishing(true);
    // POST /api/legal/terms-of-service/versions { content } → 201 Created
    setTimeout(() => {
      setPublishing(false); setJustPublished(true);
      setAnnounce('New Terms of Service version published. Every user will be asked to re-accept.');
      setTimeout(() => setJustPublished(false), 2600);
    }, 1000);
  };

  // ---- Own-compliance precheck: a non-compliant admin is routed through
  //      /accept-terms before they can reach this panel (spec §3 state 5). ----
  if (compliance === 'outstanding') {
    return (
      <div className="col gap-6">
        <LDReviewBar compliance={compliance} content={content} phase={phase}
          onCompliance={setCompliance} onContent={setContent} onPhase={setPhase} />
        <PageHeader title="Terms of Service" icon="gavel" card
          sub="Author and publish the Terms of Service, and review version history" />
        <div className="lg-admin-blocker" role="alert">
          <span className="material-icons" aria-hidden="true">assignment_late</span>
          <div>
            <div className="lg-admin-blocker-ttl">Accept the current terms before managing them</div>
            <div className="lg-admin-blocker-desc">
              You have an outstanding License or Terms of Service acceptance. Publishing forces everyone to re-accept, so you must be compliant yourself first. We’ll bring you right back here afterward.
            </div>
            <div className="lg-admin-blocker-act">
              <Button variant="filled" icon="arrow_forward" onClick={() => onGoAcceptTerms && onGoAcceptTerms()}>Review terms</Button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  const publishBtn = (
    <Button variant="filled" icon={justPublished ? 'check' : 'publish'} loading={publishing}
      disabled={!canPublish} onClick={() => setConfirmOpen(true)}>
      {justPublished ? 'Published' : 'Publish new version'}
    </Button>
  );

  return (
    <div className="col gap-6">
      <LDReviewBar compliance={compliance} content={content} phase={phase}
        onCompliance={setCompliance} onContent={setContent} onPhase={setPhase} />

      <PageHeader title="Terms of Service" icon="gavel" card
        sub="Author and publish the Terms of Service, and review version history"
        primary={publishBtn} />

      {phase === 'error' ? (
        <div className="ss-retry" role="alert">
          <span className="material-icons" aria-hidden="true">cloud_off</span>
          <div className="ss-retry-ttl">Couldn’t load legal documents</div>
          <div className="ss-retry-desc">Something went wrong reading the Terms of Service and its history.</div>
          <Button variant="outlined" icon="refresh" onClick={() => { setPhase('loading'); setTimeout(() => setPhase('ready'), 900); }}>Retry</Button>
        </div>
      ) : (
        <>
          {/* ---- Editor section ---- */}
          <section className="set-section" aria-labelledby="lg-ed-h">
            <div className="set-section-head">
              <span className="set-section-ic"><MIcon name="edit_document" size={17} /></span>
              <h2 className="set-section-title" id="lg-ed-h">Terms of Service</h2>
              <span className="set-section-rule" />
            </div>

            <Card outlined>
              <div className="card-body lg-admin-card">
                {content === 'none-pub' ? (
                  <>
                    <div className="lg-notpub" role="note" style={{ marginBottom: 16 }}>
                      <span className="material-icons" aria-hidden="true">description</span>
                      <div className="lg-notpub-txt"><b>No version published yet.</b> Write the first Terms of Service below and publish it. Until then, users are asked to accept only the software license.</div>
                    </div>
                    <p className="lg-editor-note">Publishing the first version asks every user to accept it at their next sign-in or within the standard bounded window for active sessions.</p>
                  </>
                ) : (
                  <div className="lg-editor-head">
                    <span className="lg-current-tag"><span className="material-icons" aria-hidden="true">bookmark</span>Editing on top of version {current.id}</span>
                    <span className="muted" style={{ font: '400 12px/1 var(--font-sans)' }}>Published {lgFmtDate(current.publishedAt)}</span>
                  </div>
                )}

                {phase === 'loading' ? (
                  <div className="lg-sk" style={{ padding: '20px 0' }} aria-hidden="true">
                    {[96, 90, 94, 70, 88, 60].map((w, i) => <div key={i} className="lg-sk-bar" style={{ width: `${w}%` }} />)}
                  </div>
                ) : (
                  <NoteField
                    label="Terms of Service content"
                    value={draft} onChange={setDraft}
                    rows={26} maxLength={TOS_MAX} showCount
                    placeholder="Write the Terms of Service in plain text. Plain-text only — no Markdown or rich formatting in this version."
                    error={editorError}
                    help={editorError ? undefined : 'Plain text. Published exactly as written; every prior version is retained.'} />
                )}

                <div className="lg-editor-note" style={{ marginTop: 14, marginBottom: 0 }}>
                  <b>Publishing is not the same as the page-wide Save.</b> It creates a new immutable version and requires every user — including you — to re-accept within the standard bounded window.
                </div>
              </div>
            </Card>
          </section>

          {/* ---- Version history section ---- */}
          <section className="set-section" aria-labelledby="lg-hist-h">
            <div className="set-section-head">
              <span className="set-section-ic"><MIcon name="history" size={17} /></span>
              <h2 className="set-section-title" id="lg-hist-h">Version history</h2>
              <span className="set-section-rule" />
            </div>

            {versions.length === 0 ? (
              <EmptyState icon="history_toggle_off" mutedIcon
                title="No versions yet"
                desc="Once you publish a Terms of Service, every version stays here permanently — publishing never deletes or edits a prior one." />
            ) : (
              <div className="lg-tbl-wrap">
                <table className="tbl lg-vtbl">
                  <thead>
                    <tr>
                      <th style={{ width: 120 }}>Version</th>
                      <th>Published</th>
                      <th>Published by</th>
                      <th className="numeric" style={{ width: 96 }}>Full text</th>
                    </tr>
                  </thead>
                  <tbody>
                    {versions.map((v, i) => (
                      <tr key={v.id} onClick={() => openView(v.id)}>
                        <td>
                          <span className="lg-ver-id">v{v.id}</span>
                          {i === 0 && <span className="lg-ver-cur">Current</span>}
                        </td>
                        <td className="lg-ver-when">{lgFmtDate(v.publishedAt)}</td>
                        <td>
                          {v.publishedByDisplayName
                            ? <span className="lg-ver-by">{v.publishedByDisplayName}</span>
                            : <span className="lg-ver-by deleted">deleted user</span>}
                        </td>
                        <td className="lg-ver-act">
                          <Button variant="text" icon="visibility" onClick={(e) => { e && e.stopPropagation && e.stopPropagation(); openView(v.id); }}>View</Button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}

      {/* ---- Publish confirmation ---- */}
      {confirmOpen && (
        <Modal
          title="Publish a new Terms of Service?"
          subtitle={current ? `This becomes version ${current.id + 1} and the new current version.` : 'This becomes version 1 — the first published Terms of Service.'}
          icon="publish" iconTone="warning"
          onClose={() => setConfirmOpen(false)}
          footer={(
            <>
              <Button variant="text" onClick={() => setConfirmOpen(false)}>Cancel</Button>
              <Button variant="filled" icon="publish" onClick={doPublish}>Publish version</Button>
            </>
          )}>
          <p style={{ margin: '0 0 12px', font: '400 14px/1.6 var(--font-sans)', color: 'var(--mud-palette-text-secondary)' }}>
            Every user — new, existing, and currently active, <b style={{ color: 'var(--mud-palette-text-primary)', fontWeight: 500 }}>including you</b> — will be asked to re-accept. Active sessions are interrupted within the standard bounded window (at most 30 minutes); everyone else is gated at their next sign-in.
          </p>
          <p style={{ margin: 0, font: '400 14px/1.6 var(--font-sans)', color: 'var(--mud-palette-text-secondary)' }}>
            Prior versions and every recorded acceptance are kept unchanged.
          </p>
        </Modal>
      )}

      {/* ---- On-demand historical-version viewer ---- */}
      {viewId != null && (
        <Modal
          title={`Terms of Service — version ${viewId}`}
          subtitle={viewedVersion ? `Published ${lgFmtDate(viewedVersion.publishedAt)} · ${viewedVersion.publishedByDisplayName || 'deleted user'}` : undefined}
          icon="description"
          onClose={() => setViewId(null)}
          footer={(
            <>
              <Button variant="text" icon={copied ? 'check' : 'content_copy'}
                disabled={viewLoading || !viewedVersion}
                onClick={() => {
                  const t = viewedVersion ? viewedVersion.content : '';
                  const done = () => { setCopied(true); setTimeout(() => setCopied(false), 2000); };
                  if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(t).then(done).catch(done);
                  else done();
                }}>{copied ? 'Copied' : 'Copy text'}</Button>
              <Button variant="filled" onClick={() => setViewId(null)}>Close</Button>
            </>
          )}>
          {viewLoading ? (
            <div className="lg-ver-loading"><span className="lg-spin" aria-hidden="true" />Loading full text…</div>
          ) : (
            <div className="lg-ver-view" tabIndex={0}>{viewedVersion ? viewedVersion.content : ''}</div>
          )}
        </Modal>
      )}

      <div className="odc-visually-hidden" role="status" aria-live="polite">{announce}</div>
    </div>
  );
}

// Design-review-only state switcher (not shipped with the page).
const LDReviewBar = ({ compliance, content, phase, onCompliance, onContent, onPhase }) => {
  const seg = (opts, cur, on) => (
    <div className="ss-seg" role="group">
      {opts.map((o) => (
        <button key={o.id} type="button" className={`ss-seg-btn${cur === o.id ? ' active' : ''}`}
          aria-pressed={cur === o.id} onClick={() => on(o.id)}>{o.label}</button>
      ))}
    </div>
  );
  return (
    <div className="ss-review">
      <span className="ss-review-lbl"><MIcon name="visibility" size={15} /> Preview state</span>
      {seg([
        { id: 'compliant', label: 'Admin compliant' },
        { id: 'outstanding', label: 'Admin outstanding' },
      ], compliance, onCompliance)}
      <span className="ss-seg-sep" />
      {seg([
        { id: 'published', label: 'Has versions' },
        { id: 'none-pub', label: 'None published' },
      ], content, onContent)}
      <span className="ss-seg-sep" />
      {seg([
        { id: 'ready', label: 'Loaded' },
        { id: 'loading', label: 'Loading' },
        { id: 'error', label: 'Load error' },
      ], phase, onPhase)}
    </div>
  );
};

Object.assign(window, { LegalDocuments });
