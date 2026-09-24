/* AddContractTermModal — New / Edit dialog for a term owned by a CONTRACT.

   The only term dialog — account terms were retired (MoveAccountTermsToContracts).
   Built on the shared Modal shell and the NewTerm field set; the owner is never
   in the body: it comes from the route.
   Three rules differ, and they are the whole reason this is its own file:

     • CURRENCY — required, and prefilled from the user's DEFAULT CURRENCY. An
       account lends its own currency to an Amount term; a contract has none to
       lend, so the preference stands in for it: the common case is one keystroke
       shorter, and a term priced in another currency is one pick away. The field
       stays required — the preference is a default, not an assumption, and
       clearing it still refuses the write.
     • ARCHIVED — a contract that is archived refuses every write. The dialog is
       not reachable from a blocked surface; the guard is restated here so a
       stale open dialog cannot post through it.

   Everything else — label normalization, the [-1, 1] percentage bound, the
   cadence pair (interval + count, count only for a periodic unit), the anchor
   date, the (label, effectiveFrom) duplicate guard that is the server's
   409 — is the shared rule, read from the same helpers the account dialog uses.

   FOUR KINDS (TextAndDateTimeTermKinds). Percentage and Amount are priced; Text
   and DateTime record a fact. The kind picker leads the form because it decides
   which fields exist at all: on Text / DateTime the direction, currency, cadence
   and first-billed date are not hidden-but-kept — they are NOT SENT. The server
   refuses each one with its own 400 rather than clearing it, so a kind change on
   edit must null them here, and the dialog says it is doing so.

   `kinds` narrows the picker. The compatibility release (backend issue, Goal 9)
   passes ['Percentage', 'Amount']; the full release passes all four.

   On confirm, onSave(dto, id?) receives the term-shaped object (id on edit). */

const CTM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));

const ctmFracToPctStr = (f) => String(Number((f * 100).toFixed(4)));

/* ---- The name field's suggestions ----

   There is no term-name table to read: a name is a free string, and the series
   it keys — (owner, kind, labelKey) — exists only because entries share it. So
   the list is DERIVED from this contract's own term history, one row per
   distinct labelKey of the kind being written, and nothing else: a name from
   another contract is not a name this one has used, and offering it invites a
   series that starts by looking like a correction to something it isn't.

   Each row carries what the reader needs to recognise the series rather than
   the string — its value in force and its cadence ('2,250 USD · monthly'). The
   note is derived from the LATEST entry on or before today; a series whose only
   entries are future-dated reads 'scheduled', and a one-time charge already in
   the past reads as the one-off it was. The design system has no "ended" flag
   on a term, so nothing here claims one.

   Picking a row and typing a new name are two different writes, and the help
   line under the field says which one is about to happen. Both remain legal:
   the field suggests, it never constrains. */
const ctmNameOptions = (existing, currentId) => {
  const H = window.OdysseyHelpers;
  const today = new Date().toISOString().slice(0, 10);
  const bySeries = {};
  for (const t of existing) {
    if (t.id === currentId) continue;
    const key = t.labelKey || H.termLabelKey(t.label);
    if (!key) continue;
    const s = bySeries[key] || (bySeries[key] = { key, label: t.label, entries: [] });
    s.entries.push(t);
    // The display form follows the newest entry — a later spelling is the
    // one the user last chose to write.
    if (t.effectiveFrom >= (s.newest || '')) { s.newest = t.effectiveFrom; s.label = t.label; }
  }
  return Object.values(bySeries)
    .sort((a, b) => a.label.localeCompare(b.label))
    .map(s => {
      const sorted = s.entries.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? -1 : 1));
      const inForce = sorted.filter(t => t.effectiveFrom <= today).pop();
      const t = inForce || sorted[sorted.length - 1];
      const raw = H.fmtTermValueFor(t);
      const amount = t.unit === 'Text' ? `“${raw.length > 36 ? raw.slice(0, 35) + '…' : raw}”` : raw;
      const cadence = H.cadenceTextFor(t);
      const parts = [amount];
      if (cadence) parts.push(cadence);
      else if (t.interval === 'OneTime') parts.push('one-time');
      if (!inForce) parts.push(`from ${t.effectiveFrom}`);
      return {
        value: s.label,
        label: s.label,
        icon: inForce ? undefined : 'schedule',
        note: parts.join(' · '),
      };
    });
};

