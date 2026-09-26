/* AddPropertyEventModal — add OR edit one event on a property
   (POST / PUT /api/properties/{propertyId}/events). Mirrors
   AddContractEventModal; the differences are the property rules:

   1. THE TYPE LIST IS THE MATRIX. PropertyEventTypeSelect is given the
      property's type and offers its 17 legal members, type-specific first.
      Property.Type is immutable, so a legal event never becomes illegal.
   2. SYSTEM-ONLY TYPES ARE NEVER OFFERED ON CREATE. Editing a recorded
      Archived / Restored / Acquired date cleared / Disposal reversed row
      passes that key as keepType, so the PUT may keep it (AC 5). Switching it
      to a user type is allowed; switching back is not.
   3. RECORDING AN EVENT DOES NOT CHANGE THE PROPERTY. A hand-written
      "Disposed of" does not set the disposed date (Non-Goal 5). The sentence
      is the Type field's help, so aria-describedby carries it.
   4. VALUED IS NOT AN ESTIMATE. It writes a log line only; the estimate
      history is a separate section behind its own claim.
   5. PUT IS A FULL REPLACEMENT, stated in an Alert when editing.

   `source`, the owner id and the attribution fields are not in the body. */

const APE_NO_SIDE_EFFECTS = 'Recording an event does not change the property. To set its acquired or disposed date, edit the property.';

const AddPropertyEventModal = ({ property, event = null, onClose, onSave }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const NS = window.OdysseyDesignSystem_d5aa51 || {};
  const editing = !!event;
  const system = H.pevIsSystem(event);
  const keepType = editing && D.propertyEventSystemOnly.includes(event.type) ? event.type : undefined;

  const [type, setType] = useState(editing ? event.type : 'Other');
  const [title, setTitle] = useState(editing ? event.title : '');
  const [description, setDescription] = useState(editing ? (event.description || '') : '');
  const [notes, setNotes] = useState(editing ? (event.notes || '') : '');
  const [date, setDate] = useState(editing ? event.occurredAt.slice(0, 10) : H.conToday());
  const [time, setTime] = useState(editing ? event.occurredAt.slice(11, 16) : new Date().toISOString().slice(11, 16));
  const [errors, setErrors] = useState({});

  const info = H.pevTypeInfo(type);
  const typeHelp = [info.desc, type === 'Valued' ? 'This is a log line, not an estimate — it does not change the estimated value.' : null, APE_NO_SIDE_EFFECTS].filter(Boolean).join(' ');
  const systemTitleHelp = system
    ? `Recorded automatically when ${H.pevAutoClause(event.type)}. Editing changes this log line only; it does not change the property.`
    : null;

  const submit = () => {
    const err = {};
    if (!title.trim()) err.title = 'Give this event a title — what happened, in your own words.';
    else if (title.length > 256) err.title = 'Keep the title under 256 characters.';
    if (!date) err.date = 'Say when this happened.';
    const occurredAt = date ? `${date}T${time || '00:00'}:00Z` : null;
    if (occurredAt && new Date(occurredAt).getTime() > Date.now() + 60000) err.date = 'This can’t be in the future — events record what has already happened.';
    const legal = H.pevLegality(property.type, type);
    if (legal === 'illegal') err.type = `${info.label} can’t be recorded on ${property.type === 'Vehicle' ? 'a vehicle' : 'real estate'}.`;
    if (legal === 'systemOnly' && type !== keepType) err.type = `${info.label} is recorded automatically and can’t be chosen.`;
    if (Object.keys(err).length) { setErrors(err); return; }
    onSave && onSave({
      id: editing ? event.id : `pev-new-${Date.now()}`,
      propertyId: property.id,
      type, title: title.trim(),
      description: description.trim() || null,
      notes: notes.trim() || null,
      occurredAt,
      source: editing ? (event.source || 'user') : 'user',
      createdByUserId: editing ? event.createdByUserId : 'u-owner',
      createdAtUtc: editing ? event.createdAtUtc : new Date().toISOString(),
    });
  };

  const TypeSel = NS.PropertyEventTypeSelect;
  const placeholder = property.type === 'Vehicle' ? 'e.g. Winter tyres on' : 'e.g. Boiler serviced';

  return (
    <Modal
      title={editing ? 'Edit event' : 'New event'}
      subtitle={editing
        ? (system ? 'Recorded automatically. Changing it changes this log line, not the property.' : 'Change what this entry records. Who recorded it, and when, does not change.')
        : `Record something that has happened to ${property.name}.`}
      icon="history"
      onClose={onClose}
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>{editing ? 'Save changes' : 'Create event'}</Button>
      </React.Fragment>}>
      {TypeSel ? (
        <TypeSel value={type} onChange={(v) => { setType(v); setErrors(e => ({ ...e, type: undefined })); }}
          propertyType={property.type} keepType={keepType} help={errors.type ? undefined : typeHelp} error={errors.type} />
      ) : (
        <Select label="Type" value={type} onChange={setType} help={typeHelp}
          options={(D.propertyEventTypeMatrix[property.type] || []).filter(k => !D.propertyEventSystemOnly.includes(k) || k === keepType)
            .map(k => H.pevTypeInfo(k)).map(t => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }))} />
      )}
      <FieldShell label="When" required error={errors.date}
        helper={errors.date ? undefined : 'Date and time, in the past. A log, not a plan.'}>
        <div className="cev-when">
          <DateField value={date} onChange={(v) => { setDate(v); setErrors(e => ({ ...e, date: undefined })); }} max={H.conToday()} placeholder="Pick a date" />
          <TimeField value={time} onChange={(v) => setTime(v || '00:00')} step={15} />
        </div>
      </FieldShell>
      <Field label="Title" required value={title} onChange={setTitle} error={errors.title} autoFocus
        name="property-event-title" autoComplete="off" data-1p-ignore data-lpignore="true" data-bwignore
        placeholder={placeholder}
        help={systemTitleHelp || 'A short label. This is the line the timeline shows.'} />
      <NoteField label="Description" maxLength={1024} value={description} onChange={setDescription}
        placeholder="What was done, by whom, what it found…"
        help="Shown on the timeline under the title." />
      <NoteField label="Notes" maxLength={1024} value={notes} onChange={setNotes}
        placeholder="Reminders to yourself — when it is due next, what to check…"
        help="Your working notes. Kept off the timeline, but anyone who can see this property can read them." />
      {editing ? (
        <Alert severity="info">
          Saving replaces the whole event — type, title, description, notes and date. Text you clear is cleared, and a type
          you leave unset resets to <strong>Other</strong>. Who recorded this event, and when, is never rewritten.
          {system ? ' Nothing regenerates a description you clear on a recorded event.' : ''}
        </Alert>
      ) : null}
    </Modal>
  );
};

Object.assign(window, { AddPropertyEventModal });
