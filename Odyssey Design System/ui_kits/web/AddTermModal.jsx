/* AddTermModal — New / Edit dialog for a Term (interest-rate & fee history).

   Opened from the "Terms" section (Accounts → account detail). Built on the
   shared DS Modal shell, like every other create/edit dialog. Field set mirrors
   the NewTerm DTO and enforces the spec's validation:

     • TermKind        — eligibility-gated by the account's AccountType (matrix in
                         data.js). Three values: InterestRate (interest-bearing
                         accounts), ExpectedReturn (investment/pension) and Fee
                         (everywhere). Where only ONE kind is eligible — cash,
                         property, vehicle, any type with no rate — the picker is
                         not rendered at all and the form opens on that kind.
     • Label           — the series name. Refused on rate kinds, REQUIRED on every
                         fee. Normalized (trim + collapse whitespace) by the
                         shared rule; ≤ 64 chars.
     • ValueUnit       — Percentage | Amount. Locked to Percentage for rate kinds.
     • Value           — Percentage: typed as a percent, stored as a fraction in
                         [-1, 1] (3.40 → 0.0340; negative allowed). Amount: ≥ 0.
     • CurrencyCode    — required for Amount (defaults to the account currency);
                         null for Percentage.
     • Interval        — optional context for fees; null for rate kinds. The
                         cadence UNIT only: OneTime / PerOccurrence / PerUnit /
                         Daily / Weekly / Monthly / Annually. Quarterly is gone —
                         it is Monthly with a count of 3.
     • IntervalCount   — the multiplier, 1…1000. Offered, and written, ONLY for a
                         periodic unit; null in every other case (including a
                         null interval), never a meaningless 1.
     • AnchorDate      — optional, fees only: when the term is FIRST BILLED, as
                         opposed to when its price took effect. No ordering
                         against EffectiveFrom is imposed — arrears and prepaid
                         are both legitimate records.
     • EffectiveFrom   — required; past or future allowed (future = scheduled).
     • Note            — optional, ≤ 512 chars.

   Rejects a (TermKind, Label, EffectiveFrom) duplicate on the case-folded label
   key (the server's 409). On confirm, onSave(dto, id?) receives the term-shaped
   object (id present on edit). */

/* Money adornments are the ISO CODE, not a symbol — see MoneyField. */
const TRM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));

/* One default interval for a new fee — there is no longer a fee kind to
   guess from, and the four kind-specific guesses went away with the kinds. */
const trmDefaultInterval = () => window.OdysseyData.defaultFeeInterval;
const TRM_COUNT = window.OdysseyData.termIntervalCount; // { min: 1, max: 1000 }

/* percent fraction → editable percent string ("0.0340" → "3.4") */
const fracToPctStr = (f) => {
  const p = f * 100;
  return String(Number(p.toFixed(4)));
};