const CTM_ALL_KINDS = ['Percentage', 'Amount', 'Text', 'DateTime'];

/* The server's messages (§9), verbatim in meaning — and never the text itself. */
const CTM_TEXT_ERR = {
  empty: 'Enter the text this term records.',
  long: `Keep it to ${window.OdysseyData.termTextMaxLength} characters.`,
  control: 'Remove tabs, line breaks and hidden direction marks.',
};

const AddContractTermModal = ({ contract, term, existing = [], onClose, onSave, kinds = CTM_ALL_KINDS, initialUnit }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const isEdit = !!term;

  const COUNT = D.termIntervalCount;

  const [draft, setDraft] = useState(() => ({
    unit: initialUnit || (term ? term.unit : 'Amount'),
    valueStr: term && term.value != null ? (term.unit === 'Percentage' ? ctmFracToPctStr(term.value) : String(term.value)) : '',
    textValue: term && term.textValue ? term.textValue : '',
    dtDate: term && term.dateTimeValue ? H.termUtcToLocal(term.dateTimeValue).date : '',
    dtTime: term && term.dateTimeValue ? H.termUtcToLocal(term.dateTimeValue).time : '',
    // No account currency to inherit, so the USER'S DEFAULT stands in for one.
    // Still required: a cleared field refuses the write.
    currency: term ? (term.currency || '') : H.defaultCurrency(),
    interval: term ? (term.interval || '') : D.defaultFeeInterval,
    intervalCount: term && term.intervalCount != null ? String(term.intervalCount) : '',
    anchorDate: term ? (term.anchorDate || '') : '',
    // Which way the money moves. Outgoing is the default because it is what
    // every term meant before the field existed — an omitted direction and a
    // chosen Outgoing are the same fact.
    direction: term ? H.termDirection(term) : 'Outgoing',
    effectiveFrom: term ? term.effectiveFrom : new Date().toISOString().slice(0, 10),
    label: term ? (term.label || '') : '',
    note: term ? (term.note || '') : '',
  }));
  const [errors, setErrors] = useState({});

  const isPct = draft.unit === 'Percentage';
  const isText = draft.unit === 'Text';
  const isDt = draft.unit === 'DateTime';
  const numeric = H.termIsNumeric(draft.unit);
  const info = H.termInfo(draft.unit);
  const dtUtc = isDt ? H.termLocalToUtc(draft.dtDate, draft.dtTime) : null;
  const zone = H.localZone(dtUtc);
  const textTrimmed = draft.textValue.trim();
  const textProblem = isText && draft.textValue !== '' ? H.termTextProblem(draft.textValue) : null;
  const periodic = H.intervalIsPeriodic(draft.interval);
  const intervalInfo = H.intervalInfo(draft.interval);

  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };


  const submit = () => {
    const next = {};
    if (contract.archived) next.archived = 'This contract is archived — restore it first.';

    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    if (numeric) {
      if (draft.valueStr === '' || isNaN(raw)) {
        next.value = 'Enter a value.';
      } else if (isPct) {
        if (raw < -100 || raw > 100) next.value = 'Must be between −100% and 100%.';
      } else if (raw < 0) {
        next.value = 'An amount can’t be negative.';
      }
      // The contract rule: an amount needs a currency of its own on the record.
      if (!isPct && !draft.currency) next.currency = 'Pick the currency this amount is in — a contract has no currency of its own.';
    } else if (isText) {
      const p = H.termTextProblem(draft.textValue);
      if (p) next.textValue = CTM_TEXT_ERR[p];
    } else if (isDt) {
      if (!draft.dtDate || !draft.dtTime) next.dateTimeValue = 'Pick both a date and a time.';
      else {
        const R = D.termDateTimeRange;
        if (!dtUtc || dtUtc < R.min || dtUtc > R.max) next.dateTimeValue = 'Pick a date between 1900 and 2200.';
      }
    }

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';

    const countRaw = String(draft.intervalCount).trim();
    let count = null;
    if (numeric && periodic && countRaw !== '') {
      count = parseInt(countRaw, 10);
      if (isNaN(count) || count < COUNT.min || count > COUNT.max) {
        next.intervalCount = `Enter a whole number between ${COUNT.min} and ${COUNT.max}.`;
      }
    }
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    const label = H.termLabelNormalize(draft.label);
    if (!label) next.label = 'Name this term so it keeps its own history.';
    else if (label.length > 64) next.label = 'Keep the name under 64 characters.';

    // Duplicate (label, effectiveFrom) within THIS contract's series → 409.
    // An account term with the same label and date is a different series
    // and never collides with it.
    const key = H.termLabelKey(label);
    const dup = label && existing.some(t =>
      t.id !== (term && term.id)
      && H.termSeriesKey(t) === key
      && t.effectiveFrom === draft.effectiveFrom);
    if (dup) next.effectiveFrom = `“${label}” already has an entry on that date.`;

    if (Object.keys(next).length) { setErrors(next); return; }

    const value = !numeric ? null : isPct ? Number((raw / 100).toFixed(6)) : Number(raw.toFixed(2));
    /* Exactly one value field is set, and every field that does not apply to
       the kind goes out as null (direction as Outgoing) — never left over from
       a kind the entry used to be. */
    onSave({
      contractId: contract.id,
      accountId: null,
      unit: draft.unit,
      value,
      textValue: isText ? textTrimmed : null,
      // Always an instant with Z — the server refuses a time with no offset.
      dateTimeValue: isDt ? dtUtc : null,
      currency: numeric && !isPct ? draft.currency : null,
      interval: numeric ? (draft.interval || null) : null,
      intervalCount: numeric && periodic ? (count == null ? 1 : count) : null,
      anchorDate: numeric ? (draft.anchorDate || null) : null,
      direction: numeric ? draft.direction : 'Outgoing',
      effectiveFrom: draft.effectiveFrom,
      label,
      labelKey: key,
      note: draft.note.trim() || null,
    }, term && term.id);
  };

  /* The name field's two outcomes, resolved on the SAME key the duplicate guard
     and the server use — so what the help line promises is what gets written. */
  const nameOptions = ctmNameOptions(existing, term && term.id);
  const draftKey = H.termLabelKey(draft.label);
  const matchedSeries = draftKey ? nameOptions.find(o => H.termLabelKey(o.value) === draftKey) : null;

  const cadence = H.cadenceText(draft.interval, draft.intervalCount === '' ? 1 : parseInt(draft.intervalCount, 10));
  const dirInfo = H.termDirectionInfo(draft.direction);
  /* The money field's lead flips between these two, showing each one's own
     SHORT WORD where a sign would be — the registry, mapped to MoneyField's
     shape. No arrow: see termDirections on why the glyph was dropped. */
  const DIR_OPTIONS = D.termDirections.map(d => ({ value: d.key, label: d.label, short: d.short, tone: d.tone }));
  const previewFrac = (() => {
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    return isNaN(raw) ? null : raw / 100;
  })();

  return (
    <Modal
      title={isEdit ? 'Edit term' : 'New term'}
      subtitle={isEdit ? 'Correct this entry in the contract’s term history.' : `Record a price, a rate or a fact of ${contract.name}, effective from a date.`}
      icon={isEdit ? 'edit' : '§'}
      className="trm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={isEdit ? 'check' : 'add'} onClick={submit}>
            {isEdit ? 'Save changes' : 'Create term'}
          </Button>
        </React.Fragment>
      }>

      {errors.archived && <div className="helper aam-err">{errors.archived}</div>}

      {/* Name — the series label. Required on every fee; refused on a rate.

         A free string, but not an unaided one: the field suggests the names
         this contract has already used for this kind, because the name is what
         decides whether this entry JOINS an existing price history or STARTS
         one. Typing it exactly was the only way to join before; now it is a
         pick, and the help line names the consequence either way. `freeText`
         keeps a new name a first-class answer rather than a fallback. */}
      {(
        <FieldShell
          label="Name"
          htmlFor="ctm-label"
          required
          error={errors.label}
          help={errors.label ? undefined : (matchedSeries
            ? <React.Fragment>Joins the history of <b>{matchedSeries.label}</b> — currently {matchedSeries.note}. This entry supersedes it from the effective date.</React.Fragment>
            : (draft.label.trim()
              ? <React.Fragment>Starts a <b>new term</b> on this contract, with its own history separate from the others.</React.Fragment>
              : 'Pick a term this updates, or type a new name to start one.'))}>
          <Combobox
            id="ctm-label"
            freeText
            clearable
            value={draft.label}
            onChange={set('label')}
            options={nameOptions}
            onCreate={(text) => text}
            createLabel="New term"
            placeholder="e.g. Monthly rent, Notice period"
            ariaLabel="Name"
            required
            invalid={!!errors.label}
            emptyText={nameOptions.length ? 'No matching term — type to name a new one' : 'No terms yet — type a name'}
          />
        </FieldShell>
      )}

      {/* Direction — which way the money moves, from the HOUSEHOLD's side, not
         from either named party's. It rides in the VALUE control itself, in the
         slot a sign would occupy: the record stores a direction, not a sign, so
         the word is the sign (see MoneyField / AmountField `directionOptions`).
         Both units carry the same lead, so there is exactly ONE control for the
         value whichever way it is priced — and a rate is asked the same way a
         fee is, because an arrears rate charges and a deposit rate pays. */}

      {/* Kind — first, because it decides which fields exist below. */}
      <FieldShell label="Kind" required>
        <div className="trm-kind-seg" role="radiogroup" aria-label="Kind">
          {kinds.map(k => {
            const ki = H.termInfo(k);
            const on = draft.unit === k;
            return (
              <button key={k} type="button" role="radio" aria-checked={on}
                style={on ? { background: ki.soft, color: ki.color } : undefined}
                onClick={() => { set('unit')(k); setErrors({}); }}>
                <MIcon name={ki.icon} size={16} />{ki.short || ki.label}
              </button>
            );
          })}
        </div>
      </FieldShell>
      {isEdit && term.unit !== draft.unit && (
        <div className="trm-kind-switch">
          Changes this entry from <b>{H.termInfo(term.unit).label.toLowerCase()}</b> to <b>{info.label.toLowerCase()}</b>.
          {H.termIsNumeric(term.unit) && !numeric ? ' Its value, currency and cadence are removed.' : ''} The name keeps its history.
        </div>
      )}

      <div className="trm-value-block">
        {isText ? (
          <FieldShell label="Text" htmlFor="ctm-text" required
            error={errors.textValue || (textProblem === 'control' ? CTM_TEXT_ERR.control : undefined)}
            aside={<span className={`trm-text-count${textTrimmed.length > D.termTextMaxLength ? ' over' : ''}`}>{textTrimmed.length} / {D.termTextMaxLength}</span>}
            help="One line of plain text, as the contract words it.">
            <div className="odc-input-wrap">
              <input id="ctm-text" className="odc-input" type="text" autoFocus
                value={draft.textValue} onChange={(e) => set('textValue')(e.target.value)}
                aria-invalid={!!(errors.textValue || textProblem === 'control')}
                placeholder="e.g. 3 months, to the end of a month" />
            </div>
          </FieldShell>
        ) : isDt ? (
          <div>
            <FormRow cols={2}>
              <DateField label="Date" required value={draft.dtDate} onChange={(v) => { set('dtDate')(v); setErrors(e => ({ ...e, dateTimeValue: undefined })); }} />
              <TimeField label="Time" required step={30} value={draft.dtTime} onChange={(v) => { set('dtTime')(v || ''); setErrors(e => ({ ...e, dateTimeValue: undefined })); }} />
            </FormRow>
            {errors.dateTimeValue
              ? <div className="helper aam-err">{errors.dateTimeValue}</div>
              : <div className="helper">
                  In your time zone, <b>{zone.name}</b> ({zone.offset}){dtUtc ? <React.Fragment>. Saved as <span className="trm-dt-utc">{H.termUtcStamp(dtUtc)}</span>.</React.Fragment> : '.'}
                </div>}
          </div>
        ) : (
        <React.Fragment>
        <div className="trm-field-head">
          <div className="label" style={{ marginBottom: 0 }}>Value<span className="odc-field-req" aria-hidden="true">*</span></div>
        </div>
        {isPct ? (
          <AmountField
            size="lg"
            required
            suffix="%"
            allowNegative
            autoFocus
            value={draft.valueStr}
            onChange={set('valueStr')}
            /* A percentage is money too — a fee priced as a share, or a rate
               that charges or pays — so it carries the same direction lead as
               an amount: one control with one unit swapped, never two
               differently-shaped questions. */
            direction={draft.direction}
            onDirectionChange={set('direction')}
            directionOptions={DIR_OPTIONS}
            tone={dirInfo.tone}
            error={errors.value}
            help={errors.value ? undefined : (
              <React.Fragment>
                <b>{dirInfo.label}</b> — {dirInfo.sentence}. Click <b>{dirInfo.short}</b> to switch.{' '}
                Stored as a fraction: <b>{previewFrac == null ? '—' : previewFrac.toFixed(4)}</b>{cadence ? ` · ${cadence}` : ''}
              </React.Fragment>
            )}
          />
        ) : (
          <MoneyField
            size="lg"
            required
            allowNegative={false}
            autoFocus
            value={draft.valueStr}
            onChange={set('valueStr')}
            /* The lead is the DIRECTION, not a sign: the amount stays positive
               and the word carries the meaning — one control for the value, the
               way it is one field on the record. */
            direction={draft.direction}
            onDirectionChange={set('direction')}
            directionOptions={DIR_OPTIONS}
            tone={dirInfo.tone}
            currency={draft.currency}
            onCurrencyChange={set('currency')}
            currencyOptions={CTM_CURRENCIES}
            currencySearchThreshold={0}
            currencyPlaceholder="Pick"
            error={[errors.value, errors.currency, errors.direction].filter(Boolean).join(' ') || undefined}
            help={errors.value || errors.currency || errors.direction ? undefined : (draft.currency
              ? <React.Fragment>
                  <b>{dirInfo.label}</b> — {dirInfo.sentence}. Flat amount in <b>{draft.currency}</b>{cadence ? <React.Fragment> · {cadence}</React.Fragment> : ''}.
                  {' '}Click <b>{dirInfo.short}</b> to switch{isEdit ? ' — a correction supersedes the entry, it never forks the history' : ''}.
                </React.Fragment>
              : <React.Fragment>A contract has no currency of its own — <b>pick one</b> for this amount. The word on the left says which way the money moves.</React.Fragment>)}
          />
        )}
        </React.Fragment>
        )}
      </div>

      {!numeric && (
        <div className="trm-na-note">
          <MIcon name="info" size={16} />
          <span>{isText ? 'A text term' : 'A date-time term'} records a fact, not a charge. It has no direction, currency or cadence, and it doesn’t count toward the contract’s run rate.</span>
        </div>
      )}

      <FormRow cols={1}>
        <DateField label="Effective from" required value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

      {/* Cadence — the count only once the unit is periodic. Numeric kinds only. */}
      {numeric && (
        <div className="trm-cadence">
          <FormRow cols={periodic ? 2 : 1}>
            <FieldShell label="Interval"
              help={periodic ? `Applies ${cadence}` : (intervalInfo && intervalInfo.key !== 'OneTime' ? `Applies ${intervalInfo.adverb}` : undefined)}>
              <Select value={draft.interval} onChange={set('interval')}
                options={[{ value: '', label: 'Not specified' }, ...D.intervals.map(b => ({ value: b.key, label: b.label, icon: b.icon, iconColor: b.color }))]} />
            </FieldShell>
            {periodic && (
              <NumberField
                label="Every"
                min={COUNT.min}
                max={COUNT.max}
                step={1}
                unit={intervalInfo ? intervalInfo.many : ''}
                placeholder="1"
                value={draft.intervalCount === '' ? null : Number(draft.intervalCount)}
                onChange={(v) => set('intervalCount')(v == null ? '' : String(v))}
                error={errors.intervalCount}
                help={errors.intervalCount ? undefined : `Leave blank for ${intervalInfo.adverb}`} />
            )}
          </FormRow>
          {draft.interval === 'PerUnit' && (
            <div className="helper trm-cadence-echo">Name the unit in the charge’s name — “Storage · per pallet”.</div>
          )}
          <DateField label="First billed on" value={draft.anchorDate} onChange={set('anchorDate')}
            helper="When this is first actually charged, if that isn’t the effective date" />
        </div>
      )}

      <NoteField label="Note" maxLength={512} value={draft.note} onChange={set('note')}
        placeholder="What changed, and why — e.g. “Indexed to CPI, revised each October”."
        error={errors.note} />
    </Modal>
  );
};

Object.assign(window, { AddContractTermModal });
