/* ExportCalendarEventsModal — the bulk VEVENT export dialog (spec §3 state #3 / §7 #2).
   ----------------------------------------------------------------------------
   Opened from the Calendar page toolbar "Export events" action. Export-only —
   no file is read. Two scopes via a SegmentedControl:
     • All events  — every event across every calendar the caller can read,
                     bounded only by the MaxVEvents cap (2000).
     • Filtered    — events matching an explicit From/To window (both required
                     together, span ≤ 92 days), Calendars selection, and search
                     term — all PREFILLED from the page's current period / filter
                     / search so "export what I'm looking at" is one click.

   States (spec §3): Idle · Submitting (button → "Exporting…", disabled) ·
   Error (inline, aria-describedby-associated, polite live region; dialog stays
   open so the user can narrow and retry) · Success (browser downloads, dialog
   closes — handled by the parent's onExport).

   onExport({ scope, from, to, calendarIds, search }) → { error } | undefined.
   A returned { error } string keeps the dialog open and shows the inline error;
   undefined means the download fired and the parent will close the dialog. */

const EXPORT_MAX_SPAN_DAYS = 92;

const ExportCalendarEventsModal = ({ calendars = [], initial = {}, countFor, onClose, onExport }) => {
  const { useState } = React;
  const [scope, setScope] = useState(initial.scope || 'filtered');
  const [from, setFrom] = useState(initial.from || '');
  const [to, setTo] = useState(initial.to || '');
  const [calendarIds, setCalendarIds] = useState(initial.calendarIds || []);
  const [search, setSearch] = useState(initial.search || '');
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  const calOptions = calendars.map((c) => ({ value: c.id, label: c.name, icon: 'circle', iconColor: c.color }));
  const spanDays = (from && to) ? Math.round((new Date(to) - new Date(from)) / 86400000) + 1 : null;
  const query = { scope, from: scope === 'filtered' ? from : null, to: scope === 'filtered' ? to : null, calendarIds: scope === 'filtered' ? calendarIds : [], search: scope === 'filtered' ? search.trim() : '' };
  // Live count of what will export (spec surfaces set size before submit).
  const willCount = countFor ? countFor(query) : null;

  const submit = () => {
    if (busy) return;
    setError(null);
    if (scope === 'filtered') {
      if ((from && !to) || (!from && to)) { setError('Choose both a start and an end date, or clear both to export without a date range.'); return; }
      if (from && to && from > to) { setError('The start date is after the end date.'); return; }
      if (spanDays != null && spanDays > EXPORT_MAX_SPAN_DAYS) { setError(`That date range spans ${spanDays} days. Choose a range of ${EXPORT_MAX_SPAN_DAYS} days or fewer.`); return; }
    }
    setBusy(true);
    setTimeout(() => {
      const res = onExport ? onExport(query) : undefined;
      if (res && res.error) { setBusy(false); setError(res.error); return; }
      // success — parent closes the dialog after the download fires.
    }, 500);
  };

  const footer = (
    <React.Fragment>
      <Button variant="text" onClick={onClose}>Cancel</Button>
      <Button variant="filled" color="primary" icon="download" loading={busy}
        disabled={willCount === 0} onClick={submit}>
        {busy ? 'Exporting…' : (willCount != null ? `Export ${willCount} ${willCount === 1 ? 'event' : 'events'}` : 'Export')}
      </Button>
    </React.Fragment>
  );

  return (
    <Modal title="Export events" subtitle="Download calendar events as an iCalendar (.ics) file you can import into another calendar app." icon="download" className="cal-exp-dialog" onClose={busy ? undefined : onClose} footer={footer}>
      <div className="cal-exp-form">
        <FieldShell label="What to export">
          <SegmentedControl ariaLabel="Export scope" value={scope} onChange={(v) => { setScope(v); setError(null); }}
            options={[{ value: 'all', label: 'All events', icon: 'calendar_month' }, { value: 'filtered', label: 'Filtered', icon: 'filter_list' }]} />
        </FieldShell>

        {scope === 'all' ? (
          <div className="cvi-note">
            <MIcon name="info" size={16} />
            <span>Exports every event across all {calendars.length} calendars. Large calendars are capped at 2,000 events — narrow with a filter if you hit the limit.</span>
          </div>
        ) : (
          <div className="cal-exp-filters">
            <FieldShell label="Date range" help={`Both dates together · up to ${EXPORT_MAX_SPAN_DAYS} days`}>
              <div className="cal-exp-range">
                <DateField value={from} onChange={(v) => { setFrom(v); setError(null); }} />
                <span className="cal-exp-range-sep" aria-hidden="true">→</span>
                <DateField value={to} onChange={(v) => { setTo(v); setError(null); }} min={from} />
              </div>
            </FieldShell>
            <FieldShell label="Calendars" optional>
              <MultiSelect allLabel="All calendars" icon="calendar_month" value={calendarIds} onChange={setCalendarIds} options={calOptions} />
            </FieldShell>
            <FieldShell label="Search" optional>
              <SearchField placeholder="Filter by title or location…" value={search} onChange={setSearch} />
            </FieldShell>
          </div>
        )}

        {error ? (
          <div id="cal-exp-error" role="alert" aria-live="polite"><Alert severity="error">{error}</Alert></div>
        ) : null}
      </div>
    </Modal>
  );
};

/* ExportEventScopeModal — the occurrence-vs-series prompt for exporting a single
   recurring event from the event dialog (spec §3 state #2 / §7 #1). A standalone
   event never opens this; the parent downloads it immediately. */
const ExportEventScopeModal = ({ event, onClose, onChoose }) => {
  const { useState } = React;
  const [scope, setScope] = useState('occurrence');
  return (
    <Modal title="Export event" subtitle={`“${event.title}” is part of a recurring series. Choose what to export.`} icon="event_repeat" className="cal-exp-scope" onClose={onClose}
      footer={(
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <span style={{ flex: 1 }} />
          <Button variant="filled" color="primary" icon="download" onClick={() => onChoose(scope)}>Export</Button>
        </React.Fragment>
      )}>
      <div className="cal-exp-scope-body" role="radiogroup" aria-label="What to export">
        <button type="button" role="radio" aria-checked={scope === 'occurrence'} className={`cal-exp-choice${scope === 'occurrence' ? ' sel' : ''}`} onClick={() => setScope('occurrence')}>
          <MIcon name={scope === 'occurrence' ? 'radio_button_checked' : 'radio_button_unchecked'} size={22} className="cal-exp-choice-radio" />
          <span className="cal-exp-choice-t"><b>This occurrence</b><span>Just the one dated event, as a standalone VEVENT.</span></span>
        </button>
        <button type="button" role="radio" aria-checked={scope === 'series'} className={`cal-exp-choice${scope === 'series' ? ' sel' : ''}`} onClick={() => setScope('series')}>
          <MIcon name={scope === 'series' ? 'radio_button_checked' : 'radio_button_unchecked'} size={22} className="cal-exp-choice-radio" />
          <span className="cal-exp-choice-t"><b>Entire series</b><span>The full recurring rule as one RRULE VEVENT.</span></span>
        </button>
      </div>
    </Modal>
  );
};

Object.assign(window, { ExportCalendarEventsModal, ExportEventScopeModal });
