/* AddContractEventModal — add OR edit one event on a contract
   (POST …/events and PUT …/events/{eventId}).

   Tracks "Contract Events — Backend (Draft v6)".

   Four decisions the dialog has to carry, each of them a rule from the backend
   spec that a user would otherwise meet as an error — or, in the third case,
   never meet at all, which is worse:

   1. THREE FREE-TEXT FIELDS, THREE ROLES (§4.1). `Title` is required for every
      type — a predetermined type does not derive it and does not make it
      optional, `Other` is the catch-all member, and the user's own wording is
      what the log reads back. `Description` is the longer account and appears
      on the timeline beside the title. `Notes` are the user's own working
      notes and are the one field the timeline does not show.

   2. OCCURRED AT IS A DATE AND A TIME, and must not be in the future (§8.3).
      The bound is the server clock with a 60-second forward tolerance, so the
      field refuses "next Tuesday" and accepts "now" from a slightly fast
      clock. Planned events are a non-goal: this is a record of what happened.

   3. "NOT ON THE TIMELINE" IS NOT "PRIVATE", AND THE LABEL MUST NOT SAY IT IS.
      `Notes` sits behind the same contracts.read claim as everything else, is
      searched by the same term and is exported with the rest — and
      contracts.read is held by Admin, Owner and User, so in a household
      install the audience is everyone but Guest. The spec forbids "Private
      note" or any phrasing implying a narrower audience (§4.1), because a user
      who believes the field is private will put things in it that the label
      promised to protect. The helper text here states the audience outright.

   4. THE PUT IS A FULL REPLACEMENT (§5.3), the opposite of UpdateContract. An
      omitted description or note clears it; an omitted type resets to Other.
      The dialog says that where the decision is made.

   No links: the four contact / account columns were removed in v5 (Non-Goal 5),
   so there is nothing to pick and no delete anywhere else to guard. */

const ACE_TYPE_DEFAULT = 'Other';

const AddContractEventModal = ({ contract, event = null, onClose, onSave }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const editing = !!event;

  const initialDate = editing ? event.occurredAt.slice(0, 10) : H.conToday();
  const initialTime = editing ? event.occurredAt.slice(11, 16) : new Date().toISOString().slice(11, 16);

  const [type, setType] = useState(editing ? event.type : ACE_TYPE_DEFAULT);
  const [title, setTitle] = useState(editing ? event.title : '');
  const [description, setDescription] = useState(editing ? (event.description || '') : '');
  const [notes, setNotes] = useState(editing ? (event.notes || '') : '');
  const [date, setDate] = useState(initialDate);
  const [time, setTime] = useState(initialTime);
  const [errors, setErrors] = useState({});

  const typeInfo = H.cevTypeInfo(type);

  const submit = () => {
    const err = {};
    // Whitespace-only is rejected as empty, as the server does (§8.1).
    if (!title.trim()) err.title = 'Give this event a title — what happened, in your own words.';
    else if (title.length > 256) err.title = 'Keep the title under 256 characters.';
    if (!date) err.date = 'Say when this happened.';

    const occurredAt = date ? `${date}T${(time || '00:00')}:00Z` : null;
    // The 60-second forward tolerance, checked against the clock rather than
    // in a [Range] attribute — the bound is runtime state, not a constant.
    if (occurredAt && new Date(occurredAt).getTime() > Date.now() + 60000) {
      err.date = 'This can’t be in the future — events record what has already happened.';
    }

    if (Object.keys(err).length) { setErrors(err); return; }

    onSave && onSave({
      id: editing ? event.id : `cev-new-${Date.now()}`,
      contractId: contract.id,
      type,
      title: title.trim(),
      description: description.trim() || null,
      notes: notes.trim() || null,
      occurredAt,
      // Server-stamped on insert, never rewritten by an update (§5.3).
      createdByUserId: editing ? event.createdByUserId : 'u-jane',
      createdAtUtc: editing ? event.createdAtUtc : new Date().toISOString(),
    });
  };

  return (
    <Modal
      title={editing ? 'Edit event' : 'New event'}
      subtitle={editing
        ? 'Change what this entry records. Who recorded it, and when, does not change.'
        : `Record something that has happened to ${contract.name}.`}
      icon="history"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create event'}
          </Button>
        </React.Fragment>
      }>
      <Select label="Type" value={type} onChange={setType} help={typeInfo.desc}
        options={D.contractEventTypes.map(t => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }))} />
      <FieldShell label="When" required error={errors.date}
        helper={errors.date ? undefined : 'Date and time, in the past. A log, not a plan.'}>
        <div className="cev-when">
          <DateField value={date} onChange={(v) => { setDate(v); setErrors(e => ({ ...e, date: undefined })); }}
            max={H.conToday()} placeholder="Pick a date" />
          <TimeField value={time} onChange={(v) => setTime(v || '00:00')} step={15} />
        </div>
      </FieldShell>

      {/* Required for EVERY type, `Other` included — the type classifies, the
          title is what the reader actually reads.

          The autofill opt-outs are load-bearing: with no `autocomplete`, this
          is an unnamed text input that autofocuses inside a dialog, which is
          exactly the shape browsers and password managers read as a username
          field. `autoComplete="off"` alone is widely ignored, so the two
          vendor opt-outs go with it. */}
      <Field label="Title" required value={title} onChange={setTitle} error={errors.title} autoFocus
        name="contract-event-title" autoComplete="off" data-1p-ignore data-lpignore="true" data-bwignore
        placeholder="e.g. Emailed landlord about the rent increase"
        help="A short label. This is the line the timeline shows." />

      <NoteField label="Description" maxLength={1024} value={description} onChange={setDescription}
        placeholder="What happened, at more length — what was said, what was agreed…"
        help="Shown on the timeline under the title." />

      {/* The audience is stated, never implied to be narrower (§4.1). */}
      <NoteField label="Notes" maxLength={1024} value={notes} onChange={setNotes}
        placeholder="Reminders to yourself — what to chase, what to check next time…"
        help="Your working notes. Kept off the timeline, but anyone who can see this contract can read them." />

      {editing ? (
        <Alert severity="info">
          Saving replaces the whole event — type, title, description, notes and date. Text you clear
          is cleared on the contract, and a type you leave unset resets to <strong>Other</strong>. Who
          recorded this event, and when, is never rewritten.
        </Alert>
      ) : null}
    </Modal>
  );
};

Object.assign(window, { AddContractEventModal });
