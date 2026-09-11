/* AddTermModal — New / Edit dialog for an AccountTerm (interest-rate & fee history).

   Opened from the "Terms" section (Accounts → account detail). Built on the
   shared DS Modal shell, like every other create/edit dialog. Field set mirrors
   the NewAccountTerm DTO and enforces the spec's validation:

     • TermKind        — eligibility-gated by the account's AccountType (matrix in
                         data.js). Interest rate only on interest-bearing accounts,
                         expected return only on investment/pension, fees broadly.
     • ValueUnit       — Percentage | Amount. Locked to Percentage for rate kinds.
     • Value           — Percentage: typed as a percent, stored as a fraction in
                         [-1, 1] (3.40 → 0.0340; negative allowed). Amount: ≥ 0.
     • CurrencyCode    — required for Amount (defaults to the account currency);
                         null for Percentage.
     • BillingPeriod   — optional context for fees; null for rate kinds.
     • Label           — names one fee, so a card can carry a domestic and a foreign
                         cash-withdrawal charge at once. Required for every fee (there is
                         one fee kind, so two unnamed fees cannot be told apart); refused
                         for rate kinds, which stay one-series so the headline rate is
                         unambiguous. ≤ 64 chars.
     • EffectiveFrom   — required; past or future allowed (future = scheduled).
     • Note            — optional, ≤ 512 chars.

   Rejects an exact (TermKind, Label, EffectiveFrom) duplicate (the server's 409),
   matching the label case-insensitively so a typo does not fork a second series. On
   confirm, onSave(dto, id?) receives the term-shaped object (id present on edit). */

/* Series label normalization — mirrors TermLabel in Odyssey.Dtos. Display form keeps
   the author's casing; the key case-folds, and is what de-duplicates. */
const trmLabelNorm = (v) => (String(v || '').trim().replace(/\s+/g, ' ') || null);
const trmLabelKey = (v) => { const n = trmLabelNorm(v); return n && n.toLowerCase(); };

const TRM_SYM = window.ATM_CURRENCY_SYMBOL || { USD: '$', EUR: '€', GBP: '£', JPY: '¥', NOK: 'kr', SEK: 'kr', CAD: '$' };
const TRM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));

/* A fee opens on Monthly — the commonest cadence for an account-level charge. The four
   kind-specific defaults went away with the kinds; one honest default beats a guess that was
   silently wrong for three cases in four. */
const TRM_DEFAULT_FEE_BILLING = 'Monthly';

/* percent fraction → editable percent string ("0.0340" → "3.4") */
const fracToPctStr = (f) => {
  const p = f * 100;
  return String(Number(p.toFixed(4)));
};

