/* ImportJournalEntriesModal — the VJOURNAL (.ics) import dialog (spec §3 / §5 / §9 / §11).
   ----------------------------------------------------------------------------
   Opened from the Journal page-header ⋯ (ActionMenu) "Import" item — visible
   only when the caller holds BOTH journal.create AND journal.update. Sibling of
   ImportTasksModal / ContactImportModal; reuses the shared cvi-* result
   styling (contacts.css).

   Four states (spec §3):
     • compose  — a single .ics picker (DS FileUpload, multiple=false /
                  showKinds=false), PLUS a persistent destructive-replace warning
                  (spec §3 state #4 / §9): a UID-matched update REPLACES an
                  entry's tags, linked contacts, and attachments/photos — it does
                  not merge them. Import disabled until a file is chosen.
     • in-flight— submit disabled, spinner + progress copy (state #5).
     • rejected — envelope-level failure (bad extension/content-type, over the
                  size/count cap, unparseable file) shown INLINE, role=alert,
                  before any block is applied (state #7).
     • result   — the JournalEntryIcsImportResult: created / updated as plain
                  counts, SKIPPED grouped by reason (up to 100 sample titles),
                  plus the FOUR link-level skip counts as a secondary stat line
                  (SkippedTagLinkCount / SkippedContactLinkCount /
                  SkippedAttachmentCount / SkippedPhotoCount, spec §6/§3 #6),
                  shown only when nonzero. "Done" refreshes the list.

   onImport(file) → { result } | { rejected }. */

const VJ_SAMPLE_CAP = 100;

const JournalSkipGroup = ({ group }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const shown = (group.sampleTitles || []).slice(0, VJ_SAMPLE_CAP);
  const overflow = group.count - shown.length;
  return (
    <div className={`cvi-skip${open ? ' open' : ''}`}>
      <button type="button" className="cvi-skip-head" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <MIcon name="chevron_right" size={18} className="cvi-skip-chev" />
        <span className="cvi-skip-reason">{group.reason}</span>
        <span className="cvi-skip-count">{group.count}</span>
      </button>
      {open ? (
        <ul className="cvi-skip-list">
          {shown.map((t, i) => (
            <li key={i} className="cvi-skip-item"><MIcon name="menu_book" size={14} /><span>{t || 'Untitled entry'}</span></li>
          ))}
          {overflow > 0 ? <li className="cvi-skip-more">+{overflow} more</li> : null}
        </ul>
      ) : null}
    </div>
  );
};

