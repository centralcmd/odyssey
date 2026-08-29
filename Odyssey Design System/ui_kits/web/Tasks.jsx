/* Tasks — /tasks
   ----------------------------------------------------------------------------
   The shared to-do list with a lightweight kanban lifecycle (Backlog → Doing →
   Done, plus Archived). No separate lists — tags are the only organisation axis.
   Two views (view toggle): a TaskBoard kanban (default) and a flat List.

   STATUS IS DERIVED, not stored: a task carries three nullable datetimes —
   StartedAt, CompletedAt, Archived — and OdysseyHelpers.taskStatus() maps them
   to Backlog/Doing/Done/Archived (precedence Archived→Done→Doing→Backlog). The
   write API still accepts a `status` value; every write here translates the
   target status to a datetime patch via OdysseyHelpers.taskStatusPatch (what the
   API does server-side). Board moves are dual-path (drag or keyboard buttons);
   moving to Done stamps CompletedAt, moving out clears it. Archived tasks are
   off-board, shown in a muted section only when the status filter includes
   Archived. Seed + helpers from journal-data.js. */

const T_H = window.OdysseyHelpers;
const T_D = window.OdysseyData;

const TASK_TAG_OPTIONS = () => T_D.taskTags.filter((t) => !t.archived).map((t) => ({ value: t.id, label: t.name }));

/* Atoms not bridged to the kit globals — read straight off the DS namespace. */
const { Menu: TMenu, Toast: TToast, ToastStack: TToastStack } = window.OdysseyDesignSystem_d5aa51 || {};

/* ================= iCalendar VTODO (RFC 5545) export + import sim (spec §6/§9) =================
   Export is real — each task serializes to a VTODO inside a VCALENDAR envelope
   with §3.1 line folding and text escaping, downloaded as text/calendar. Import
   is a simulated parse (the DS FileUpload abstracts the raw bytes, as in the ICS
   / vCard precedents) that creates/updates by UID and returns a result the page
   applies + surfaces. */
const taskExternalUid = (t) => t.externalUid || `urn:uuid:${t.id}`;
const icsEsc = (s) => String(s == null ? '' : s).replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;');
const icsFold = (line) => {
  if (line.length <= 75) return line;
  let out = line.slice(0, 75), rest = line.slice(75);
  while (rest.length) { out += '\r\n ' + rest.slice(0, 74); rest = rest.slice(74); }
  return out;
};
const icsUtc = (iso) => { try { return new Date(iso).toISOString().replace(/[-:]/g, '').replace(/\.\d+/, ''); } catch (e) { return ''; } };
const TASK_ICS_STATUS = { Backlog: 'NEEDS-ACTION', Doing: 'IN-PROCESS', Done: 'COMPLETED', Archived: 'CANCELLED' };

