/* AddCalendarEventModal — the create / edit-occurrence / edit-series dialog.
   ----------------------------------------------------------------------------
   One shell, three modes:
     • create           — new event, with the "Does not repeat / Repeats" toggle
                           editable. "Repeats" reveals the full rule builder.
     • edit             — a single event/occurrence. The repeat toggle is
                           READ-ONLY (the Repeats choice is fixed at creation —
                           §2 Non-Goal). An occurrence of a series shows a
                           "this occurrence only" hint + an "Edit series…" link.
     • series           — the series-level dialog (the pattern's template/rule),
                           reached from "Edit series…". Saving affects future
                           occurrences only.
   Submitting a create routes to the event endpoint ("Does not repeat") or the
   recurrence-pattern endpoint ("Repeats") — surfaced in the footer hint. */

const CE_DAYS = [
  { key: 'Mon', label: 'M' }, { key: 'Tue', label: 'T' }, { key: 'Wed', label: 'W' },
  { key: 'Thu', label: 'T' }, { key: 'Fri', label: 'F' }, { key: 'Sat', label: 'S' }, { key: 'Sun', label: 'S' },
];
const CE_FREQ = [
  { value: 'Daily', label: 'Daily' }, { value: 'Weekly', label: 'Weekly' },
  { value: 'Monthly', label: 'Monthly' }, { value: 'Yearly', label: 'Yearly' },
];
const CE_MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const ceUnit = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };

const ceIsoDate = (s) => (s || '').slice(0, 10);
const ceIsoTime = (s) => (s || '').slice(11, 16) || '09:00';
const ceAddDays = (isoD, n) => { if (!isoD) return isoD; const [y, m, d] = isoD.split('-').map(Number); const dt = new Date(Date.UTC(y, m - 1, d)); dt.setUTCDate(dt.getUTCDate() + n); const p = (x) => String(x).padStart(2, '0'); return `${dt.getUTCFullYear()}-${p(dt.getUTCMonth() + 1)}-${p(dt.getUTCDate())}`; };

const CalDayToggle = ({ value = [], onChange }) => (
  <div className="cal-daytoggle" role="group" aria-label="Repeat on">
    {CE_DAYS.map((d) => {
      const on = value.includes(d.key);
      return (
        <button key={d.key} type="button" className={`cal-daytoggle-btn${on ? ' on' : ''}`} aria-pressed={on}
          aria-label={d.key} title={d.key}
          onClick={() => onChange(on ? value.filter((k) => k !== d.key) : [...value, d.key])}>
          {d.label}
        </button>
      );
    })}
  </div>
);