const ImportJournalEntriesModal = ({ onClose, onImport }) => {
  const { useState, useEffect, useRef } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const Spinner = DS.Spinner;
  const [files, setFiles] = useState([]);
  const [errors, setErrors] = useState({});
  const [rejected, setRejected] = useState(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);
  const [announce, setAnnounce] = useState('');
  const resultRef = useRef(null);
  const rejectRef = useRef(null);

  const file = files[0];

  useEffect(() => { if (result && resultRef.current) resultRef.current.focus(); }, [result]);
  useEffect(() => { if (rejected && rejectRef.current) rejectRef.current.focus(); }, [rejected]);

  const submit = () => {
    if (busy) return;
    const e = {};
    if (!file) e.file = 'Choose a .ics file to import.';
    else if (!/\.ics$/i.test(file.name)) e.file = 'That isn’t a .ics file. iCalendar files use the .ics extension.';
    else if ((file.sizeBytes || 0) > getImportLimitMb('journal') * 1024 * 1024) e.file = `That file is larger than the ${getImportLimitMb('journal')} MB limit.`;
    if (Object.keys(e).length) { setErrors(e); return; }
    setErrors({}); setRejected(null); setBusy(true);
    setTimeout(() => {
      const res = onImport ? onImport(file) : null;
      setBusy(false);
      if (!res) { onClose && onClose(); return; }
      if (res.rejected) { setRejected(res.rejected); return; }
      const r = res.result;
      setResult(r);
      const skippedTotal = r.skipped.reduce((s, g) => s + g.count, 0);
      setAnnounce(`${r.importedCount} imported, ${r.updatedCount} updated, ${skippedTotal} skipped.`);
    }, 950);
  };

  const reset = () => { setResult(null); setFiles([]); setErrors({}); setRejected(null); setAnnounce(''); };

  const skippedTotal = result ? result.skipped.reduce((s, g) => s + g.count, 0) : 0;
  // The four link-level skip tallies, rendered as a secondary stat line (§3 #6).
  const linkSkips = result ? [
    { label: result.skippedTagLinkCount === 1 ? 'tag link' : 'tag links', n: result.skippedTagLinkCount || 0 },
    { label: result.skippedContactLinkCount === 1 ? 'contact link' : 'contact links', n: result.skippedContactLinkCount || 0 },
    { label: result.skippedAttachmentCount === 1 ? 'attachment' : 'attachments', n: result.skippedAttachmentCount || 0 },
    { label: result.skippedPhotoCount === 1 ? 'photo' : 'photos', n: result.skippedPhotoCount || 0 },
  ].filter((x) => x.n > 0) : [];

  const composeFooter = (
    <React.Fragment>
      <Button variant="text" onClick={onClose}>Cancel</Button>
      <Button variant="filled" color="primary" icon="upload_file" loading={busy} disabled={!file} onClick={submit}>Import</Button>
    </React.Fragment>
  );
  const resultFooter = (
    <React.Fragment>
      <Button variant="text" icon="restart_alt" onClick={reset}>Import another file</Button>
      <span style={{ flex: 1 }} />
      <Button variant="filled" color="primary" icon="check" onClick={onClose}>Done</Button>
    </React.Fragment>
  );

  return (
    <Modal
      title="Import journal entries"
      subtitle={result ? undefined : 'Add entries from an iCalendar (.ics) file of VJOURNAL items — from Odyssey or any VJOURNAL-capable tool. Entries whose UID matches an existing entry are updated in place; the rest are created. Invalid entries are skipped with a reason.'}
      icon="upload_file"
      className="cvi-dialog"
      onClose={busy ? undefined : onClose}
      footer={result ? resultFooter : composeFooter}>

      <div className="sr-only" role="status" aria-live="polite">{announce}</div>

      {result ? (
        <div className="cvi-result">
          <h3 className="cvi-result-h" tabIndex={-1} ref={resultRef}>
            Imported from <b>{file ? file.name : 'file'}</b>
          </h3>

          <div className="cvi-stats">
            <div className="cvi-stat ok">
              <MIcon name="note_add" size={20} />
              <div className="cvi-stat-body"><span className="cvi-stat-n">{result.importedCount}</span><span className="cvi-stat-l">imported</span></div>
            </div>
            <div className="cvi-stat upd">
              <MIcon name="sync" size={20} />
              <div className="cvi-stat-body"><span className="cvi-stat-n">{result.updatedCount}</span><span className="cvi-stat-l">updated</span></div>
            </div>
            <div className={`cvi-stat${skippedTotal ? ' skip' : ''}`}>
              <MIcon name={skippedTotal ? 'rule' : 'check_circle'} size={20} />
              <div className="cvi-stat-body"><span className="cvi-stat-n">{skippedTotal}</span><span className="cvi-stat-l">skipped</span></div>
            </div>
          </div>

          {skippedTotal ? (
            <div className="cvi-skips">
              <div className="cvi-skips-head">Skipped entries, by reason</div>
              {result.skipped.map((g, i) => <JournalSkipGroup key={i} group={g} />)}
            </div>
          ) : (
            <div className="cvi-clean"><MIcon name="task_alt" size={18} />Every entry in the file imported cleanly.</div>
          )}

          {linkSkips.length ? (
            <div className="cvi-note">
              <MIcon name="link_off" size={16} />
              <span>
                {linkSkips.map((x, i) => (
                  <React.Fragment key={x.label}>
                    {i > 0 ? (i === linkSkips.length - 1 ? ', and ' : ', ') : ''}
                    <b>{x.n}</b> {x.label}
                  </React.Fragment>
                ))}
                {' '}couldn’t be resolved and {linkSkips.length === 1 && linkSkips[0].n === 1 ? 'was' : 'were'} left off — the entries themselves imported. See reasons above.
              </span>
            </div>
          ) : null}
        </div>
      ) : busy ? (
        <div className="cvi-busy" role="status" aria-live="polite">
          {Spinner ? <Spinner size="lg" /> : null}
          <div>
            <div className="cvi-busy-l">Importing entries…</div>
            <div className="cvi-busy-s">Matching entries by UID and validating each VJOURNAL.</div>
          </div>
        </div>
      ) : (
        <div className="cvi-form">
          {rejected ? <div ref={rejectRef} tabIndex={-1} role="alert" style={{ outline: 'none' }}><Alert severity="error">{rejected}</Alert></div> : null}
          <Alert severity="warning">Importing a file that matches an existing entry <b>replaces</b> its tags, linked contacts, and attachments and photos — it does not merge them.</Alert>
          <FieldShell label="iCalendar file" error={errors.file}>
            <FileUpload
              files={files}
              onChange={(next) => { setFiles(next); if (errors.file) setErrors((p) => ({ ...p, file: undefined })); if (rejected) setRejected(null); }}
              multiple={false}
              showKinds={false}
              accept=".ics,text/calendar"
              compact
              hint={`iCalendar (.ics) · one file · up to ${getImportLimitMb('journal')}\u00a0MB`}
            />
          </FieldShell>
          <div className="cvi-note">
            <MIcon name="info" size={16} />
            <span>Re-importing a file you exported from Odyssey updates the same entries in place instead of duplicating them, matched on each VJOURNAL’s UID.</span>
          </div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ImportJournalEntriesModal });
