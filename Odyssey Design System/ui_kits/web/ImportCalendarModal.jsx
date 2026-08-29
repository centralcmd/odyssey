/* ImportCalendarModal — the .ics import dialog (spec §3 / §7 / §11).
   ----------------------------------------------------------------------------
   Opened from the Manage Calendars dialog, which explicitly CLOSES itself first
   (mirrors CalendarEventDialog's "Edit series…" close-then-open sequencing).

   One shell, two phases:
     • compose — a required target-calendar Select + a single-file .ics picker
                 (the DS FileUpload with multiple=false / showKinds=false). The
                 picker's single-file behaviour relies on the FileUpload
                 `multiple=false` fix (a new pick replaces, never accumulates).
     • result  — the IcsImportResult summary. Imported / updated show as plain
                 counts (they succeeded, no per-row attention). The SKIPPED list
                 is grouped by reason, each group expandable to its event titles,
                 capped at 100 samples with a "+N more" note — bounded regardless
                 of the 2000-VEVENT file cap. A regenerate-future warning banner
                 shows whenever AnySeriesRegenerated is true. An aria-live region
                 announces the summary; focus moves to the result heading.

   onImport(calendarId, file) → IcsImportResult (the page runs the simulated
   parse in calendar-data.js and appends the imported rows). */

const ICS_SAMPLE_CAP = 100;

/* One reason group in the skipped list — expandable to its sample titles. */
const IcsSkipGroup = ({ group }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const shown = group.sampleTitles.slice(0, ICS_SAMPLE_CAP);
  const overflow = group.count - shown.length;
  return (
    <div className={`cal-imp-skip${open ? ' open' : ''}`}>
      <button type="button" className="cal-imp-skip-head" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <MIcon name="chevron_right" size={18} className="cal-imp-skip-chev" />
        <span className="cal-imp-skip-reason">{group.reason}</span>
        <span className="cal-imp-skip-count">{group.count}</span>
      </button>
      {open ? (
        <ul className="cal-imp-skip-list">
          {shown.map((t, i) => (
            <li key={i} className="cal-imp-skip-item"><MIcon name="event_busy" size={14} /><span>{t || 'Untitled event'}</span></li>
          ))}
          {overflow > 0 ? <li className="cal-imp-skip-more">+{overflow} more</li> : null}
        </ul>
      ) : null}
    </div>
  );
};