const AddTermModal = ({ account, term, existing = [], initialKind, onClose, onSave }) => {
  const { useState } = React;
  const isEdit = !!term;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  const eligible = H.eligibleTermKinds(account.type);
  const eligibleKinds = D.termKinds.filter(k => eligible.includes(k.key));

  const initKind = term ? term.kind
    : (initialKind && eligible.includes(initialKind)) ? initialKind
    : (eligibleKinds[0] && eligibleKinds[0].key) || 'Fee';
  const initInfo = H.termKindInfo(initKind);

  const [draft, setDraft] = useState(() => ({
    kind: initKind,
    unit: term ? term.unit : initInfo.defaultUnit,
    valueStr: term ? (term.unit === 'Percentage' ? fracToPctStr(term.value) : String(term.value)) : '',
    currency: term ? (term.currency || account.currency || 'USD') : (account.currency || 'USD'),
    interval: term ? (term.interval || '') : (initInfo.group === 'fee' ? trmDefaultInterval() : ''),
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
  const labelRule = H.termLabelRule(draft.kind); // hidden | optional | required
  // The one condition the cadence fields hang off: a count exists only for a
  // periodic unit, so the field is not merely disabled — it is not there.
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
      // A rate kind refuses a label, so a typed one is discarded on the switch.
      label: H.termLabelRule(k) === 'hidden' ? '' : d.label,
      interval: ki.group === 'fee' ? (d.interval || trmDefaultInterval()) : '',
      // A rate is not billed, so it carries neither half of a billing description.
      intervalCount: ki.group === 'fee' ? d.intervalCount : '',
      anchorDate: ki.group === 'fee' ? d.anchorDate : '',
    }));
    setErrors({});
  };

  const submit = () => {
    const next = {};
    if (!draft.kind) next.kind = 'Choose what this term is.';
    if (!H.isTermKindEligible(draft.kind, account.type)) next.kind = 'Not available for this account type.';

    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    if (draft.valueStr === '' || isNaN(raw)) {
      next.value = 'Enter a value.';
    } else if (isPct) {
      if (raw < -100 || raw > 100) next.value = 'Rate must be between −100% and 100%.';
    } else if (raw < 0) {
      next.value = 'A fee amount can’t be negative.';
    }

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';

    // IntervalCount range — the same bound as the DTO's [Range], which the
    // service re-checks for callers that never pass through model binding.
    const countRaw = String(draft.intervalCount).trim();
    let count = null;
    if (!isRate && periodic && countRaw !== '') {
      count = parseInt(countRaw, 10);
      if (isNaN(count) || count < TRM_COUNT.min || count > TRM_COUNT.max) {
        next.intervalCount = `Enter a whole number between ${TRM_COUNT.min} and ${TRM_COUNT.max}.`;
      }
    }
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    // Label rules — refused on rate kinds, required on every fee, ≤ 64 chars.
    const label = labelRule === 'hidden' ? null : H.termLabelNormalize(draft.label);
    if (labelRule === 'required' && !label) next.label = 'Name this fee so it keeps its own history.';
    if (label && label.length > 64) next.label = 'Keep the name under 64 characters.';

    // Duplicate (kind, label, effectiveFrom) → 409, excluding the row being edited.
    // Compared on the SAME normalized, case-folded key the server writes, so
    // "ATM abroad" and "  atm   Abroad " collide here exactly as they would there.
    const key = H.termLabelKey(label);
    const dup = existing.some(t =>
      t.id !== (term && term.id) && t.kind === draft.kind
      && (t.labelKey || H.termLabelKey(t.label) || null) === (key || null)
      && t.effectiveFrom === draft.effectiveFrom);
    if (dup) next.effectiveFrom = label
      ? `“${label}” already has an entry on that date.`
      : 'This kind already has an entry on that date.';

    if (Object.keys(next).length) { setErrors(next); return; }

    const value = isPct ? Number((raw / 100).toFixed(6)) : Number(raw.toFixed(2));
    onSave({
      kind: draft.kind,
      unit: draft.unit,
      value,
      currency: isPct ? null : draft.currency,
      interval: isRate ? null : (draft.interval || null),
      // Stored as 1 when a periodic unit is left without a count (the identity
      // cadence), and as null — never 1 — in every non-periodic case.
      intervalCount: !isRate && periodic ? (count == null ? 1 : count) : null,
      anchorDate: isRate ? null : (draft.anchorDate || null),
      effectiveFrom: draft.effectiveFrom,
      label,
      // LabelKey is derived, never posted — this stands in for the server's
      // write path, which is the only thing allowed to set it.
      labelKey: key,
      note: draft.note.trim() || null,
    }, term && term.id);
  };

  const sym = draft.currency; // eslint-disable-line no-unused-vars
  // The cadence in words, from the single helper every surface reads.
  const cadence = isRate ? null : H.cadenceText(draft.interval, draft.intervalCount === '' ? 1 : parseInt(draft.intervalCount, 10));
  const previewFrac = (() => {
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    return isNaN(raw) ? null : raw / 100;
  })();

  return (
    <Modal
      title={isEdit ? 'Edit term' : 'New term'}
      subtitle={isEdit ? 'Correct this rate or fee entry.' : `Record a rate or fee on ${account.name}, effective from a date.`}
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

      {/* Kind — eligibility-gated grid (new) · locked tile (edit) · nothing at all
         when the account type leaves one eligible kind. Dropping a one-option
         button grid removes a control, not information: the kind it would have
         selected is still written out on every tile and history row. */}
      {(isEdit || eligibleKinds.length > 1) && (
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
            {eligibleKinds.length < D.termKinds.length && (
              <div className="trm-kind-ineligible">
                Some kinds don’t apply to a <b>{window.ACCOUNT_TYPE_LABEL[account.type] || account.type}</b> account and are hidden.
              </div>
            )}
          </React.Fragment>
        )}
        {errors.kind && <div className="helper aam-err">{errors.kind}</div>}
      </div>
      )}

      {/* Name — the series label. Not rendered on a rate kind (the headline rate
         stays unambiguous); required on every fee, since with one fee kind two
         unnamed fees could not be told apart. */}
      {labelRule !== 'hidden' && (
        <Field
          label="Name"
          required={labelRule === 'required'}
          value={draft.label}
          onChange={set('label')}
          placeholder="e.g. ATM withdrawal · abroad"
          error={errors.label}
          help="Names this fee so it keeps its own history, separate from the account's other fees."
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
            currencyOptions={TRM_CURRENCIES}
            currencySearchThreshold={0}
            error={errors.value}
            help={<React.Fragment>Flat amount in <b>{draft.currency}</b>{cadence ? <React.Fragment> · {cadence}</React.Fragment> : ''}</React.Fragment>}
          />
        )}
      </div>

      {/* Effective date — currency lives inside the money field (amount mode); a
         rate has no currency, so nothing about it is shown here. */}
      {/* Direction is a CONTRACT-term field and is deliberately absent here.
          A savings account's interest is incoming and a loan's is outgoing,
          but no account surface reads a direction today, so offering one would
          let a user record a fact the product then contradicts — the server
          refuses anything but Outgoing on an account term. Stated rather than
          silently missing, so the asymmetry with the contract dialog reads as
          a decision. */}
      {!isRate && (
        <div className="trm-dir-refused">
          <MIcon name="block" size={15} />
          <span>An account term is always <b>money out</b>. Direction — money in or out — is recorded on a <b>contract</b> term.</span>
        </div>
      )}

      <FormRow cols={1}>
        <DateField label="Effective from" required value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

{/* Cadence — fees only. The unit picker always; the count only once the unit
          is periodic, so a "how many" question is never asked about a one-time
          or per-occurrence charge. */}
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
            <div className="helper trm-cadence-echo">Name the unit in the fee’s name — “Custody · per share”.</div>
          )}
          <DateField label="First billed on" value={draft.anchorDate} onChange={set('anchorDate')}
            helper="When this is first actually charged, if that isn’t the effective date" />
        </div>
      )}

      {/* Note */}
      <NoteField label="Note" maxLength={512} value={draft.note} onChange={set('note')}
        placeholder="What changed, and why — e.g. “Fed cut pass-through”."
        error={errors.note} />
    </Modal>
  );
};

Object.assign(window, { AddTermModal });
