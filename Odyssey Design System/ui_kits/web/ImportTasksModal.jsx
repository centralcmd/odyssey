/* ImportTasksModal — the .ics VTODO import dialog (spec §3 / §4 / §7 / §11).
   ----------------------------------------------------------------------------
   Opened from the Tasks page-header "Import" action (visible only when the
   caller holds BOTH tasks.create AND tasks.update). A near-direct sibling of
   ContactImportModal / ImportCalendarModal — reuses the shared cvi-*
   result styling (contacts.css).

   Four states: compose (single .ics picker) · in-flight (spinner) · rejected
   (envelope-level failure, inline) · result (Imported / Updated / Skipped
   counts, skip groups by reason with sample titles, plus the two soft-skip
   tallies unique to this feature — unresolved CATEGORIES tag links and
   odyssey-file: ATTACH references, §6).

   onImport(file) → { result } | { rejected }. */

const ICS_SAMPLE_CAP = 100;

const TaskSkipGroup = ({ group }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const shown = (group.sampleTitles || []).slice(0, ICS_SAMPLE_CAP);
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
            <li key={i} className="cvi-skip-item"><MIcon name="playlist_remove" size={14} /><span>{t || 'Untitled task'}</span></li>
          ))}
          {overflow > 0 ? <li className="cvi-skip-more">+{overflow} more</li> : null}
        </ul>
      ) : null}
    </div>
  );
};

const ImportTasksModal = ({ onClose, onImport }) => {
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

  const file = files[0];

  useEffect(() => { if (result && resultRef.current) resultRef.current.focus(); }, [result]);

  const submit = () => {
    if (busy) return;
    const e = {};
    if (!file) e.file = 'Choose a .ics file to import.';
    else if (!/\.ics$/i.test(file.name)) e.file = 'That isn’t a .ics file. iCalendar files use the .ics extension.';
    else if ((file.sizeBytes || 0) > getImportLimitMb('tasks') * 1024 * 1024) e.file = `That file is larger than the ${getImportLimitMb('tasks')} MB limit.`;
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
  const softSkips = result ? (result.skippedTagLinkCount || 0) + (result.skippedAttachmentCount || 0) : 0;

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
      title="Import tasks"
      subtitle={result ? undefined : 'Add tasks from an iCalendar (.ics) file of VTODO items — from Odyssey or any VTODO-capable app. Entries whose UID matches an existing task are updated in place; the rest are created. Recurring or invalid entries are skipped with a reason.'}
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
              <MIcon name="add_task" size={20} />
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
              <div className="cvi-skips-head">Skipped tasks, by reason</div>
              {result.skipped.map((g, i) => <TaskSkipGroup key={i} group={g} />)}
            </div>
          ) : (
            <div className="cvi-clean"><MIcon name="task_alt" size={18} />Every task in the file imported cleanly.</div>
          )}

          {softSkips ? (
            <div className="cvi-note">
              <MIcon name="link_off" size={16} />
              <span>
                {result.skippedTagLinkCount ? `${result.skippedTagLinkCount} tag ${result.skippedTagLinkCount === 1 ? 'name' : 'names'} didn’t match a board tag` : ''}
                {result.skippedTagLinkCount && result.skippedAttachmentCount ? ' and ' : ''}
                {result.skippedAttachmentCount ? `${result.skippedAttachmentCount} attachment ${result.skippedAttachmentCount === 1 ? 'reference' : 'references'} couldn’t be resolved` : ''}
                {' '}— left off, but the tasks themselves imported.
              </span>
            </div>
          ) : null}
        </div>
      ) : busy ? (
        <div className="cvi-busy" role="status" aria-live="polite">
          {Spinner ? <Spinner size="lg" /> : null}
          <div>
            <div className="cvi-busy-l">Importing tasks…</div>
            <div className="cvi-busy-s">Matching entries by UID and validating each VTODO.</div>
          </div>
        </div>
      ) : (
        <div className="cvi-form">
          {rejected ? <Alert severity="error">{rejected}</Alert> : null}
          <FieldShell label="iCalendar file" error={errors.file}>
            <FileUpload
              files={files}
              onChange={(next) => { setFiles(next); if (errors.file) setErrors((p) => ({ ...p, file: undefined })); if (rejected) setRejected(null); }}
              multiple={false}
              showKinds={false}
              accept=".ics,text/calendar"
              compact
              hint={`iCalendar (.ics) · one file · up to ${getImportLimitMb('tasks')}\u00a0MB`}
            />
          </FieldShell>
          <div className="cvi-note">
            <MIcon name="info" size={16} />
            <span>Re-importing a file you exported from Odyssey updates the same tasks in place instead of duplicating them, matched on each VTODO’s UID.</span>
          </div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ImportTasksModal });
