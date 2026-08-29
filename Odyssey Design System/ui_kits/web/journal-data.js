/* Journal module seed — /journal + /tasks
   ----------------------------------------------------------------------------
   Mirrors the Journal spec (v2.1) DTO shapes for the click-thru. No API.
   - JournalEntry: Title, Content (plain text), EntryDate (UTC), Location,
     CreatedBy/UpdatedBy (author, display-only), timestamps, Archived, plus
     scalar link sets: tagIds, contactIds, and owned photos / attachments.
   - TodoItem: Title, Content, Deadline (DateOnly), Status (Backlog/Doing/Done/
     Archived), Position (per-column order), CompletedAt, tags, attachments.
   - JournalTag / TaskTag: module-local reference data (Guid PKs in prod).
   Contact ids reference the existing Finance contacts seed; one id
   ('c-ghost') is deliberately dangling to exercise the "Unavailable" placeholder.
   Photos are file references; the kit has no real bytes, so a photo is just
   { id, name } and renders as a striped placeholder tile.                     */

(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  // ---- Authors (display-only attribution; not an authz boundary) ----
  const AUTHORS = {
    jane: 'Jane Sato',
    mara: 'Mara Lindqvist',
    sofia: 'Sofia Ruiz',
  };

  // ---- Journal tags ----
  D.journalTags = [
    { id: 'jt1', name: 'Milestone',   normalizedName: 'MILESTONE',   description: 'A notable event worth marking.', archived: null },
    { id: 'jt2', name: 'Property',    normalizedName: 'PROPERTY',    description: 'Home, mortgage, and maintenance.', archived: null },
    { id: 'jt3', name: 'Tax',         normalizedName: 'TAX',         description: 'Filings, letters, deadlines.', archived: null },
    { id: 'jt4', name: 'Vehicle',     normalizedName: 'VEHICLE',     description: null, archived: null },
    { id: 'jt5', name: 'Travel',      normalizedName: 'TRAVEL',      description: null, archived: null },
    { id: 'jt6', name: 'Insurance',   normalizedName: 'INSURANCE',   description: null, archived: null },
    { id: 'jt7', name: 'Old category', normalizedName: 'OLD CATEGORY', description: 'Retired — kept for history.', archived: '2025-02-01T00:00:00Z' },
  ];

  // ---- Task tags ----
  D.taskTags = [
    { id: 'kt1', name: 'Finance',   normalizedName: 'FINANCE',   description: 'Money admin.', archived: null },
    { id: 'kt2', name: 'Home',      normalizedName: 'HOME',      description: null, archived: null },
    { id: 'kt3', name: 'Urgent',    normalizedName: 'URGENT',    description: 'Time-sensitive.', archived: null },
    { id: 'kt4', name: 'Paperwork', normalizedName: 'PAPERWORK', description: null, archived: null },
    { id: 'kt5', name: 'Errand',    normalizedName: 'ERRAND',    description: null, archived: null },
  ];

  // ---- Journal entries (reverse-chron by entryDate for display) ----
  D.journalEntries = [
    {
      id: 'je1', title: 'Closed on the Sunset Ave flat',
      content: 'Keys handed over at 3pm. The notary appointment ran long but everything cleared. Wired the balance from the joint account this morning; the confirmation is attached. Need to switch the utilities over and set up a maintenance log.',
      entryDate: '2026-06-28T00:00:00Z', location: 'San Francisco, CA',
      createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane,
      createdAt: '2026-06-28T22:04:00Z', updatedAt: '2026-06-28T22:04:00Z', archived: null,
      tagIds: ['jt1', 'jt2'], contactIds: ['c2'],
      photos: [
        { id: 'jp1', name: 'keys-handover.jpg' },
        { id: 'jp2', name: 'living-room.jpg' },
        { id: 'jp3', name: 'kitchen.jpg' },
      ],
      attachments: [
        { id: 'ja1', name: 'wire_confirmation_0628.pdf', kind: 'Other', size: '88 KB', uploaded: '2026-06-28' },
        { id: 'ja2', name: 'closing_statement.pdf', kind: 'Statement', size: '240 KB', uploaded: '2026-06-28' },
      ],
    },
    {
      id: 'je2', title: 'IRS letter about the 2024 return',
      content: 'Received a CP2000 notice proposing a change to the 2024 return — they flagged a 1099 that was already reported under a different payer name. Drafting a response with the brokerage summary. Response due within 30 days.',
      entryDate: '2026-06-19T00:00:00Z', location: null,
      createdBy: AUTHORS.jane, updatedBy: AUTHORS.mara,
      createdAt: '2026-06-19T14:20:00Z', updatedAt: '2026-06-21T09:02:00Z', archived: null,
      tagIds: ['jt3'], contactIds: ['c-ghost'],
      photos: [{ id: 'jp4', name: 'cp2000-page1.jpg' }],
      attachments: [{ id: 'ja3', name: 'cp2000_notice.pdf', kind: 'Other', size: '1.1 MB', uploaded: '2026-06-19' }],
    },
    {
      id: 'je3', title: 'Annual car service',
      content: 'Full service at 60k miles. Replaced brake pads and cabin filter. They noted the front tires will need replacing before winter. Kept the invoice for the maintenance history.',
      entryDate: '2026-05-30T00:00:00Z', location: 'Oakland, CA',
      createdBy: AUTHORS.sofia, updatedBy: AUTHORS.sofia,
      createdAt: '2026-05-30T18:41:00Z', updatedAt: '2026-05-30T18:41:00Z', archived: null,
      tagIds: ['jt4'], contactIds: [],
      photos: [],
      attachments: [{ id: 'ja4', name: 'service_invoice_may.pdf', kind: 'Invoice', size: '156 KB', uploaded: '2026-05-30' }],
    },
    {
      id: 'je4', title: 'Renewed home insurance',
      content: 'Switched carriers at renewal — the new premium is lower with the same coverage limits. Bundled it with the auto policy for a small multi-line discount. Policy documents attached.',
      entryDate: '2026-05-12T00:00:00Z', location: null,
      createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane,
      createdAt: '2026-05-12T11:15:00Z', updatedAt: '2026-05-12T11:15:00Z', archived: null,
      tagIds: ['jt6', 'jt2'], contactIds: ['c2'],
      photos: [],
      attachments: [{ id: 'ja5', name: 'policy_2026.pdf', kind: 'Policy', size: '420 KB', uploaded: '2026-05-12' }],
    },
    {
      id: 'je5', title: 'Two weeks in Lisbon',
      content: 'Booked flights and the apartment for late September. Set aside a travel budget and noted the card with no foreign transaction fees. Adding the confirmations here so everything is in one place.',
      entryDate: '2026-04-25T00:00:00Z', location: 'Lisbon, Portugal',
      createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane,
      createdAt: '2026-04-25T20:10:00Z', updatedAt: '2026-04-26T08:00:00Z', archived: null,
      tagIds: ['jt5'], contactIds: [],
      photos: [{ id: 'jp5', name: 'itinerary.png' }, { id: 'jp6', name: 'apartment.jpg' }],
      attachments: [],
    },
    {
      id: 'je6', title: 'Old budgeting note',
      content: 'Early draft of the household budget from last year. Superseded by the Budgets page — archived here for reference.',
      entryDate: '2025-11-02T00:00:00Z', location: null,
      createdBy: AUTHORS.mara, updatedBy: AUTHORS.mara,
      createdAt: '2025-11-02T09:00:00Z', updatedAt: '2025-11-02T09:00:00Z', archived: '2026-01-10T00:00:00Z',
      tagIds: ['jt7'], contactIds: [],
      photos: [],
      attachments: [],
    },
  ];

  // ---- Tasks (one shared to-do list) --------------------------------------
  // No stored Status enum: the kanban status is DERIVED from three nullable
  // datetimes (mirrors how the other entities archive). Precedence:
  //   Archived (archived set) → Done (completedAt set) → Doing (startedAt set)
  //   → Backlog (all null, the starting state).
  // `position` is the per-column display order. Unarchiving restores whatever
  // the datetimes imply (a done task returns to Done, not Backlog).
  D.tasks = [
    { id: 'ti1', title: 'Respond to the CP2000 notice', content: 'Attach the brokerage 1099 summary and a short cover letter.', deadline: '2026-07-18', position: 0, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-06-21T09:05:00Z', updatedAt: '2026-06-24T10:00:00Z', startedAt: '2026-06-24T10:00:00Z', completedAt: null, archived: null, tagIds: ['kt1', 'kt3', 'kt4'], attachments: [{ id: 'ka1', name: 'draft_response.pdf', kind: 'Other', size: '64 KB', uploaded: '2026-06-24' }] },
    { id: 'ti2', title: 'Switch Sunset Ave utilities to our name', content: 'Electric, water, internet. Take meter photos on move-in day.', deadline: '2026-07-05', position: 1, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-06-28T22:10:00Z', updatedAt: '2026-06-29T08:00:00Z', startedAt: '2026-06-29T08:00:00Z', completedAt: null, archived: null, tagIds: ['kt2'], attachments: [] },
    { id: 'ti3', title: 'Replace front tires before winter', content: 'Flagged at the May service. Get two quotes.', deadline: null, position: 0, createdBy: AUTHORS.sofia, updatedBy: AUTHORS.sofia, createdAt: '2026-05-30T18:45:00Z', updatedAt: '2026-05-30T18:45:00Z', startedAt: null, completedAt: null, archived: null, tagIds: ['kt5'], attachments: [] },
    { id: 'ti4', title: 'Set up a maintenance log for the flat', content: null, deadline: null, position: 1, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-06-28T22:12:00Z', updatedAt: '2026-06-28T22:12:00Z', startedAt: null, completedAt: null, archived: null, tagIds: ['kt2'], attachments: [] },
    { id: 'ti5', title: 'File Q2 estimated tax', content: 'Use last year’s safe-harbor figure.', deadline: '2026-09-15', position: 2, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-06-15T12:00:00Z', updatedAt: '2026-06-15T12:00:00Z', startedAt: null, completedAt: null, archived: null, tagIds: ['kt1', 'kt4'], attachments: [] },
    { id: 'ti6', title: 'Cancel the duplicate streaming plan', content: null, deadline: null, position: 3, createdBy: AUTHORS.mara, updatedBy: AUTHORS.mara, createdAt: '2026-06-10T15:00:00Z', updatedAt: '2026-06-10T15:00:00Z', startedAt: null, completedAt: null, archived: null, tagIds: ['kt5'], attachments: [] },
    { id: 'ti7', title: 'Bundle auto + home insurance', content: 'Done at renewal — kept the multi-line discount.', deadline: null, position: 0, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-05-01T10:00:00Z', updatedAt: '2026-05-12T11:20:00Z', startedAt: '2026-05-01T10:00:00Z', completedAt: '2026-05-12T11:20:00Z', archived: null, tagIds: ['kt1'], attachments: [] },
    { id: 'ti8', title: 'Open the joint savings account', content: null, deadline: null, position: 1, createdBy: AUTHORS.jane, updatedBy: AUTHORS.jane, createdAt: '2026-04-02T09:00:00Z', updatedAt: '2026-04-09T16:30:00Z', startedAt: '2026-04-02T09:00:00Z', completedAt: '2026-04-09T16:30:00Z', archived: null, tagIds: ['kt1'], attachments: [] },
    { id: 'ti9', title: 'Shred 2019 paperwork', content: 'Past the retention window.', deadline: null, position: 0, createdBy: AUTHORS.mara, updatedBy: AUTHORS.mara, createdAt: '2026-01-05T09:00:00Z', updatedAt: '2026-02-01T09:00:00Z', startedAt: null, completedAt: null, archived: '2026-02-01T09:00:00Z', tagIds: ['kt4'], attachments: [] },
  ];

  // ---- Lookups ----
  D.journalTagById = Object.fromEntries(D.journalTags.map((t) => [t.id, t]));
  D.taskTagById = Object.fromEntries(D.taskTags.map((t) => [t.id, t]));

  // ---- Helpers (attached to the shared OdysseyHelpers) ----
  Object.assign(H, {
    // Resolve an entry's tag id set to JournalTag records (skips unknown ids).
    jEntryTags(e) { return (e.tagIds || []).map((id) => D.journalTagById[id]).filter(Boolean); },
    jTaskTags(t) { return (t.tagIds || []).map((id) => D.taskTagById[id]).filter(Boolean); },

    // ---- Task status: DERIVED from datetimes (no stored enum) ----
    // Precedence Archived → Done → Doing → Backlog. The write API still accepts a
    // `status` value (see taskStatusPatch) and maps it to these datetimes server
    // side; the client derives the status back for display.
    taskStatus(t) {
      if (!t) return 'Backlog';
      if (t.archived) return 'Archived';
      if (t.completedAt) return 'Done';
      if (t.startedAt) return 'Doing';
      return 'Backlog';
    },
    // Translate a target status into the datetime patch the store persists.
    // Mirrors what the API does when it receives a `status` write: Backlog clears
    // the progress stamps; Doing stamps StartedAt; Done stamps CompletedAt (and
    // StartedAt if it was skipped); Archived stamps Archived but PRESERVES the
    // progress stamps, so unarchiving restores the prior derived status.
    taskStatusPatch(t, target, nowIso) {
      const now = nowIso || new Date().toISOString();
      switch (target) {
        case 'Backlog':  return { startedAt: null, completedAt: null, archived: null };
        case 'Doing':    return { startedAt: (t && t.startedAt) || now, completedAt: null, archived: null };
        case 'Done':     return { startedAt: (t && t.startedAt) || now, completedAt: now, archived: null };
        case 'Archived': return { archived: (t && t.archived) || now };
        default:         return {};
      }
    },

    // Cross-context contact link resolution (spec §11): a known id hydrates
    // to { id, name, type }; a dangling / no-access id returns { id, unavailable }
    // so the UI can render a text-labelled "Unavailable" placeholder.
    jContacts(e) {
      return (e.contactIds || []).map((id) => {
        const c = D.contactById[id];
        return c ? { id: c.id, name: c.name, type: c.type, unavailable: false }
                 : { id, name: 'Unavailable', type: null, unavailable: true };
      });
    },

    // Entry-date, rendered in the reader's local zone (stored UTC).
    jEntryDate(iso) {
      if (!iso) return '—';
      const d = new Date(iso);
      return d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });
    },
    jDateTime(iso) {
      if (!iso) return '—';
      const d = new Date(iso);
      return d.toLocaleString('en-US', { month: 'short', day: '2-digit', year: 'numeric', hour: 'numeric', minute: '2-digit' });
    },
    jDeadline(dateOnly) {
      if (!dateOnly) return null;
      const d = new Date(dateOnly + 'T00:00:00');
      return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    },
    // Whole-day difference from today for a DateOnly deadline; negative = overdue.
    jDaysUntil(dateOnly) {
      if (!dateOnly) return null;
      const t0 = new Date(); t0.setHours(0, 0, 0, 0);
      const d = new Date(dateOnly + 'T00:00:00');
      return Math.round((d - t0) / 86400000);
    },
    jDeadlineRel(dateOnly) {
      const n = H.jDaysUntil(dateOnly);
      if (n == null) return null;
      if (n < 0) return `${Math.abs(n)}d overdue`;
      if (n === 0) return 'Due today';
      if (n === 1) return 'Due tomorrow';
      return `in ${n}d`;
    },
    jSnippet(text, max = 160) {
      const s = (text || '').replace(/\s+/g, ' ').trim();
      return s.length > max ? s.slice(0, max - 1).trimEnd() + '…' : s;
    },

    // ---- FileUpload round-trip (shared by Journal + Tasks) ----
    // FileUpload speaks UploadFile ({ uid, name, kind, sizeBytes }); records store
    // photos as { id, name } and attachments as { id, name, kind, size, uploaded }.
    // These map both ways so ONE control seeds existing files for editing and
    // captures new ones — create + edit share a single path.
    parseSize(s) {
      if (typeof s !== 'string') return null;
      const m = s.match(/([\d.]+)\s*(B|KB|MB|GB)?/i);
      if (!m) return null;
      const mult = { b: 1, kb: 1024, mb: 1048576, gb: 1073741824 }[(m[2] || 'b').toLowerCase()];
      return Math.round(parseFloat(m[1]) * mult);
    },
    humanSize(bytes) {
      if (bytes == null) return '—';
      if (bytes < 1024) return `${bytes} B`;
      if (bytes < 1048576) return `${Math.round(bytes / 1024)} KB`;
      return `${(bytes / 1048576).toFixed(1)} MB`;
    },
    toUploadPhotos(photos) { return (photos || []).map((p) => ({ uid: p.id, name: p.name, kind: 'Image', sizeBytes: p.sizeBytes ?? null })); },
    toUploadFiles(atts) { return (atts || []).map((a) => ({ uid: a.id, name: a.name, kind: a.kind || 'Other', sizeBytes: a.sizeBytes ?? H.parseSize(a.size) })); },
    fromUploadPhotos(files) { return (files || []).map((f, i) => ({ id: f.uid && !/^tmp/.test(f.uid) ? f.uid : `jp-${Date.now()}-${i}`, name: f.name })); },
    fromUploadFiles(files, dateStr, prefix = 'ja') {
      return (files || []).map((f, i) => ({
        id: f.uid && !/^tmp/.test(f.uid) ? f.uid : `${prefix}-${Date.now()}-${i}`,
        name: f.name, kind: f.kind || 'Other', size: H.humanSize(f.sizeBytes), uploaded: dateStr,
      }));
    },
  });
})();