const AddCalendarEventModal = ({ mode = 'create', event, calendars = [], defaultDate, defaultCalendarId, onClose, onCreate, onUpdate, onDelete, onExport, onEditSeries, onDeleteSeries }) => {
  const { useState } = React;
  const isSeries = mode === 'series';
  const isEdit = mode === 'edit';
  const src = event || {};

  const seedDate = ceIsoDate(src.start) || defaultDate || (window.OdysseyCalendarData && window.OdysseyCalendarData.todayIso);
  const [d, setD] = useState({
    calendarId: src.calendarId || defaultCalendarId || (calendars[0] && calendars[0].id),
    title: src.title || '',
    description: src.description || '',
    location: src.location || '',
    isAllDay: !!src.isAllDay,
    startDate: seedDate,
    endDate: src.isAllDay ? ceAddDays(ceIsoDate(src.end), -1) : (ceIsoDate(src.end) || seedDate),
    startTime: (src.isAllDay || !src.start) ? '09:00' : ceIsoTime(src.start),
    endTime: (src.isAllDay || !src.end) ? '10:00' : ceIsoTime(src.end),
    // Recurrence
    repeats: isSeries || (mode === 'create' ? false : !!src.patternId),
    freq: (src.pattern && src.pattern.frequency) || 'Weekly',
    interval: (src.pattern && src.pattern.interval) || 1,
    days: (src.pattern && src.pattern.daysOfWeek) || ['Mon'],
    dayOfMonth: (src.pattern && src.pattern.dayOfMonth) || (Number(seedDate.slice(8, 10)) || 1),
    monthOfYear: (src.pattern && src.pattern.monthOfYear) || (Number(seedDate.slice(5, 7)) || 1),
    endMode: (src.pattern && src.pattern.recurrenceEndDate) ? 'date' : 'count',
    endOnDate: (src.pattern && src.pattern.recurrenceEndDate) || ceAddDays(seedDate, 90),
    count: (src.pattern && src.pattern.occurrenceCount) || 10,
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setD((p) => ({ ...p, [k]: v })); if (errors[k]) setErrors((e) => ({ ...e, [k]: undefined })); };

  const cal = calendars.find((c) => c.id === d.calendarId) || calendars[0] || {};
  const calOptions = calendars.map((c) => ({ value: c.id, label: c.name, icon: 'circle', iconColor: c.color }));

  const occurrenceOfSeries = isEdit && !!src.patternId;
  const repeatEditable = mode === 'create';

  const validate = () => {
    const e = {};
    if (!d.title.trim()) e.title = 'Give the event a title.';
    if (!d.startDate) e.startDate = 'Choose a start date.';
    if (d.isAllDay) {
      if (d.endDate && d.endDate < d.startDate) e.endDate = 'End day is before the start day.';
    } else if (`${d.endDate}T${d.endTime}` <= `${d.startDate}T${d.startTime}`) {
      e.endTime = 'End must be after the start.';
    }
    if (d.repeats) {
      if (d.freq === 'Weekly' && !d.days.length) e.days = 'Pick at least one day.';
      if (d.endMode === 'count' && (!d.count || d.count < 1)) e.count = 'Enter how many times it repeats.';
      if (d.endMode === 'date' && (!d.endOnDate || d.endOnDate < d.startDate)) e.endOnDate = 'End date must be after the start.';
    }
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const buildTimes = () => {
    if (d.isAllDay) return { start: `${d.startDate}T00:00`, end: `${ceAddDays(d.endDate || d.startDate, 1)}T00:00`, isAllDay: true };
    return { start: `${d.startDate}T${d.startTime}`, end: `${d.endDate}T${d.endTime}`, isAllDay: false };
  };
  const buildPattern = () => d.repeats ? {
    frequency: d.freq, interval: Number(d.interval) || 1,
    daysOfWeek: d.freq === 'Weekly' ? d.days : null,
    dayOfMonth: (d.freq === 'Monthly' || d.freq === 'Yearly') ? Number(d.dayOfMonth) : null,
    monthOfYear: d.freq === 'Yearly' ? Number(d.monthOfYear) : null,
    recurrenceEndDate: d.endMode === 'date' ? d.endOnDate : null,
    occurrenceCount: d.endMode === 'count' ? Number(d.count) : null,
  } : null;

  const submit = () => {
    if (!validate()) return;
    const times = buildTimes();
    const core = { calendarId: d.calendarId, title: d.title.trim(), description: d.description.trim(), location: d.location.trim(), ...times };
    if (isSeries) { onUpdate && onUpdate({ ...core, pattern: buildPattern() }); return; }
    if (isEdit) { onUpdate && onUpdate(core); return; }
    if (d.repeats) onCreate && onCreate({ ...core, pattern: buildPattern() });
    else onCreate && onCreate(core);
  };

  const title = isSeries ? 'Edit recurring event' : isEdit ? 'Edit event' : 'New event';
  const icon = isSeries ? 'event_repeat' : 'event';

  const ruleFields = (
    <React.Fragment>
      <div className="cal-rule-row">
        <Select label="Frequency" value={d.freq} onChange={set('freq')} options={CE_FREQ} />
        <FieldShell label="Repeat every">
          <StepperField value={d.interval} onChange={set('interval')} min={1} max={365} unit={ceUnit[d.freq]} />
        </FieldShell>
      </div>
      {d.freq === 'Weekly' && (
        <FieldShell label="Repeat on" error={errors.days}>
          <CalDayToggle value={d.days} onChange={set('days')} />
        </FieldShell>
      )}
      {(d.freq === 'Monthly' || d.freq === 'Yearly') && (
        <div className="cal-rule-row">
          {d.freq === 'Yearly' && (
            <Select label="Month" value={String(d.monthOfYear)} onChange={(v) => set('monthOfYear')(Number(v))}
              options={CE_MONTHS.map((m, i) => ({ value: String(i + 1), label: m }))} />
          )}
          <NumberField label="Day of month" value={d.dayOfMonth} onChange={set('dayOfMonth')} min={1} max={31}
            helper="Clamps to the last day in shorter months." />
        </div>
      )}
      <FieldShell label="Ends" error={errors.endOnDate || errors.count}>
        <div className="cal-ends">
          <label className={`cal-ends-opt${d.endMode === 'date' ? ' on' : ''}`}>
            <input type="radio" name="cal-ends" className="cal-ends-input" checked={d.endMode === 'date'} onChange={() => set('endMode')('date')} />
            <span className="cal-ends-radio" aria-hidden="true" />
            <span className="cal-ends-lbl">On date</span>
            <div className="cal-ends-ctl">
              <DateField value={d.endOnDate} onChange={set('endOnDate')} min={d.startDate} disabled={d.endMode !== 'date'} />
            </div>
          </label>
          <label className={`cal-ends-opt${d.endMode === 'count' ? ' on' : ''}`}>
            <input type="radio" name="cal-ends" className="cal-ends-input" checked={d.endMode === 'count'} onChange={() => set('endMode')('count')} />
            <span className="cal-ends-radio" aria-hidden="true" />
            <span className="cal-ends-lbl">After</span>
            <div className="cal-ends-ctl">
              <StepperField value={d.count} onChange={set('count')} min={1} max={730} unit="occurrence" disabled={d.endMode !== 'count'} />
            </div>
          </label>
        </div>
      </FieldShell>
    </React.Fragment>
  );

  const footer = (
    <React.Fragment>
      {isEdit && onDelete ? <Button variant="text" color="" icon="delete" onClick={() => onDelete(src.id)} className="cal-danger-btn">Delete</Button> : null}
      {isSeries && onDeleteSeries ? <Button variant="text" icon="delete" onClick={() => onDeleteSeries(src.patternId)} className="cal-danger-btn">Delete series</Button> : null}
      <span style={{ flex: 1 }} />
      {(isEdit || isSeries) && onExport ? <Button variant="text" icon="download" onClick={onExport}>Export</Button> : null}
      <Button variant="text" onClick={onClose}>Cancel</Button>
      <Button variant="filled" color="primary" icon="check" onClick={submit}>
        {isSeries ? 'Save series' : isEdit ? 'Save changes' : d.repeats ? 'Create series' : 'Create event'}
      </Button>
    </React.Fragment>
  );

  return (
    <Modal title={title} icon={icon} onClose={onClose} footer={footer} className="cal-event-dialog" bodyClassName="cal-event-body">
      {occurrenceOfSeries ? (
        <div className="cal-series-hint">
          <MIcon name="event_repeat" size={18} />
          <div className="cal-series-hint-t">
            <b>This is one occurrence of “{src.title}”.</b> Changes here affect this occurrence only.
            <button type="button" className="link-btn" onClick={() => onEditSeries && onEditSeries(src)}>Edit series…</button>
          </div>
        </div>
      ) : null}

      <div className="edit-grid cal-event-grid">
        <div className="edit-wide">
          <Field label="Title" value={d.title} onChange={set('title')} error={errors.title} maxLength={200} autoFocus placeholder="What's happening?" />
        </div>
        <Select label="Calendar" value={d.calendarId} onChange={set('calendarId')} options={calOptions} />
        <FieldShell label="All day">
          <div className="cal-allday-row">
            <Switch checked={d.isAllDay} onChange={set('isAllDay')} />
            <span className="cal-allday-label">{d.isAllDay ? 'Spans whole days' : 'Has a start and end time'}</span>
          </div>
        </FieldShell>

        <div className="edit-wide">
          <FieldShell label="Starts" error={errors.startDate}>
            <div className={`cal-when-row${d.isAllDay ? ' allday' : ''}`}>
              <DateField value={d.startDate} onChange={set('startDate')} />
              {!d.isAllDay ? <TimeField value={d.startTime} onChange={set('startTime')} step={15} /> : null}
            </div>
          </FieldShell>
        </div>
        <div className="edit-wide">
          <FieldShell label="Ends" error={errors.endDate || errors.endTime} help={d.isAllDay ? 'Inclusive — the last whole day.' : undefined}>
            <div className={`cal-when-row${d.isAllDay ? ' allday' : ''}`}>
              <DateField value={d.endDate} onChange={set('endDate')} min={d.startDate} />
              {!d.isAllDay ? <TimeField value={d.endTime} onChange={set('endTime')} step={15} /> : null}
            </div>
          </FieldShell>
        </div>

        <div className="edit-wide">
          <Field label="Location" value={d.location} onChange={set('location')} placeholder="Optional" maxLength={300} />
        </div>
        <div className="edit-wide">
          <NoteField label="Description" value={d.description} onChange={set('description')} maxLength={2000} rows={3} optional placeholder="Notes, agenda, links…" />
        </div>

        {/* ---- Repeat ---- */}
        {isSeries ? (
          <div className="edit-wide cal-rule">{ruleFields}</div>
        ) : (
          <RevealPanel className="edit-wide" ariaLabel="Repeat"
            value={d.repeats ? 'repeats' : 'once'}
            onChange={(v) => set('repeats')(v === 'repeats')}
            options={[{ value: 'once', label: 'Does not repeat', icon: 'event_available' }, { value: 'repeats', label: 'Repeats', icon: 'event_repeat' }]}
            open={d.repeats && repeatEditable}
            locked={!repeatEditable}
            lockedContent={(
              <div className="cal-repeat-locked">
                <MIcon name={d.repeats ? 'event_repeat' : 'event_available'} size={18} />
                <span>{d.repeats ? 'Repeats — the schedule is fixed at creation.' : 'Does not repeat.'}</span>
              </div>
            )}>
            {ruleFields}
          </RevealPanel>
        )}
      </div>
    </Modal>
  );
};

Object.assign(window, { AddCalendarEventModal });
