/* AddTermModal — New / Edit dialog for an account Term.

   There is no term kind. Every term is one shape, named by its Label, and keeps
   its own dated history — a rate is simply a term priced as a Percentage.
   Field set mirrors the NewTerm DTO:

     • Label           — REQUIRED; the series name. Normalized (trim + collapse
                         whitespace) by the shared rule; ≤ 64 chars.
     • ValueUnit       — Percentage | Amount.
     • Value           — Percentage: typed as a percent, stored as a fraction in
                         [-1, 1] (3.40 → 0.0340; negative allowed). Amount: ≥ 0.
     • CurrencyCode    — required for Amount (defaults to the account currency);
                         null for Percentage.
     • Interval        — optional cadence unit: OneTime / PerOccurrence / PerUnit /
                         Daily / Weekly / Monthly / Annually.
     • IntervalCount   — 1…1000, only for a periodic unit; null otherwise.
     • AnchorDate      — optional: when the term is FIRST BILLED.
     • EffectiveFrom   — required; past or future allowed (future = scheduled).
     • Note            — optional, ≤ 512 chars.

   Rejects a (Label, EffectiveFrom) duplicate on the case-folded label key (the
   server's 409). On confirm, onSave(dto, id?) receives the term-shaped object. */

const TRM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));

const trmDefaultInterval = () => window.OdysseyData.defaultFeeInterval;
const TRM_COUNT = window.OdysseyData.termIntervalCount; // { min: 1, max: 1000 }

const fracToPctStr = (f) => String(Number((f * 100).toFixed(4)));

const AddTermModal = ({ account, term, existing = [], onClose, onSave }) => {
  const { useState } = React;
  const isEdit = !!term;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  const [draft, setDraft] = useState(() => ({
    unit: term ? term.unit : 'Amount',
    valueStr: term ? (term.unit === 'Percentage' ? fracToPctStr(term.value) : String(term.value)) : '',
    currency: term ? (term.currency || account.currency || 'USD') : (account.currency || 'USD'),
    interval: term ? (term.interval || '') : trmDefaultInterval(),
    intervalCount: term && term.intervalCount != null ? String(term.intervalCount) : '',
    anchorDate: term ? (term.anchorDate || '') : '',
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
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    if (draft.valueStr === '' || isNaN(raw)) next.value = 'Enter a value.';
    else if (isPct) { if (raw < -100 || raw > 100) next.value = 'Must be between −100% and 100%.'; }
    else if (raw < 0) next.value = 'An amount can’t be negative.';

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';

    const countRaw = String(draft.intervalCount).trim();
    let count = null;
    if (periodic && countRaw !== '') {
      count = parseInt(countRaw, 10);
      if (isNaN(count) || count < TRM_COUNT.min || count > TRM_COUNT.max) {
        next.intervalCount = `Enter a whole number between ${TRM_COUNT.min} and ${TRM_COUNT.max}.`;
      }
    }
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    const label = H.termLabelNormalize(draft.label);
    if (!label) next.label = 'Name this term so it keeps its own history.';
    else if (label.length > 64) next.label = 'Keep the name under 64 characters.';

    const key = H.termLabelKey(label);
    const dup = label && existing.some(t =>
      t.id !== (term && term.id)
      && H.termSeriesKey(t) === key
      && t.effectiveFrom === draft.effectiveFrom);
    if (dup) next.effectiveFrom = `“${label}” already has an entry on that date.`;

    if (Object.keys(next).length) { setErrors(next); return; }

    const value = isPct ? Number((raw / 100).toFixed(6)) : Number(raw.toFixed(2));
    onSave({
      unit: draft.unit,
      value,
      currency: isPct ? null : draft.currency,
      interval: draft.interval || null,
      intervalCount: periodic ? (count == null ? 1 : count) : null,
      anchorDate: draft.anchorDate || null,
      effectiveFrom: draft.effectiveFrom,
      label,
      labelKey: key,
      note: draft.note.trim() || null,
    }, term && term.id);
  };

  const cadence = H.cadenceText(draft.interval, draft.intervalCount === '' ? 1 : parseInt(draft.intervalCount, 10));
  const previewFrac = (() => {
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    return isNaN(raw) ? null : raw / 100;
  })();

  return (
    <Modal
      title={isEdit ? 'Edit term' : 'New term'}
      subtitle={isEdit ? 'Correct this entry.' : `Record a term on ${account.name}, effective from a date.`}
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

      <Field
        label="Name"
        required
        value={draft.label}
        onChange={set('label')}
        placeholder="e.g. Interest rate · ATM withdrawal abroad"
        error={errors.label}
        help="Names this term so it keeps its own history, separate from the account's other terms."
      />

      <div className="trm-value-block">
        <div className="trm-field-head">
          <div className="label" style={{ marginBottom: 0 }}>Value<span className="odc-field-req" aria-hidden="true">*</span></div>
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
        </div>

        {isPct ? (
          <AmountField
            size="lg"
            required
            suffix="%"
            allowNegative
            value={draft.valueStr}
            onChange={set('valueStr')}
            error={errors.value}
            help={<React.Fragment>Stored as a fraction: <b>{previewFrac == null ? '—' : previewFrac.toFixed(4)}</b>{cadence ? <React.Fragment> · {cadence}</React.Fragment> : ''}</React.Fragment>}
          />
        ) : (
          <MoneyField
            size="lg"
            required
            allowNegative
            signEditable
            value={draft.valueStr}
            onChange={set('valueStr')}
            currency={draft.currency}
            onCurrencyChange={set('currency')}
            currencyOptions={TRM_CURRENCIES}
            currencySearchThreshold={0}
            error={errors.value}
            help={<React.Fragment>Flat amount in <b>{draft.currency}</b>{cadence ? <React.Fragment> · {cadence}</React.Fragment> : ''}</React.Fragment>}
          />
        )}
      </div>

      {/* Direction is a CONTRACT-term field; an account term is always money out. */}
      <div className="trm-dir-refused">
        <MIcon name="block" size={15} />
        <span>An account term is always <b>money out</b>. Direction — money in or out — is recorded on a <b>contract</b> term.</span>
      </div>

      <FormRow cols={1}>
        <DateField label="Effective from" required value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

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
              min={TRM_COUNT.min}
              max={TRM_COUNT.max}
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
          <div className="helper trm-cadence-echo">Name the unit in the term’s name — “Custody · per share”.</div>
        )}
        <DateField label="First billed on" value={draft.anchorDate} onChange={set('anchorDate')}
          helper="When this is first actually charged, if that isn’t the effective date" />
      </div>

      <NoteField label="Note" maxLength={512} value={draft.note} onChange={set('note')}
        placeholder="What changed, and why — e.g. “Fed cut pass-through”."
        error={errors.note} />
    </Modal>
  );
};

Object.assign(window, { AddTermModal });
