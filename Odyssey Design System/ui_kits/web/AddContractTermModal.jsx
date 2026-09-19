/* AddContractTermModal — New / Edit dialog for a term owned by a CONTRACT.

   The account dialog's twin (AddTermModal), on the same Modal shell and the
   same NewTerm field set — the body a contract POSTs is byte-identical to the
   one an account POSTs, and the owner is never in it: it comes from the route.
   Three rules differ, and they are the whole reason this is its own file:

     • KIND — Fee and InterestRate only, on every ContractType. ExpectedReturn
       prices invested principal, which a contract does not hold, so it is not
       offered (and would be a 400 if posted).
     • CURRENCY — required, and never defaulted. An account lends its own
       currency to an Amount term; a contract has none to lend, so the field is
       mandatory and the dialog says why.
     • ARCHIVED — a contract that is archived refuses every write. The dialog is
       not reachable from a blocked surface; the guard is restated here so a
       stale open dialog cannot post through it.

   Everything else — label normalization, the [-1, 1] percentage bound, the
   cadence pair (interval + count, count only for a periodic unit), the anchor
   date, the (kind, label, effectiveFrom) duplicate guard that is the server's
   409 — is the shared rule, read from the same helpers the account dialog uses.

   On confirm, onSave(dto, id?) receives the term-shaped object (id on edit). */

const CTM_SYM = window.ATM_CURRENCY_SYMBOL || { USD: '$', EUR: '€', GBP: '£', JPY: '¥', NOK: 'kr', SEK: 'kr', CAD: '$' };
const CTM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));

const ctmFracToPctStr = (f) => String(Number((f * 100).toFixed(4)));

