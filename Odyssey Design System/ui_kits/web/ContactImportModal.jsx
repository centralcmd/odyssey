/* ContactImportModal — the .vcf import dialog (spec §3 / §4 / §7 / §11).
   ----------------------------------------------------------------------------
   Opened from the Contacts page header "Import" action (visible only when
   the caller holds BOTH contacts.create AND contacts.update).

   One shell, four states (spec §3):
     • compose  — a single-file .vcf picker (DS FileUpload, multiple=false /
                  showKinds=false), Import disabled until a file is chosen.
     • in-flight— submit disabled, a spinner + progress copy (state #5).
     • rejected — an envelope-level failure (bad extension/content-type, over the
                  size/count cap, unparseable file) shown INLINE in compose,
                  before any row is applied (state #7).
     • result   — the VCardImportResult summary: created / updated as plain
                  counts, the SKIPPED list grouped by reason and expandable to a
                  capped list of sample display names (mirrors
                  IcsImportSkipGroup.SampleTitles). "Done" refreshes the list.

   onImport(file) → { result } | { rejected } — the page runs the simulated
   parse (create/update by UID match) and refreshes its rows on { result }. */

const VCF_SAMPLE_CAP = 100;

/* One reason group in the skipped list — expandable to its sample names. */
const VCardSkipGroup = ({ group }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const shown = (group.sampleNames || []).slice(0, VCF_SAMPLE_CAP);
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
            <li key={i} className="cvi-skip-item"><MIcon name="person_off" size={14} /><span>{t || 'Unnamed contact'}</span></li>
          ))}
          {overflow > 0 ? <li className="cvi-skip-more">+{overflow} more</li> : null}
        </ul>
      ) : null}
    </div>
  );
};

const ContactImportModal = ({ onClose, onImport }) => {
  const { useState, useEffect, useRef } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const Spinner = DS.Spinner;
  const [files, setFiles] = useState([]);        // single-file (multiple=false)
  const [errors, setErrors] = useState({});      // field-level (file picker)
  const [rejected, setRejected] = useState(null); // envelope-level message
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);
  const [announce, setAnnounce] = useState('');
  const resultRef = useRef(null);

  const file = files[0];

  // After completion, move focus to the result summary (spec §3).
  useEffect(() => { if (result && resultRef.current) resultRef.current.focus(); }, [result]);

  const submit = () => {
    if (busy) return;
    const e = {};
    if (!file) e.file = 'Choose a .vcf file to import.';
    else if (!/\.vcf$/i.test(file.name)) e.file = 'That isn’t a .vcf file. vCard files use the .vcf extension.';
    else if ((file.sizeBytes || 0) > getImportLimitMb('contacts') * 1024 * 1024) e.file = `That file is larger than the ${getImportLimitMb('contacts')} MB limit.`;
    if (Object.keys(e).length) { setErrors(e); return; }
    setErrors({}); setRejected(null); setBusy(true);
    // Simulated in-flight pass (state #5) — processed synchronously server-side.
    setTimeout(() => {
      const res = onImport ? onImport(file) : null;
      setBusy(false);
      if (!res) { onClose && onClose(); return; }
      if (res.rejected) { setRejected(res.rejected); return; }
      const r = res.result;
      setResult(r);
      const skippedTotal = r.skipped.reduce((s, g) => s + g.count, 0);
      setAnnounce(`${r.createdCount} created, ${r.updatedCount} updated, ${skippedTotal} skipped.`);
    }, 950);
  };

  const reset = () => { setResult(null); setFiles([]); setErrors({}); setRejected(null); setAnnounce(''); };

  const skippedTotal = result ? result.skipped.reduce((s, g) => s + g.count, 0) : 0;

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
      title="Import contacts"
      subtitle={result ? undefined : 'Add people and organizations from a vCard (.vcf) file. Entries whose UID matches an existing contact are updated in place; the rest are created. Anything invalid is skipped with a reason — the rest of the file still imports.'}
      icon="upload_file"
      className="cvi-dialog"
      onClose={busy ? undefined : onClose}
      footer={result ? resultFooter : composeFooter}>

      {/* Live region — mounted empty, populated only on completion (spec §3). */}
      <div className="sr-only" role="status" aria-live="polite">{announce}</div>

      {result ? (
        <div className="cvi-result">
          <h3 className="cvi-result-h" tabIndex={-1} ref={resultRef}>
            Imported from <b>{file ? file.name : 'file'}</b>
          </h3>

          <div className="cvi-stats">
            <div className="cvi-stat ok">
              <MIcon name="person_add" size={20} />
              <div className="cvi-stat-body"><span className="cvi-stat-n">{result.createdCount}</span><span className="cvi-stat-l">created</span></div>
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
              {result.skipped.map((g, i) => <VCardSkipGroup key={i} group={g} />)}
            </div>
          ) : (
            <div className="cvi-clean"><MIcon name="task_alt" size={18} />Every contact in the file imported cleanly.</div>
          )}
        </div>
      ) : busy ? (
        <div className="cvi-busy" role="status" aria-live="polite">
          {Spinner ? <Spinner size="lg" /> : null}
          <div>
            <div className="cvi-busy-l">Importing contacts…</div>
            <div className="cvi-busy-s">Matching entries by UID and validating each field.</div>
          </div>
        </div>
      ) : (
        <div className="cvi-form">
          {rejected ? <Alert severity="error">{rejected}</Alert> : null}
          <FieldShell label="vCard file" error={errors.file}>
            <FileUpload
              files={files}
              onChange={(next) => { setFiles(next); if (errors.file) setErrors((p) => ({ ...p, file: undefined })); if (rejected) setRejected(null); }}
              multiple={false}
              showKinds={false}
              accept=".vcf,text/vcard"
              compact
              hint={`vCard (.vcf) · one file · up to ${getImportLimitMb('contacts')}\u00a0MB`}
            />
          </FieldShell>
          <div className="cvi-note">
            <MIcon name="info" size={16} />
            <span>Re-importing a file you exported from Odyssey updates the same contacts in place instead of duplicating them, matched on each entry’s vCard UID.</span>
          </div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ContactImportModal });