const buildVTodo = (t) => {
  const status = T_H.taskStatus(t);
  const L = ['BEGIN:VTODO'];
  L.push('UID:' + taskExternalUid(t));
  L.push('DTSTAMP:' + icsUtc(t.updatedAt || new Date().toISOString()));
  L.push('SUMMARY:' + icsEsc(t.title));
  if (t.content) L.push('DESCRIPTION:' + icsEsc(t.content));
  if (t.deadline) L.push('DUE;VALUE=DATE:' + t.deadline.replace(/-/g, ''));
  L.push('STATUS:' + TASK_ICS_STATUS[status]);
  L.push('PERCENT-COMPLETE:' + (status === 'Done' ? '100' : '0'));
  if (t.startedAt) L.push('DTSTART:' + icsUtc(t.startedAt));
  if (t.completedAt) L.push('COMPLETED:' + icsUtc(t.completedAt));
  const tags = T_H.jTaskTags(t).map((x) => x.name);
  if (tags.length) L.push('CATEGORIES:' + tags.map(icsEsc).join(','));
  (t.attachments || []).forEach((a) => { const id = a.id || a.fileId || a.uid; if (id) L.push('ATTACH;VALUE=URI:odyssey-file:' + id); });
  L.push('END:VTODO');
  return L.map(icsFold).join('\r\n');
};
const buildIcs = (list) => {
  const head = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Odyssey//Tasks//EN', 'CALSCALE:GREGORIAN'];
  return [...head, ...list.map(buildVTodo), 'END:VCALENDAR'].join('\r\n') + '\r\n';
};
const icsStamp = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}Z`; };
const icsDownload = (text, filename) => {
  const blob = new Blob([text], { type: 'text/calendar;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename; document.body.appendChild(a); a.click();
  a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
};

/* ---- import simulation (see note above) ---- */
const TASK_IMP_TITLES = ['Review Q3 vendor invoices', 'Renew domain registration', 'File insurance claim', 'Reconcile savings account', 'Cancel unused subscription', 'Schedule tax appointment'];
let __tSeq = 0;
const makeImportedTasks = (n, rows) => {
  const now = new Date().toISOString();
  const base = rows.filter((t) => T_H.taskStatus(t) === 'Backlog' && !t.archived).length;
  return Array.from({ length: n }, (_, i) => ({
    id: `ti-imp-${Date.now()}-${i}`, externalUid: `urn:uuid:imported-${Date.now()}-${i}`,
    title: TASK_IMP_TITLES[(__tSeq + i) % TASK_IMP_TITLES.length], content: 'Imported from iCalendar.', deadline: null,
    tagIds: [], attachments: [], createdBy: T_D.user.name, updatedBy: T_D.user.name, createdAt: now, updatedAt: now,
    startedAt: null, completedAt: null, archived: null, position: base + i,
  }));
};
const simulateTaskImport = (file, rows, outcome) => {
  if (outcome === 'rejected') return { rejected: 'This file has more than the 2,000-task limit (MaxVTodos). Split it into smaller files and import each.' };
  if (/^odyssey-tasks/i.test(file.name || '')) {
    const ids = rows.map((r) => r.id);
    return { result: { importedCount: 0, updatedCount: ids.length, skipped: [], skippedTagLinkCount: 0, skippedAttachmentCount: 0 }, createdRows: [], updatedIds: ids };
  }
  const created = makeImportedTasks(5, rows); __tSeq += 5;
  const updatedIds = rows.slice(0, 2).map((r) => r.id);
  const skipped = outcome === 'clean' ? [] : [
    { reason: 'Recurring VTODO not supported', count: 2, sampleTitles: ['Weekly budget review', 'Monthly rent reminder'] },
    { reason: 'Title is missing or over 200 characters', count: 1, sampleTitles: ['(no summary)'] },
    { reason: 'External ID already in use by another task', count: 1, sampleTitles: ['Pay electricity bill'] },
  ];
  return { result: { importedCount: created.length, updatedCount: updatedIds.length, skipped, skippedTagLinkCount: outcome === 'clean' ? 0 : 3, skippedAttachmentCount: outcome === 'clean' ? 0 : 1 }, createdRows: created, updatedIds };
};

// Deadline chip tone: overdue = expense, ≤3 days = pending, else neutral.
const DeadlineChip = ({ deadline }) => {
  if (!deadline) return null;
  const n = T_H.jDaysUntil(deadline);
  const tone = n < 0 ? 'expense' : n <= 3 ? 'pending' : 'outline';
  return (
    <span className={`odc-chip ${tone} sm tk-deadline`}>
      <MIcon name="event" size={14} />
      {T_H.jDeadline(deadline)} · {T_H.jDeadlineRel(deadline)}
    </span>
  );
};

const TaskTagChips = ({ t }) => {
  const tags = T_H.jTaskTags(t);
  return tags.length ? <TagChips tags={tags.map((x) => ({ label: x.name }))} max={4} /> : null;
};

/* ---------- A board card body (move buttons are added by TaskBoard) ---------- */
const TaskCardBody = ({ t, onEdit, onArchive, onDelete, onExport }) => (
  <React.Fragment>
    <div className="tk-card-head">
      <span className="tk-card-title">{t.title}</span>
      <div onClick={(e) => e.stopPropagation()}>
        <ActionMenu items={[
          { icon: 'edit', label: 'Edit task', onClick: () => onEdit(t) },
          { icon: 'event_note', label: 'Export as iCalendar', onClick: () => onExport && onExport(t) },
          { icon: 'inventory_2', label: 'Archive', onClick: () => onArchive(t) },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete(t.id) },
        ]} />
      </div>
    </div>
    {t.content ? <div className="tk-card-note">{T_H.jSnippet(t.content, 90)}</div> : null}
    <div className="tk-card-foot">
      <TodoStatusChip status={T_H.taskStatus(t)} size="sm" />
      {t.deadline ? <DeadlineChip deadline={t.deadline} /> : null}
      {t.attachments && t.attachments.length ? <span className="tk-att-count"><MIcon name="attach_file" size={13} /><span className="mono">{t.attachments.length}</span></span> : null}
    </div>
    <div className="tk-card-tags"><TaskTagChips t={t} /></div>
  </React.Fragment>
);

// Cycling status control (Backlog → Doing → Done → Backlog), text-labelled per
// state; Archived is read-only here — reached only via the row's action menu.
const TASK_STATUS_ICON = { Backlog: 'radio_button_unchecked', Doing: 'radio_button_checked', Done: 'task_alt', Archived: 'task_alt' };
const TASK_STATUS_CYCLE = { Backlog: 'Doing', Doing: 'Done', Done: 'Archived' };
const TaskStatusButton = ({ status, onCycle }) => {
  const archived = status === 'Archived';
  const next = TASK_STATUS_CYCLE[status];
  return (
    <button type="button" className="tk-status-btn" data-s={status} disabled={archived}
      aria-label={archived ? 'Archived' : `Status: ${status}. Click to set ${next}.`}
      onClick={archived ? undefined : () => onCycle(next)}>
      <span className="material-icons" aria-hidden="true">{TASK_STATUS_ICON[status]}</span>
    </button>
  );
};

/* ---------- Flat LIST view ---------- */
const TaskListRow = ({ t, onStatus, onEdit, onArchive, onDelete, onExport }) => (
  <Card className="acct-item tk-row">
    <div className="acct-head" style={{ cursor: 'default' }} data-status={T_H.taskStatus(t)}>
      <div onClick={(e) => e.stopPropagation()}>
        <TaskStatusButton status={T_H.taskStatus(t)} onCycle={(v) => onStatus(t, v)} />
      </div>
      <div className="acct-id">
        <div className="acct-name-row">
          <span className="acct-name">{t.title}</span>
          {t.deadline ? <DeadlineChip deadline={t.deadline} /> : null}
        </div>
        <div className="acct-tags tk-subline">
          <span className="je-author"><MIcon name="person" size={14} />{t.createdBy}</span>
          {t.completedAt ? <React.Fragment><span className="acct-dot">·</span><span className="mono">Done {T_H.jDateTime(t.completedAt)}</span></React.Fragment> : null}
          {t.content ? <React.Fragment><span className="acct-dot">·</span><span className="tk-note-inline">{T_H.jSnippet(t.content, 80)}</span></React.Fragment> : null}
        </div>
        <div className="je-cardfoot"><TaskTagChips t={t} /></div>
      </div>
      <div className="acct-controls" onClick={(e) => e.stopPropagation()}>
        <ActionMenu items={[
          { icon: 'edit', label: 'Edit task', onClick: () => onEdit(t) },
          { icon: 'event_note', label: 'Export as iCalendar', onClick: () => onExport && onExport(t) },
          { divider: true },
          { icon: t.archived ? 'unarchive' : 'inventory_2', label: t.archived ? 'Unarchive' : 'Archive', onClick: () => onArchive(t) },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete(t.id) },
        ]} />
      </div>
    </div>
  </Card>
);

/* ---------- Create / edit dialog ---------- */
const AddTaskModal = ({ task, onClose, onSubmit }) => {
  const { useState } = React;
  const editing = !!task;
  const [draft, setDraft] = useState({
    title: task ? task.title : '', content: task ? (task.content || '') : '',
    deadline: task ? (task.deadline || '') : '', status: task ? T_H.taskStatus(task) : 'Backlog',
    tagIds: task ? (task.tagIds || []) : [], attachments: T_H.toUploadFiles(task ? task.attachments : []),
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft((d) => ({ ...d, [k]: v })); if (errors[k]) setErrors((x) => ({ ...x, [k]: undefined })); };

  const submit = () => {
    const next = {};
    if (!draft.title.trim()) next.title = 'Give the task a title.';
    if (Object.keys(next).length) { setErrors(next); return; }
    // Emit a semantic status choice; the page converts it to the datetime model
    // (StartedAt/CompletedAt/Archived) at the write boundary — the create/update
    // API requires the new model, so no `status` is ever persisted.
    onSubmit({
      title: draft.title.trim(), content: draft.content.trim() || null,
      deadline: draft.deadline || null, statusChoice: draft.status, tagIds: draft.tagIds,
      attachments: T_H.fromUploadFiles(draft.attachments, new Date().toISOString().slice(0, 10), 'ka'),
    });
  };

  return (
    <Modal title={editing ? 'Edit task' : 'New task'} icon="checklist" onClose={onClose}
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon="check" onClick={submit}>{editing ? 'Save changes' : 'Create task'}</Button>
      </React.Fragment>}>
      <div className="edit-grid je-create-grid">
        <div className="edit-wide"><Field label="Title" value={draft.title} onChange={set('title')} error={errors.title} maxLength={200} autoFocus /></div>
        {editing ? (
          <DateField label="Deadline" value={draft.deadline} onChange={set('deadline')} optional />
        ) : (
          <div className="edit-wide"><DateField label="Deadline" value={draft.deadline} onChange={set('deadline')} optional /></div>
        )}
        {editing ? (
          <Select label="Status" value={draft.status} onChange={set('status')}
            options={(window.TODO_STATUSES || []).map((s) => ({ value: s.key, label: s.label }))} />
        ) : null}
        <div className="edit-wide"><TagMultiSelect label="Tags" value={draft.tagIds} onChange={set('tagIds')} options={TASK_TAG_OPTIONS()} optional /></div>
        <div className="edit-wide"><NoteField label="Content" value={draft.content} onChange={set('content')} maxLength={4096} rows={4} optional placeholder="Optional details" /></div>
        <div className="edit-wide">
          <FieldShell label="Attachments" optional helper="PDFs and documents.">
            <FileUpload files={draft.attachments} onChange={set('attachments')} compact />
          </FieldShell>
        </div>
      </div>
    </Modal>
  );
};

/* ---------- Page ---------- */
const Tasks = ({ tweaks = {} }) => {
  const { useState, useEffect, useMemo } = React;

  const [view, setView] = useState('board'); // board | list
  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [tagFilter, setTagFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [rows, setRows] = useState(T_D.tasks);
  const [sort, setSort] = useState({ key: 'status', dir: 'asc' });
  const [dialog, setDialog] = useState(null); // {mode:'new'} | {mode:'edit', task}
  const [importOpen, setImportOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [toast, setToast] = useState(null);
  const canImport = tweaks.tCanImport !== false; // requires tasks.create AND tasks.update
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  // List-view sort fields (the board is always manual "Order" within each column).
  const taskSortFields = [
    { key: 'status', label: 'Status', type: 'status', sortValue: (t) => ({ Doing: 0, Backlog: 1, Done: 2, Archived: 3 }[T_H.taskStatus(t)] ?? 1) },
    { key: 'position', label: 'Order', type: 'number', sortValue: (t) => t.position ?? 0 },
    { key: 'title', label: 'Title', type: 'text', sortValue: (t) => (t.title || '').toLowerCase() },
    { key: 'deadline', label: 'Deadline', type: 'date', sortValue: (t) => t.deadline || null },
  ];

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  // Build the datetime-model write DTO from a semantic status choice. This is the
  // create/edit/update boundary — it persists StartedAt/CompletedAt/Archived and
  // never a `status` field (the search API filters by derived status separately).
  const writeStatus = (base, choice, now) => ({ ...base, ...T_H.taskStatusPatch(base, choice, now) });

  const createTask = (dto) => {
    const now = new Date().toISOString();
    const target = dto.statusChoice || 'Backlog';
    const pos = rows.filter((t) => T_H.taskStatus(t) === target && !t.archived).length;
    const { statusChoice, ...rest } = dto;
    const base = {
      id: `ti-${Date.now()}`, createdBy: T_D.user.name, updatedBy: T_D.user.name,
      createdAt: now, updatedAt: now, startedAt: null, completedAt: null, archived: null,
      position: pos, ...rest,
    };
    setRows((prev) => [...prev, writeStatus(base, target, now)]);
    setDialog(null);
  };
  const updateTask = (id, dto) => {
    const now = new Date().toISOString();
    const { statusChoice, ...rest } = dto;
    setRows((prev) => prev.map((t) => {
      if (t.id !== id) return t;
      const merged = { ...t, ...rest, updatedBy: T_D.user.name, updatedAt: now };
      return statusChoice ? writeStatus(merged, statusChoice, now) : merged;
    }));
    setDialog(null);
  };
  // List-row Status select: re-derives datetimes and re-sequences into the target column.
  const setStatus = (t, target) => {
    const now = new Date().toISOString();
    setRows((prev) => {
      const pos = prev.filter((x) => x.id !== t.id && T_H.taskStatus(x) === target && !x.archived).length;
      return prev.map((x) => (x.id === t.id
        ? { ...x, ...T_H.taskStatusPatch(x, target, now), position: target === 'Archived' ? x.position : pos, updatedBy: T_D.user.name, updatedAt: now }
        : x));
    });
  };
  // Archive stamps the Archived datetime; unarchiving moves the task back to
  // Backlog (clears the progress stamps too), not to its pre-archive status.
  const archiveTask = (t) => {
    const now = new Date().toISOString();
    setRows((prev) => prev.map((x) => {
      if (x.id !== t.id) return x;
      return x.archived
        ? { ...x, ...T_H.taskStatusPatch(x, 'Backlog', now), updatedBy: T_D.user.name, updatedAt: now }
        : { ...x, archived: now, updatedBy: T_D.user.name, updatedAt: now };
    }));
  };
  const onDelete = (id) => setRows((prev) => prev.filter((t) => t.id !== id));

  // Board move (drag or keyboard): patch the moved card's datetimes to the target
  // column's status, then re-sequence that column gap-free. Columns are never
  // Archived (archived is off-board).
  const onMove = (id, toStatus, toIndex) => {
    const now = new Date().toISOString();
    setRows((prev) => {
      const moved = prev.find((t) => t.id === id);
      if (!moved) return prev;
      const rest = prev.filter((t) => t.id !== id);
      const patched = { ...moved, ...T_H.taskStatusPatch(moved, toStatus, now), updatedBy: T_D.user.name, updatedAt: now };
      const col = rest.filter((t) => T_H.taskStatus(t) === toStatus && !t.archived).sort((a, b) => a.position - b.position);
      col.splice(toIndex, 0, patched);
      const reseq = col.map((t, i) => ({ ...t, position: i }));
      const untouched = rest.filter((t) => !(T_H.taskStatus(t) === toStatus && !t.archived));
      return [...untouched, ...reseq];
    });
  };

  const matchQ = (t) => {
    if (!debouncedQ) return true;
    return `${t.title} ${t.content || ''}`.toLowerCase().includes(debouncedQ.toLowerCase());
  };
  const matchTag = (t) => !tagFilter.length || (t.tagIds || []).some((x) => tagFilter.includes(x));
  const showArchived = !statusFilter.length || statusFilter.includes('Archived');

  // Which board columns to show: the selected non-archived statuses (all three
  // when nothing / only Archived is selected). Unselected columns are hidden so
  // the board never shows an empty column the filter excluded.
  const BOARD_KEYS = ['Backlog', 'Doing', 'Done'];
  const selectedBoardKeys = BOARD_KEYS.filter((k) => statusFilter.includes(k));
  const boardColumnKeys = selectedBoardKeys.length ? selectedBoardKeys : BOARD_KEYS;
  const boardColumns = boardColumnKeys.map((k) => ({ key: k, label: k }));

  // Board data: non-archived tasks matching search/tag, restricted to the
  // visible columns (so the status filter applies on the board too).
  const boardTasks = useMemo(() => rows.filter((t) => !t.archived && boardColumnKeys.includes(T_H.taskStatus(t)) && matchQ(t) && matchTag(t)), [rows, debouncedQ, tagFilter, statusFilter]);
  const archivedTasks = useMemo(() => rows.filter((t) => t.archived && matchQ(t) && matchTag(t)), [rows, debouncedQ, tagFilter]);

  // List data: honor the status filter directly (default hides Archived), then
  // apply the chosen sort. A secondary status→position sort keeps ties stable.
  const listTasks = useMemo(() => {
    const wantStatuses = statusFilter.length ? statusFilter : ['Backlog', 'Doing', 'Done', 'Archived'];
    const base = rows.filter((t) => wantStatuses.includes(T_H.taskStatus(t)) && matchQ(t) && matchTag(t));
    const DS = window.OdysseyDesignSystem_d5aa51 || {};
    const sorted = DS.SortHelpers ? DS.SortHelpers.sortRows(base, taskSortFields, sort, (t) => t.id) : base;
    return sorted;
  }, [rows, statusFilter, debouncedQ, tagFilter, sort]);

  const counts = { backlog: rows.filter((t) => T_H.taskStatus(t) === 'Backlog').length, doing: rows.filter((t) => T_H.taskStatus(t) === 'Doing').length, done: rows.filter((t) => T_H.taskStatus(t) === 'Done').length, archived: rows.filter((t) => T_H.taskStatus(t) === 'Archived').length };
  const hasFilters = !!(debouncedQ || tagFilter.length || statusFilter.length);
  const clearFilters = () => { setQ(''); setTagFilter([]); setStatusFilter([]); };
  const openEdit = (t) => setDialog({ mode: 'edit', task: t });
  // Per-task export (action-row menu) — a single-VTODO .ics download.
  const exportTask = (t) => {
    const slug = (t.title || 'task').trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60) || 'task';
    icsDownload(buildIcs([t]), `${slug}.ics`);
    pushToast('success', `Exported ${slug}.ics`);
  };

  // Export tasks as an .ics of VTODOs (spec §3/§7.1). 'all' ignores filters;
  // 'filtered' uses the current search/tag/status set (same as all when none).
  const doExport = (scope) => {
    if (exporting) return;
    if (tweaks.tExportCap) { pushToast('error', 'Too many tasks matched — narrow your filters and try again.'); return; }
    setExporting(true);
    setTimeout(() => {
      const set = scope === 'filtered' ? listTasks : rows;
      const fname = scope === 'filtered' ? `odyssey-tasks-filtered-${icsStamp()}.ics` : `odyssey-tasks-${icsStamp()}.ics`;
      icsDownload(buildIcs(set), fname);
      setExporting(false);
      pushToast('success', `Exported ${set.length} ${set.length === 1 ? 'task' : 'tasks'}.`);
    }, 700);
  };
  // Import (spec §7.2): apply created rows + touch updated rows, hand the result
  // back to the dialog to render its summary.
  const runImport = (file) => {
    const sim = simulateTaskImport(file, rows, tweaks.tImportOutcome || 'skips');
    if (sim.rejected) return { rejected: sim.rejected };
    const now = new Date().toISOString();
    const upd = new Set(sim.updatedIds || []);
    setRows(prev => [...(sim.createdRows || []), ...prev.map(t => upd.has(t.id) ? { ...t, updatedAt: now } : t)]);
    return { result: sim.result };
  };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Tasks"
        icon="checklist"
        sub={`${counts.backlog} backlog · ${counts.doing} doing · ${counts.done} done · ${counts.archived} archived`}
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 260, flex: 1 }}>
              <SearchField placeholder="Search title, content…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="Any tag" value={tagFilter} onChange={setTagFilter} options={TASK_TAG_OPTIONS()} />
            </div>
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={(window.TODO_STATUSES || []).map((s) => ({ value: s.key, label: s.label }))} />
            </div>
            <Select id="tk-view" prefix="View" value={view} onChange={setView}
              options={[{ value: 'board', label: 'Board' }, { value: 'list', label: 'List' }]} />
            {view === 'list' ? <SortSelect sort={sort} onSort={setSort} fields={taskSortFields} /> : null}
          </div>
        )}
        primary={{ label: 'New task', icon: 'add', onClick: () => setDialog({ mode: 'new' }) }}
        menu={[
          { icon: 'event_note', label: 'Export all as iCalendar', onClick: () => doExport('all') },
          { icon: 'filter_list', label: `Export filtered (${listTasks.length}) as iCalendar`, onClick: () => doExport('filtered') },
          ...(canImport ? [{ divider: true }, { icon: 'upload_file', label: 'Import from iCalendar…', onClick: () => setImportOpen(true) }] : []),
        ]}
      />

      {importOpen && <ImportTasksModal onClose={() => setImportOpen(false)} onImport={runImport} />}

      {dialog && dialog.mode === 'new' && <AddTaskModal onClose={() => setDialog(null)} onSubmit={createTask} />}
      {dialog && dialog.mode === 'edit' && <AddTaskModal task={dialog.task} onClose={() => setDialog(null)} onSubmit={(dto) => updateTask(dialog.task.id, dto)} />}

      {rows.length === 0 ? (
        <EmptyState icon="checklist" title="No tasks yet"
          description="Track finance-adjacent (or any) to-dos on a shared board — Backlog, Doing, Done — with tags, deadlines, and attachments."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setDialog({ mode: 'new' })}>New task</Button>} />
      ) : view === 'board' ? (
        <React.Fragment>
          {boardTasks.length === 0 && archivedTasks.length === 0 ? (
            <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
              {hasFilters ? <React.Fragment>No tasks match your filters. <button className="link-btn" onClick={clearFilters}>Clear filters</button></React.Fragment> : 'No tasks to show.'}
            </div>
          ) : (
            <TaskBoard tasks={boardTasks.map((t) => ({ ...t, status: T_H.taskStatus(t) }))} columns={boardColumns} onMove={onMove}
              style={{ gridTemplateColumns: `repeat(${boardColumns.length}, minmax(0, 1fr))` }}
              renderCard={(t) => <TaskCardBody t={t} onEdit={openEdit} onArchive={archiveTask} onDelete={onDelete} onExport={exportTask} />} />
          )}
          {showArchived && archivedTasks.length ? (
            <div className="tk-archived">
              <div className="odc-photogrid-head" style={{ marginBottom: 8 }}>
                <span className="material-icons" aria-hidden="true">inventory_2</span>
                <span className="odc-photogrid-title">Archived</span>
                <span className="odc-photogrid-count">{archivedTasks.length}</span>
              </div>
              <div className="acct-list">
                {archivedTasks.map((t) => (
                  <TaskListRow key={t.id} t={t} onStatus={setStatus} onEdit={openEdit} onArchive={archiveTask} onDelete={onDelete} onExport={exportTask} />
                ))}
              </div>
            </div>
          ) : null}
        </React.Fragment>
      ) : (
        listTasks.length === 0 ? (
          <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
            {hasFilters ? <React.Fragment>No tasks match your filters. <button className="link-btn" onClick={clearFilters}>Clear filters</button></React.Fragment> : 'No tasks to show.'}
          </div>
        ) : (
          <div className="acct-list">
            {listTasks.map((t) => (
              <TaskListRow key={t.id} t={t} onStatus={setStatus} onEdit={openEdit} onArchive={archiveTask} onDelete={onDelete} onExport={exportTask} />
            ))}
            <AddRow title="New task" sub="Title, optional details, a deadline, tags, and attachments." onClick={() => setDialog({ mode: 'new' })} />
          </div>
        )
      )}
      {toast && TToast && TToastStack && (
        <TToastStack>
          <TToast key={toast.k} severity={toast.severity} duration={4200} onClose={() => setToast(null)} message={toast.message} />
        </TToastStack>
      )}
    </div>
  );
};

Object.assign(window, { Tasks, TaskCardBody, TaskListRow, AddTaskModal });
