/* Calendar — seed data + helpers (window.OdysseyCalendarData)
   ----------------------------------------------------------------------------
   Mirrors the spec §6 model, flattened for the prototype. Recurring events are
   EAGERLY MATERIALIZED into concrete event rows (each carrying its patternId),
   exactly as the API does — so "edit/delete a single occurrence" is just a row
   op. Events are generated relative to TODAY so the month/week/day views are
   always populated, whatever the real date.

   Times are local wall-clock ISO strings ('YYYY-MM-DDTHH:mm'); all-day events
   use exclusive-end midnight boundaries (end = day AFTER the last day). Each
   calendar bakes the curated swatch's colour + contrast-safe foreground, so a
   chip never computes contrast. */
(function () {
  const pad = (n) => String(n).padStart(2, '0');
  const d2 = (dt) => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}`;
  const at = (dt, hh, mm) => `${d2(dt)}T${pad(hh)}:${pad(mm || 0)}`;
  const midnight = (dt) => `${d2(dt)}T00:00`;

  const base = new Date(); base.setHours(0, 0, 0, 0);
  const day = (off) => { const d = new Date(base); d.setDate(d.getDate() + off); return d; };
  const firstOfMonth = new Date(base.getFullYear(), base.getMonth(), 1);
  const lastOfMonth = new Date(base.getFullYear(), base.getMonth() + 1, 0);

  // Curated swatches (mirror of the DS ColorSwatchSelect palette) — baked fg.
  const calendars = [
    { id: 'cal-work',   name: 'Work',      description: 'Meetings, reviews, deadlines.', color: '#0369A1', fg: '#FFFFFF' },
    { id: 'cal-family', name: 'Family',    description: 'Household & the kids.',          color: '#15803D', fg: '#FFFFFF' },
    { id: 'cal-bills',  name: 'Bills',     description: 'Rent, utilities, due dates.',    color: '#B23B3B', fg: '#FFFFFF' },
    { id: 'cal-trips',  name: 'Trips',     description: 'Travel & time away.',            color: '#F59E0B', fg: '#0E1525' },
    { id: 'cal-social', name: 'Birthdays', description: 'Anniversaries & birthdays.',     color: '#6D28D9', fg: '#FFFFFF' },
  ];
  const calById = calendars.reduce((m, c) => { m[c.id] = c; return m; }, {});

  let seq = 0;
  const uid = (p) => `${p}-${++seq}`;
  const events = [];
  const patterns = [];

  const addEvent = (o) => { const c = calById[o.calendarId]; events.push({ id: uid('ev'), description: '', location: '', isAllDay: false, patternId: null, color: c.color, fg: c.fg, ...o }); };

  // A recurring series: register the pattern, then materialize occurrences.
  const addSeries = (pattern, occurrences) => {
    const pid = uid('rp');
    patterns.push({ id: pid, ...pattern });
    occurrences.forEach((o) => addEvent({ ...o, calendarId: pattern.calendarId, patternId: pid }));
    return pid;
  };

  // Window for generated recurrences: a week before the month through a week after.
  const winStart = -( (base.getDate() - 1) + 7 );
  const winEnd = ((lastOfMonth.getDate() - base.getDate()) + 7);
  const eachDay = (fn) => { for (let o = winStart; o <= winEnd; o++) fn(day(o), day(o).getDay(), o); };

  // --- Recurring: weekday standup (Mon–Fri 09:00) ---
  {
    const occ = [];
    eachDay((d, wd) => { if (wd >= 1 && wd <= 5) occ.push({ title: 'Standup', location: 'Zoom', start: at(d, 9, 0), end: at(d, 9, 15) }); });
    addSeries({ calendarId: 'cal-work', title: 'Standup', frequency: 'Daily', interval: 1, daysOfWeek: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'], recurrenceEndDate: null, occurrenceCount: 60, isAllDay: false, startTime: '09:00', endTime: '09:15' }, occ);
  }
  // --- Recurring: Monday design review (13:00) ---
  {
    const occ = [];
    eachDay((d, wd) => { if (wd === 1) occ.push({ title: 'Design review', location: 'Studio', start: at(d, 13, 0), end: at(d, 14, 0) }); });
    addSeries({ calendarId: 'cal-work', title: 'Design review', frequency: 'Weekly', interval: 1, daysOfWeek: ['Mon'], recurrenceEndDate: null, occurrenceCount: 12, isAllDay: false, startTime: '13:00', endTime: '14:00' }, occ);
  }
  // --- Recurring: Tue/Thu evening swim (family) ---
  {
    const occ = [];
    eachDay((d, wd) => { if (wd === 2 || wd === 4) occ.push({ title: 'Swim class', location: 'Leisure centre', start: at(d, 17, 30), end: at(d, 18, 30) }); });
    addSeries({ calendarId: 'cal-family', title: 'Swim class', frequency: 'Weekly', interval: 1, daysOfWeek: ['Tue', 'Thu'], recurrenceEndDate: null, occurrenceCount: 20, isAllDay: false, startTime: '17:30', endTime: '18:30' }, occ);
  }
  // --- Recurring monthly all-day: Rent due (1st) ---
  addSeries(
    { calendarId: 'cal-bills', title: 'Rent due', frequency: 'Monthly', interval: 1, dayOfMonth: 1, recurrenceEndDate: null, occurrenceCount: 12, isAllDay: true },
    [{ title: 'Rent due', start: midnight(firstOfMonth), end: midnight(day(1 - base.getDate() + 1)), isAllDay: true }]
  );

  // --- One-offs, spread through the month (relative to today) ---
  addEvent({ calendarId: 'cal-work', title: 'Quarterly planning', location: 'Boardroom', start: at(day(2), 10, 0), end: at(day(2), 12, 0), description: 'Q3 objectives and roadmap.' });
  addEvent({ calendarId: 'cal-work', title: '1:1 with Sam', start: at(day(1), 15, 0), end: at(day(1), 15, 30) });
  addEvent({ calendarId: 'cal-family', title: 'Dentist — Mia', location: 'High St Dental', start: at(day(3), 16, 30), end: at(day(3), 17, 15) });
  addEvent({ calendarId: 'cal-social', title: 'Dinner with Alex', location: 'Trattoria', start: at(day(4), 19, 30), end: at(day(4), 21, 30) });
  addEvent({ calendarId: 'cal-work', title: 'Project deadline', start: at(day(6), 17, 0), end: at(day(6), 18, 0), description: 'Ship the calendar module.' });
  addEvent({ calendarId: 'cal-bills', title: 'Electricity bill', start: midnight(day(9)), end: midnight(day(10)), isAllDay: true });

  // --- Multi-day all-day trip (4 days) ---
  addEvent({ calendarId: 'cal-trips', title: 'Lisbon trip', location: 'Lisbon, PT', start: midnight(day(11)), end: midnight(day(15)), isAllDay: true, description: 'Flights booked · Hotel Baixa.' });

  // --- All-day public holiday ---
  addEvent({ calendarId: 'cal-work', title: 'Public holiday', start: midnight(day(18)), end: midnight(day(19)), isAllDay: true });

  // --- Recurring yearly all-day birthday ---
  addSeries(
    { calendarId: 'cal-social', title: "Mum's birthday", frequency: 'Yearly', interval: 1, dayOfMonth: day(7).getDate(), monthOfYear: day(7).getMonth() + 1, recurrenceEndDate: null, occurrenceCount: 10, isAllDay: true },
    [{ title: "Mum's birthday", start: midnight(day(7)), end: midnight(day(8)), isAllDay: true }]
  );

  // A busy day to exercise "+N more": pile extra work events on today.
  addEvent({ calendarId: 'cal-work', title: 'Vendor call', start: at(base, 11, 0), end: at(base, 11, 30) });
  addEvent({ calendarId: 'cal-work', title: 'Code review', start: at(base, 14, 0), end: at(base, 14, 45) });
  addEvent({ calendarId: 'cal-family', title: 'School pickup', start: at(base, 15, 30), end: at(base, 16, 0) });

  // ---- ICS export (RFC 5545) --------------------------------------------------
  // Builds the text/calendar body for a single calendar — the same shape the
  // GET /api/calendars/{id}/ics endpoint returns (spec §5/§9). Every VEVENT
  // carries a stable UID (native ExternalUid or a synthesized
  // {EntityId}@odyssey.local), so export→reimport is idempotent. An unmodified
  // recurring series exports as one RRULE VEVENT; standalone events export
  // individually. All-day events use VALUE=DATE with exclusive-end semantics.
  const DOW_ICS = { Mon: 'MO', Tue: 'TU', Wed: 'WE', Thu: 'TH', Fri: 'FR', Sat: 'SA', Sun: 'SU' };
  const icsEsc = (s) => String(s || '').replace(/([,;\\])/g, '\\$1').replace(/\r?\n/g, '\\n');
  const icsDate = (iso) => iso.slice(0, 10).replace(/-/g, '');
  const icsDateTime = (iso) => `${icsDate(iso)}T${iso.slice(11, 16).replace(':', '')}00`;
  // RFC 5545 line folding: continuation lines begin with a single space.
  const fold = (line) => {
    if (line.length <= 74) return line;
    const out = [line.slice(0, 74)];
    let rest = line.slice(74);
    while (rest.length > 73) { out.push(' ' + rest.slice(0, 73)); rest = rest.slice(73); }
    if (rest.length) out.push(' ' + rest);
    return out.join('\r\n');
  };

  const icsWhen = (e) => (e.isAllDay
    ? [`DTSTART;VALUE=DATE:${icsDate(e.start)}`, `DTEND;VALUE=DATE:${icsDate(e.end)}`]
    : [`DTSTART:${icsDateTime(e.start)}`, `DTEND:${icsDateTime(e.end)}`]);

  const icsRule = (p) => {
    const parts = [`FREQ=${(p.frequency || 'WEEKLY').toUpperCase()}`];
    if (p.interval && p.interval !== 1) parts.push(`INTERVAL=${p.interval}`);
    if (p.frequency === 'Weekly' && p.daysOfWeek && p.daysOfWeek.length) parts.push(`BYDAY=${p.daysOfWeek.map((d) => DOW_ICS[d]).join(',')}`);
    if ((p.frequency === 'Monthly' || p.frequency === 'Yearly') && p.dayOfMonth) parts.push(`BYMONTHDAY=${p.dayOfMonth}`);
    if (p.frequency === 'Yearly' && p.monthOfYear) parts.push(`BYMONTH=${p.monthOfYear}`);
    if (p.occurrenceCount) parts.push(`COUNT=${p.occurrenceCount}`);
    else if (p.recurrenceEndDate) parts.push(`UNTIL=${icsDate(p.recurrenceEndDate)}T235959Z`);
    return parts.join(';');
  };

  const buildIcs = (calendar, allEvents, allPatterns) => {
    const evs = allEvents.filter((e) => e.calendarId === calendar.id);
    const pats = allPatterns.filter((p) => p.calendarId === calendar.id);
    return buildIcsFrom(evs, pats, { calName: calendar.name });
  };

  // General VCALENDAR builder over an arbitrary event set, across calendars
  // (spec §5 multi-event export). A series whose rows are ALL present in the set
  // collapses to one RRULE VEVENT; any event whose series is only partially
  // present (a filtered/occurrence subset) emits as standalone VEVENTs — the
  // same collapse-or-standalone logic the whole-calendar export uses.
  const buildIcsFrom = (evs, pats, opts = {}) => {
    const dtstamp = icsDateTime(new Date().toISOString());
    const head = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Odyssey//Calendar//EN', 'CALSCALE:GREGORIAN'];
    if (opts.calName) head.push(`X-WR-CALNAME:${icsEsc(opts.calName)}`);
    const lines = head.slice();
    const emit = (uid, e, rule) => {
      lines.push('BEGIN:VEVENT', `UID:${uid}`, `DTSTAMP:${dtstamp}`, ...icsWhen(e), `SUMMARY:${icsEsc(e.title)}`);
      if (e.location) lines.push(`LOCATION:${icsEsc(e.location)}`);
      if (e.description) lines.push(`DESCRIPTION:${icsEsc(e.description)}`);
      if (rule) lines.push(`RRULE:${rule}`);
      lines.push('END:VEVENT');
    };
    const evIds = new Set(evs.map((e) => e.id));
    const covered = new Set();
    (pats || []).forEach((p) => {
      const rows = evs.filter((e) => e.patternId === p.id);
      if (!rows.length) return;
      // Only collapse to RRULE when the ENTIRE materialized series is in the set;
      // a filtered subset must stay per-occurrence so nothing outside it leaks in.
      const seriesAll = (window.__calAllEvents || []).filter((e) => e.patternId === p.id);
      const whole = seriesAll.length > 0 && seriesAll.every((e) => evIds.has(e.id));
      if (whole) {
        rows.forEach((r) => covered.add(r.id));
        const first = rows.slice().sort((a, b) => (a.start < b.start ? -1 : 1))[0];
        emit(p.externalUid || `${p.id}@odyssey.local`, { ...first, title: p.title || first.title }, icsRule(p));
      }
    });
    evs.filter((e) => !covered.has(e.id)).forEach((e) => emit(e.externalUid || `${e.id}@odyssey.local`, e, null));
    lines.push('END:VCALENDAR');
    return lines.map(fold).join('\r\n');
  };

  // Single-event export (spec §5 single-event pipeline).
  //   scope 'series'     → the whole recurring series as one RRULE VEVENT
  //                        (falls back to per-occurrence if no pattern is found).
  //   scope 'occurrence' → just this row, as a standalone VEVENT.
  const buildSingleIcs = (event, scope, pattern, seriesRows) => {
    if (scope === 'series' && pattern) {
      const rows = (seriesRows && seriesRows.length ? seriesRows : [event]).slice().sort((a, b) => (a.start < b.start ? -1 : 1));
      return buildIcsFrom(rows, [pattern], {});
    }
    return buildIcsFrom([{ ...event, patternId: null }], [], {});
  };

  // ---- Simulated import result (spec §3/§7 IcsImportResult) -------------------
  // The prototype can't parse a real .ics, so a submit produces a representative
  // result AND a batch of imported rows to append — enough to exercise every UI
  // state: plain imported/updated counts, the reason-grouped skipped list with a
  // per-group 100-sample cap ("+N more"), and the regenerate-future warning.
  const bigSample = Array.from({ length: 100 }, (_, i) => `Imported meeting ${i + 1}`);
  const mockImport = (calendarId) => {
    const c = calById[calendarId] || {};
    const mk = (title, off, hh, mm, dur, loc) => {
      const s = at(day(off), hh, mm);
      const e = at(day(off), hh + Math.floor((mm + dur) / 60), (mm + dur) % 60);
      return { id: uid('ev'), calendarId, title, description: '', location: loc || '', start: s, end: e, isAllDay: false, patternId: null, externalUid: `ext-${uid('u')}@partner`, color: c.color, fg: c.fg };
    };
    const rows = [
      mk('Design sync (imported)', 1, 10, 0, 60, 'Meet'),
      mk('Budget review', 2, 14, 30, 45, 'Boardroom'),
      mk('Client workshop', 3, 9, 0, 120, 'Room A'),
      mk('Retro', 3, 16, 0, 45, 'Zoom'),
      mk('Product demo', 5, 11, 0, 30, ''),
      mk('Board meeting', 6, 13, 0, 90, 'HQ'),
      mk('Onboarding', 7, 10, 30, 60, 'Room B'),
      mk('Sales pipeline', 8, 15, 0, 30, ''),
      mk('Security review', 9, 9, 30, 60, 'Zoom'),
      mk('Roadmap planning', 10, 14, 0, 60, 'Boardroom'),
      mk('Vendor sync', 12, 11, 0, 30, 'Meet'),
      mk('All-hands', 13, 16, 30, 45, 'Auditorium'),
    ];
    const result = {
      importedCount: rows.length,
      updatedCount: 2,
      anySeriesRegenerated: true,
      skipped: [
        { reason: 'Unsupported recurrence rule', count: 137, sampleTitles: bigSample },
        { reason: 'Recurrence exceptions (EXDATE/RDATE) are not supported in v1', count: 2, sampleTitles: ['Weekly 1:1 (skips holidays)', 'Cleaner — fortnightly with exceptions'] },
        { reason: 'Unsupported BYDAY ordinal', count: 1, sampleTitles: ['Second Tuesday book club'] },
        { reason: 'Title exceeds maximum length', count: 1, sampleTitles: ['Reminder: annual home & contents insurance renewal — confirm valuation with…'] },
        { reason: 'Duplicate UID within this file is not supported', count: 1, sampleTitles: ['Standup'] },
      ],
    };
    return { rows, result };
  };

  window.OdysseyCalendarData = {
    calendars, calById, events, patterns,
    todayIso: d2(base),
    monthIso: d2(firstOfMonth),
    fmt: { d2, at, midnight, pad },
    buildIcs, buildIcsFrom, buildSingleIcs, mockImport,
  };
})();