const AddContractTermModal = ({ contract, term, existing = [], onClose, onSave }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const isEdit = !!term;

  const eligible = H.conEligibleTermKinds();
  const eligibleKinds = D.termKinds.filter(k => eligible.includes(k.key));
  const initKind = term ? term.kind : 'Fee';
  const initInfo = H.termKindInfo(initKind);
  const COUNT = D.termIntervalCount;

  const [draft, setDraft] = useState(() => ({
    kind: initKind,
    unit: term ? term.unit : initInfo.defaultUnit,
    valueStr: term ? (term.unit === 'Percentage' ? ctmFracToPctStr(term.value) : String(term.value)) : '',
    // No account currency to inherit — an Amount term starts UNSET, and stays
    // a required answer rather than a silently-assigned default.
    currency: term ? (term.currency || '') : '',
    interval: term ? (term.interval || '') : (initInfo.group === 'fee' ? D.defaultFeeInterval : ''),
    intervalCount: term && term.intervalCount != null ? String(term.intervalCount) : '',
    anchorDate: term ? (term.anchorDate || '') : '',
    effectiveFrom: term ? term.effectiveFrom : new Date().toISOString().slice(0, 10),
    label: term ? (term.label || '') : '',
    note: term ? (term.note || '') : '',
  }));
  const [errors, setErrors] = useState({});

  const info = H.termKindInfo(draft.kind);
  const isRate = info.group === 'rate';
  const isPct = draft.unit === 'Percentage';
  const labelRule = H.termLabelRule(draft.kind);
  const periodic = H.intervalIsPeriodic(draft.interval);
  const intervalInfo = H.intervalInfo(draft.interval);

  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  const pickKind = (k) => {
    const ki = H.termKindInfo(k);
    setDraft(d => ({
      ...d,
      kind: k,
      unit: ki.defaultUnit,
      label: H.termLabelRule(k) === 'hidden' ? '' : d.label,
      interval: ki.group === 'fee' ? (d.interval || D.defaultFeeInterval) : '',
      intervalCount: ki.group === 'fee' ? d.intervalCount : '',
      anchorDate: ki.group === 'fee' ? d.anchorDate : '',
    }));
    setErrors({});
  };

  const submit = () => {
    const next = {};
    if (!eligible.includes(draft.kind)) next.kind = 'Not available on a contract.';
    if (contract.archived) next.kind = 'This contract is archived — restore it first.';

    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    if (draft.valueStr === '' || isNaN(raw)) {
      next.value = 'Enter a value.';
    } else if (isPct) {
      if (raw < -100 || raw > 100) next.value = 'Rate must be between −100% and 100%.';
    } else if (raw < 0) {
      next.value = 'A fee amount can’t be negative.';
    }

    // The contract rule: an amount needs a currency, and nothing supplies one.
    if (!isPct && !draft.currency) next.currency = 'Pick the currency this amount is in — a contract has no currency of its own.';

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';

    const countRaw = String(draft.intervalCount).trim();
    let count = null;
    if (!isRate && periodic && countRaw !== '') {
      count = parseInt(countRaw, 10);
      if (isNaN(count) || count < COUNT.min || count > COUNT.max) {
        next.intervalCount = `Enter a whole number between ${COUNT.min} and ${COUNT.max}.`;
      }
    }
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    const label = labelRule === 'hidden' ? null : H.termLabelNormalize(draft.label);
    if (labelRule === 'required' && !label) next.label = 'Name this charge so it keeps its own history.';
    if (label && label.length > 64) next.label = 'Keep the name under 64 characters.';

    // Duplicate (kind, label, effectiveFrom) within THIS contract's series → 409.
    // An account term with the same kind, label and date is a different series
    // and never collides with it.
    const key = H.termLabelKey(label);
    const dup = existing.some(t =>
      t.id !== (term && term.id) && t.kind === draft.kind
      && (t.labelKey || H.termLabelKey(t.label) || null) === (key || null)
      && t.effectiveFrom === draft.effectiveFrom);
    if (dup) next.effectiveFrom = label
      ? `“${label}” already has an entry on that date.`
      : 'This contract already has an interest rate on that date.';

    if (Object.keys(next).length) { setErrors(next); return; }

    const value = isPct ? Number((raw / 100).toFixed(6)) : Number(raw.toFixed(2));
    onSave({
      contractId: contract.id,
      accountId: null,
      kind: draft.kind,
      unit: draft.unit,
      value,
      currency: isPct ? null : draft.currency,
      interval: isRate ? null : (draft.interval || null),
      intervalCount: !isRate && periodic ? (count == null ? 1 : count) : null,
      anchorDate: isRate ? null : (draft.anchorDate || null),
      effectiveFrom: draft.effectiveFrom,
      label,
      labelKey: key,
      note: draft.note.trim() || null,
    }, term && term.id);
  };

  const cadence = isRate ? null : H.cadenceText(draft.interval, draft.intervalCount === '' ? 1 : parseInt(draft.intervalCount, 10));
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

      {/* Kind — two eligible values on every contract type, so the picker is
         always a real choice (unlike the account dialog, which drops it when the
         account type leaves one). Locked on edit: the owner and the series key
         are what the route already named. */}
      <div className="field">
        <div className="label">Term<span className="odc-field-req" aria-hidden="true">*</span></div>
        {isEdit ? (
          <div className="trm-kind-opt on" style={{ cursor: 'default' }}>
            <span className="trm-kind-ic md" style={{ background: info.soft, color: info.color }}>
              <MIcon name={info.icon} size={18} />
            </span>
            <span className="trm-kind-opt-txt">
              <span className="trm-kind-opt-name">{info.label}</span>
              <span className="trm-kind-opt-grp">{info.group === 'rate' ? 'Rate' : 'Fee'}</span>
            </span>
          </div>
        ) : (
          <React.Fragment>
            <CardSelect ariaLabel="Term" value={draft.kind} onChange={pickKind}
              options={eligibleKinds.map(k => ({ value: k.key, label: k.label, icon: k.icon, color: k.color, soft: k.soft }))} />
            <div className="trm-kind-ineligible">
              A contract can carry a <b>fee</b> or an <b>interest rate</b>. Expected return prices invested principal, which a contract doesn’t hold.
            </div>
          </React.Fragment>
        )}
        {errors.kind && <div className="helper aam-err">{errors.kind}</div>}
      </div>

      {/* Name — the series label. Required on every fee; refused on a rate. */}
      {labelRule !== 'hidden' && (
        <Field
          label="Name"
          required={labelRule === 'required'}
          value={draft.label}
          onChange={set('label')}
          placeholder="e.g. Monthly rent"
          error={errors.label}
          help="Names this charge so it keeps its own history, separate from the contract's other charges."
        />
      )}

      {/* Unit + Value */}
      <div className="trm-value-block">
        <div className="trm-field-head">
          <div className="label" style={{ marginBottom: 0 }}>Value<span className="odc-field-req" aria-hidden="true">*</span></div>
          {!isRate && (
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
            error={errors.value}
            help={<React.Fragment>Stored as a fraction: <b>{previewFrac == null ? '—' : previewFrac.toFixed(4)}</b>{isRate ? ' · annual' : ''}</React.Fragment>}
          />
        ) : (
          <MoneyField
            size="lg"
            required
            allowNegative
            signEditable
            autoFocus
            value={draft.valueStr}
            onChange={set('valueStr')}
            currency={draft.currency}
            onCurrencyChange={set('currency')}
            currencyOptions={CTM_CURRENCIES}
            currencySearchThreshold={0}
            currencyPlaceholder="Pick"
            error={[errors.value, errors.currency].filter(Boolean).join(' ') || undefined}
            help={errors.value || errors.currency ? undefined : (draft.currency
              ? <React.Fragment>Flat amount in <b>{draft.currency}</b>{cadence ? <React.Fragment> · {cadence}</React.Fragment> : ''}</React.Fragment>
              : <React.Fragment>A contract has no currency of its own — <b>pick one</b> for this amount.</React.Fragment>)}
          />
        )}
      </div>

      <FormRow cols={1}>
        <DateField label="Effective from" required value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

      {/* Cadence — fees only; the count only once the unit is periodic. */}
      {!isRate && (
        <div className="trm-cadence">
          <FormRow cols={periodic ? 2 : 1}>
            <FieldShell label="Billing interval"
              help={periodic ? `Charged ${cadence}` : (intervalInfo && intervalInfo.key !== 'OneTime' ? `Charged ${intervalInfo.adverb}` : undefined)}>
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
