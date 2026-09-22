/* AddContractTermModal — New / Edit dialog for a term owned by a CONTRACT.

   The account dialog's twin (AddTermModal), on the same Modal shell and the
   same NewTerm field set — the body a contract POSTs is byte-identical to the
   one an account POSTs, and the owner is never in it: it comes from the route.
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
      const amount = t.unit === 'Percentage'
        ? `${ctmFracToPctStr(t.value)}%`
        : H.money(t.value, t.currency || H.defaultCurrency());
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

const AddContractTermModal = ({ contract, term, existing = [], onClose, onSave }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const isEdit = !!term;

  const COUNT = D.termIntervalCount;

  const [draft, setDraft] = useState(() => ({
    unit: term ? term.unit : 'Amount',
    valueStr: term ? (term.unit === 'Percentage' ? ctmFracToPctStr(term.value) : String(term.value)) : '',
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
  const info = H.termInfo(draft.unit);
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
    if (draft.valueStr === '' || isNaN(raw)) {
      next.value = 'Enter a value.';
    } else if (isPct) {
      if (raw < -100 || raw > 100) next.value = 'Must be between −100% and 100%.';
    } else if (raw < 0) {
      next.value = 'An amount can’t be negative.';
    }

    // The contract rule: an amount needs a currency of its own on the record.
    if (!isPct && !draft.currency) next.currency = 'Pick the currency this amount is in — a contract has no currency of its own.';

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';

    const countRaw = String(draft.intervalCount).trim();
    let count = null;
    if (periodic && countRaw !== '') {
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

    const value = isPct ? Number((raw / 100).toFixed(6)) : Number(raw.toFixed(2));
    onSave({
      contractId: contract.id,
      accountId: null,
      unit: draft.unit,
      value,
      currency: isPct ? null : draft.currency,
      interval: draft.interval || null,
      intervalCount: periodic ? (count == null ? 1 : count) : null,
      anchorDate: draft.anchorDate || null,
      direction: draft.direction,
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
      subtitle={isEdit ? 'Correct this entry in the contract’s price history.' : `Record what ${contract.name} costs, effective from a date.`}
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
            ? <React.Fragment>Joins the price history of <b>{matchedSeries.label}</b> — currently {matchedSeries.note}. This entry supersedes it from the effective date.</React.Fragment>
            : (draft.label.trim()
              ? <React.Fragment>Starts a <b>new charge</b> on this contract, with its own history separate from the others.</React.Fragment>
              : 'Pick a charge this updates, or type a new name to start one.'))}>
          <Combobox
            id="ctm-label"
            freeText
            clearable
            value={draft.label}
            onChange={set('label')}
            options={nameOptions}
            onCreate={(text) => text}
            createLabel="New charge"
            placeholder="e.g. Monthly rent"
            ariaLabel="Name"
            required
            invalid={!!errors.label}
            emptyText={nameOptions.length ? 'No matching charge — type to name a new one' : 'No charges yet — type a name'}
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

      {/* Unit + Value */}
      <div className="trm-value-block">
        <div className="trm-field-head">
          <div className="label" style={{ marginBottom: 0 }}>Value<span className="odc-field-req" aria-hidden="true">*</span></div>
          {(
            <div className="atm-seg" role="radiogroup" aria-label="Unit" style={{ marginLeft: 'auto' }}>
              <button type="button" role="radio" aria-checked={isPct}
                className={`atm-seg-btn ${isPct ? 'on' : ''}`} style={isPct ? { background: info.soft, color: info.color } : {}}
                onClick={() => set('unit')('Percentage')}>
                <MIcon name="percent" size={15} />Percentage
              </button>
              <button type="button" role="radio" aria-checked={!isPct}
                className={`atm-seg-btn ${!isPct ? 'on' : ''}`} style={!isPct ? { background: info.soft, color: info.color } : {}}
                onClick={() => set('unit')('Amount')}>
                <MIcon name="payments" size={15} />Amount
              </button>
            </div>
          )}
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
      </div>

      <FormRow cols={1}>
        <DateField label="Effective from" required value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

      {/* Cadence — the count only once the unit is periodic. */}
      {(
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