const ImportCalendarModal = ({ calendars = [], defaultCalendarId, onClose, onImport }) => {
  const { useState, useEffect, useRef } = React;
  const [calendarId, setCalendarId] = useState(defaultCalendarId || (calendars[0] && calendars[0].id) || '');
  const [files, setFiles] = useState([]); // single-file (multiple=false)
  const [errors, setErrors] = useState({});
  const [result, setResult] = useState(null);
  const [announce, setAnnounce] = useState('');
  const resultRef = useRef(null);

  const file = files[0];
  const cal = calendars.find((c) => c.id === calendarId) || {};
  const calOptions = calendars.map((c) => ({ value: c.id, label: c.name, icon: 'circle', iconColor: c.color }));

  // After completion, move focus to the result summary (spec §3).
  useEffect(() => { if (result && resultRef.current) resultRef.current.focus(); }, [result]);

  const submit = () => {
    const e = {};
    if (!calendarId) e.calendar = 'Choose which calendar to import into.';
    if (!file) e.file = 'Choose a .ics file to import.';
    else if ((file.sizeBytes || 0) > getImportLimitMb('calendar') * 1024 * 1024) e.file = `That file is larger than the ${getImportLimitMb('calendar')} MB limit.`;
    if (Object.keys(e).length) { setErrors(e); return; }
    const res = onImport ? onImport(calendarId, file) : null;
    if (!res) { onClose && onClose(); return; }
    setResult(res);
    const skippedTotal = res.skipped.reduce((s, g) => s + g.count, 0);
    setAnnounce(`${res.importedCount} events imported, ${res.updatedCount} updated, ${skippedTotal} skipped.`);
  };

  const reset = () => { setResult(null); setFiles([]); setErrors({}); setAnnounce(''); };

  const skippedTotal = result ? result.skipped.reduce((s, g) => s + g.count, 0) : 0;

  const composeFooter = (
    <React.Fragment>
      <Button variant="text" onClick={onClose}>Cancel</Button>
      <Button variant="filled" color="primary" icon="upload_file" onClick={submit}>Import</Button>
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
      title="Import calendar"
      subtitle={result ? undefined : 'Add events from an .ics (iCalendar) file into one of your calendars. Recurring series import as a repeating event where the rule maps cleanly; anything else is skipped with a reason.'}
      icon="upload_file"
      className="cal-imp-dialog"
      onClose={onClose}
      footer={result ? resultFooter : composeFooter}>

      {/* Live region — mounted empty, populated only on completion (spec §3). */}
      <div className="sr-only" role="status" aria-live="polite">{announce}</div>

      {!result ? (
        <div className="cal-imp-form">
          <Select
            label="Import into"
            value={calendarId}
            onChange={(v) => { setCalendarId(v); if (errors.calendar) setErrors((p) => ({ ...p, calendar: undefined })); }}
            options={calOptions}
            placeholder="Choose a calendar…"
            error={errors.calendar}
          />
          <FieldShell label="Calendar file" error={errors.file}>
            <FileUpload
              files={files}
              onChange={(next) => { setFiles(next); if (errors.file) setErrors((p) => ({ ...p, file: undefined })); }}
              multiple={false}
              showKinds={false}
              accept=".ics,text/calendar"
              compact
              hint={`iCalendar (.ics) · one file · up to ${getImportLimitMb('calendar')}\u00a0MB`}
            />
          </FieldShell>
          <div className="cal-imp-note">
            <MIcon name="info" size={16} />
            <span>Re-importing a file already imported here updates matching events in place instead of duplicating them, matched by their calendar UID.</span>
          </div>
        </div>
      ) : (
        <div className="cal-imp-result">
          <h3 className="cal-imp-result-h" tabIndex={-1} ref={resultRef}>
            Imported into <span className="cal-imp-result-cal"><span className="cal-imp-dot" style={{ background: cal.color }} aria-hidden="true" />{cal.name}</span>
          </h3>

          <div className="cal-imp-stats">
            <div className="cal-imp-stat ok">
              <MIcon name="event_available" size={20} />
              <div className="cal-imp-stat-body"><span className="cal-imp-stat-n">{result.importedCount}</span><span className="cal-imp-stat-l">imported</span></div>
            </div>
            <div className="cal-imp-stat upd">
              <MIcon name="sync" size={20} />
              <div className="cal-imp-stat-body"><span className="cal-imp-stat-n">{result.updatedCount}</span><span className="cal-imp-stat-l">updated</span></div>
            </div>
            <div className={`cal-imp-stat${skippedTotal ? ' skip' : ''}`}>
              <MIcon name={skippedTotal ? 'event_busy' : 'check_circle'} size={20} />
              <div className="cal-imp-stat-body"><span className="cal-imp-stat-n">{skippedTotal}</span><span className="cal-imp-stat-l">skipped</span></div>
            </div>
          </div>

          {result.anySeriesRegenerated ? (
            <Alert severity="warning">
              Updating a recurring series replaced its future occurrences; any individual edits to those were discarded. This matches editing a series through the calendar page.
            </Alert>
          ) : null}

          {skippedTotal ? (
            <div className="cal-imp-skips">
              <div className="cal-imp-skips-head">Skipped events, by reason</div>
              {result.skipped.map((g, i) => <IcsSkipGroup key={i} group={g} />)}
            </div>
          ) : (
            <div className="cal-imp-clean"><MIcon name="task_alt" size={18} />Every event in the file imported cleanly.</div>
          )}
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ImportCalendarModal });