const AddTermModal = ({ account, term, existing = [], onClose, onSave }) => {
  const { useState } = React;
  const isEdit = !!term;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  const eligible = H.eligibleTermKinds(account.type);
  const eligibleKinds = D.termKinds.filter(k => eligible.includes(k.key));

  const initKind = term ? term.kind : (eligibleKinds[0] && eligibleKinds[0].key) || 'Fee';
  const initInfo = H.termKindInfo(initKind);

  const [draft, setDraft] = useState(() => ({
    kind: initKind,
    unit: term ? term.unit : initInfo.defaultUnit,
    valueStr: term ? (term.unit === 'Percentage' ? fracToPctStr(term.value) : String(term.value)) : '',
    currency: term ? (term.currency || account.currency || 'USD') : (account.currency || 'USD'),
    billingPeriod: term ? (term.billingPeriod || '') : (initInfo.group === 'fee' ? TRM_DEFAULT_FEE_BILLING : ''),
    label: term ? (term.label || '') : '',
    effectiveFrom: term ? term.effectiveFrom : new Date().toISOString().slice(0, 10),
    note: term ? (term.note || '') : '',
  }));
  const [errors, setErrors] = useState({});

  const info = H.termKindInfo(draft.kind);
  const isRate = info.group === 'rate';
  const isPct = draft.unit === 'Percentage';

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
      billingPeriod: ki.group === 'fee' ? TRM_DEFAULT_FEE_BILLING : '',
      label: ki.group === 'rate' ? '' : d.label,
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
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    const label = isRate ? null : trmLabelNorm(draft.label);
    if (label && label.length > 64) next.label = 'Keep the label under 64 characters.';
    // Every fee is named: one fee kind means an unnamed fee is precisely the entry that would
    // be confused with — and silently supersede — the next unnamed one.
    if (!label && !isRate) next.label = 'Name this fee so it isn’t confused with another.';

    // Duplicate (kind, label, effectiveFrom) → 409, excluding the row being edited.
    const dup = existing.some(t =>
      t.id !== (term && term.id) && t.kind === draft.kind
      && (trmLabelKey(t.label) || null) === (trmLabelKey(label) || null)
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
      billingPeriod: isRate ? null : (draft.billingPeriod || null),
      label,
      effectiveFrom: draft.effectiveFrom,
      note: draft.note.trim() || null,
    }, term && term.id);
  };

  const sym = TRM_SYM[draft.currency] || draft.currency; // eslint-disable-line no-unused-vars
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

      {/* Kind — a locked tile (edit) or an eligibility-gated grid. With one fee kind, most account
         types leave a single choice, and a one-option picker is noise: the block is dropped entirely
         and the form opens on that kind. */}
      {(isEdit || eligibleKinds.length > 1) && (
      <div className="field">
        <div className="label">Term</div>
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
            <div className="trm-kind-grid">
              {eligibleKinds.map(k => {
                const on = k.key === draft.kind;
                return (
                  <button type="button" key={k.key} className={`trm-kind-opt ${on ? 'on' : ''}`} onClick={() => pickKind(k.key)}>
                    <span className="trm-kind-ic md" style={{ background: k.soft, color: k.color }}>
                      <MIcon name={k.icon} size={18} />
                    </span>
                    <span className="trm-kind-opt-txt">
                      <span className="trm-kind-opt-name">{k.label}</span>
                      <span className="trm-kind-opt-grp">{k.group === 'rate' ? 'Rate' : 'Fee'}</span>
                    </span>
                    {on && <MIcon name="check_circle" size={18} className="trm-kind-check" />}
                  </button>
                );
              })}
            </div>
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

      {/* Unit + Value */}
      <div className="trm-value-block">
        <div className="trm-field-head">
          <div className="label" style={{ marginBottom: 0 }}>Value</div>
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
            help={<React.Fragment>Flat amount in <b>{draft.currency}</b>{draft.billingPeriod && draft.billingPeriod !== 'OneTime' ? <React.Fragment> · {(H.billingInfo(draft.billingPeriod) || {}).label}</React.Fragment> : ''}</React.Fragment>}
          />
        )}
      </div>

      {/* Effective date — currency now lives inside the money field (amount mode);
         a rate has no currency, so that column only appears for percentages. */}
      <FormRow cols={isPct ? 2 : 1}>
        {isPct ? (
          <div className="field">
            <div className="label">Currency</div>
            <div className="trm-kind-opt" style={{ cursor: 'default', height: 44, padding: '0 12px', opacity: 0.6 }}>
              <MIcon name="block" size={16} style={{ color: 'var(--mud-palette-text-secondary)' }} />
              <span className="trm-kind-opt-name" style={{ fontWeight: 400, color: 'var(--mud-palette-text-secondary)' }}>Not used for a rate</span>
            </div>
          </div>
        ) : null}
        <DateField label="Effective from" value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </FormRow>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

      {/* Label — fees only. What lets one kind hold several fees at once. */}
      {!isRate && (
        <Field
          label="Label"
          required
          maxLength={64}
          value={draft.label}
          onChange={set('label')}
          error={errors.label}
          placeholder="e.g. “ATM withdrawal · abroad”"
          help="Names this fee and keeps its own price history — e.g. a domestic and a foreign ATM charge side by side."
        />
      )}

      {/* Billing period — fees only */}
      {!isRate && (
        <FieldShell label="Billing period" optional>
          <Select value={draft.billingPeriod} onChange={set('billingPeriod')}
            options={[{ value: '', label: 'Not specified' }, ...D.billingPeriods.map(b => ({ value: b.key, label: b.label }))]} />
        </FieldShell>
      )}

      {/* Note */}
      <NoteField label="Note" optional maxLength={512} value={draft.note} onChange={set('note')}
        placeholder="What changed, and why — e.g. “Fed cut pass-through”."
        error={errors.note} />
    </Modal>
  );
};

Object.assign(window, { AddTermModal });
