/* ManageCalendarsModal — the calendars CRUD dialog.
   ----------------------------------------------------------------------------
   List every calendar with an inline editor (name, description, colour
   swatch-select), an add row, and per-row delete. Deleting a calendar that
   still holds events/patterns is BLOCKED (the service returns 409) — the delete
   control is disabled with the reason, mirroring Journal's restrict-while-in-use
   philosophy. Colour is the curated ColorSwatchSelect, never a free picker. */

const CalRowEditor = ({ draft, setDraft, error }) => (
  <div className="cal-mgr-editor">
    <div className="cal-mgr-editor-grid">
      <Field label="Name" value={draft.name} onChange={(v) => setDraft({ ...draft, name: v })} error={error} maxLength={150} autoFocus placeholder="e.g. Personal" />
      <Field label="Description" value={draft.description} onChange={(v) => setDraft({ ...draft, description: v })} placeholder="Optional" maxLength={1000} />
    </div>
    <FieldShell label="Colour">
      <ColorSwatchSelect value={draft.color} onChange={(hex) => setDraft({ ...draft, color: hex })} />
    </FieldShell>
  </div>
);

const ManageCalendarsModal = ({ calendars = [], eventCounts = {}, onClose, onCreate, onUpdate, onDelete, onExport, onImport }) => {
  const { useState } = React;
  const [editingId, setEditingId] = useState(null);
  const [draft, setDraft] = useState(null);
  const [adding, setAdding] = useState(false);
  const [newDraft, setNewDraft] = useState({ name: '', description: '', color: '#0369A1' });
  const [err, setErr] = useState({});

  const swatchName = (hex) => (swatchFor(hex) || {}).name;

  const startEdit = (c) => { setEditingId(c.id); setDraft({ name: c.name, description: c.description || '', color: c.color }); setErr({}); setAdding(false); };
  const saveEdit = () => {
    if (!draft.name.trim()) { setErr({ edit: 'Name is required.' }); return; }
    const dup = calendars.some((c) => c.id !== editingId && c.name.trim().toLowerCase() === draft.name.trim().toLowerCase());
    if (dup) { setErr({ edit: 'A calendar with that name already exists (409).' }); return; }
    onUpdate && onUpdate(editingId, { name: draft.name.trim(), description: draft.description.trim(), color: draft.color });
    setEditingId(null); setDraft(null);
  };
  const saveNew = () => {
    if (!newDraft.name.trim()) { setErr({ add: 'Name is required.' }); return; }
    const dup = calendars.some((c) => c.name.trim().toLowerCase() === newDraft.name.trim().toLowerCase());
    if (dup) { setErr({ add: 'A calendar with that name already exists (409).' }); return; }
    onCreate && onCreate({ name: newDraft.name.trim(), description: newDraft.description.trim(), color: newDraft.color });
    setNewDraft({ name: '', description: '', color: '#0369A1' }); setAdding(false); setErr({});
  };

  return (
    <Modal title="Manage calendars" icon="calendar_month" onClose={onClose} bodyClassName="cal-mgr-body"
      footer={(
        <React.Fragment>
          <span style={{ flex: 1 }} />
          <Button variant="text" onClick={onClose}>Done</Button>
        </React.Fragment>
      )}>
      <div className="cal-mgr-list">
        {calendars.map((c) => {
          const count = eventCounts[c.id] || 0;
          const blocked = count > 0;
          const editing = editingId === c.id;
          return (
            <div key={c.id} className={`cal-mgr-item${editing ? ' editing' : ''}`}>
              <div className="cal-mgr-row">
                <span className="cal-mgr-swatch" style={{ background: c.color }} aria-hidden="true" />
                <div className="cal-mgr-id">
                  <div className="cal-mgr-name">{c.name}</div>
                  <div className="cal-mgr-sub">
                    {c.description ? <span>{c.description}</span> : <span className="muted">No description</span>}
                    <span className="cal-mgr-dot">·</span>
                    <span className="cal-mgr-count">{count} {count === 1 ? 'event' : 'events'}</span>
                    <span className="cal-mgr-dot">·</span>
                    <span className="cal-mgr-swname">{swatchName(c.color)}</span>
                  </div>
                </div>
                <div className="cal-mgr-actions">
                  <span title={count ? `Export ${c.name} as an .ics file` : 'Nothing to export — this calendar is empty'}>
                    <IconButton icon="download" label={`Export ${c.name}`} disabled={!count} onClick={() => count && onExport && onExport(c.id)} />
                  </span>
                  <IconButton icon="edit" label={`Edit ${c.name}`} onClick={() => editing ? setEditingId(null) : startEdit(c)} />
                  <span title={blocked ? 'Remove its events first — a non-empty calendar can’t be deleted (409).' : `Delete ${c.name}`}>
                    <IconButton icon="delete" label={`Delete ${c.name}`} disabled={blocked} onClick={() => !blocked && onDelete && onDelete(c.id)} />
                  </span>
                </div>
              </div>
              {editing && (
                <React.Fragment>
                  <CalRowEditor draft={draft} setDraft={setDraft} error={err.edit} />
                  <div className="cal-mgr-editor-actions">
                    <Button variant="text" onClick={() => { setEditingId(null); setDraft(null); }}>Cancel</Button>
                    <Button variant="filled" color="primary" icon="check" onClick={saveEdit}>Save</Button>
                  </div>
                </React.Fragment>
              )}
            </div>
          );
        })}
      </div>

      {adding ? (
        <div className="cal-mgr-item editing cal-mgr-add-open">
          <div className="cal-mgr-add-head"><MIcon name="add" size={18} /><span>New calendar</span></div>
          <CalRowEditor draft={newDraft} setDraft={setNewDraft} error={err.add} />
          <div className="cal-mgr-editor-actions">
            <Button variant="text" onClick={() => { setAdding(false); setErr({}); }}>Cancel</Button>
            <Button variant="filled" color="primary" icon="check" onClick={saveNew}>Create calendar</Button>
          </div>
        </div>
      ) : (
        <AddRow title="New calendar" sub="Name, an optional description, and a colour." onClick={() => { setAdding(true); setEditingId(null); }} />
      )}
    </Modal>
  );
};

Object.assign(window, { ManageCalendarsModal });
