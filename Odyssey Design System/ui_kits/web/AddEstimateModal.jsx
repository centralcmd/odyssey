/* AddEstimateModal — New / Edit dialog for an AccountEstimate (value history).

   Opened from the "Estimates" section (Accounts → account detail). Built on the
   shared DS Modal shell, like every other create/edit dialog. Field set mirrors
   the NewAccountEstimate DTO and enforces the spec's validation:

     • Value         — required money amount ≥ 0, in the account currency.
     • CurrencyCode  — ALWAYS the account currency (shown read-only; the server
                       rejects a differing currency with 400).
     • EffectiveFrom — required; past or future allowed (future = scheduled).
     • Note          — optional, ≤ 512 chars.

   An estimate has no kind / unit / billing dimension — it is a single amount, so
   there is no eligibility-gated kind grid (every account type may carry estimates).
   Rejects an exact (AccountId, EffectiveFrom) duplicate (the server's 409). On
   confirm, onSave(dto, id?) receives the estimate-shaped object (id present on edit). */

const EST_SYM = window.ATM_CURRENCY_SYMBOL || { USD: '$', EUR: '€', GBP: '£', JPY: '¥', NOK: 'kr', SEK: 'kr', CAD: '$' };

const AddEstimateModal = ({ account, estimate, existing = [], onClose, onSave, leadIcon }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const isEdit = !!estimate;
  const currency = account.currency || 'USD';
  const recommended = H.isEstimateRecommended(account.type);
  const typeLabel = (window.ACCOUNT_TYPE_LABEL && window.ACCOUNT_TYPE_LABEL[account.type]) || account.type;

  const [draft, setDraft] = useState(() => ({
    valueStr: estimate ? String(estimate.value) : '',
    effectiveFrom: estimate ? estimate.effectiveFrom : new Date().toISOString().slice(0, 10),
    note: estimate ? (estimate.note || '') : '',
  }));
  const [errors, setErrors] = useState({});

  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  const submit = () => {
    const next = {};
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    if (draft.valueStr === '' || isNaN(raw)) next.value = 'Enter an estimated value.';
    else if (raw < 0) next.value = 'An estimate can’t be negative.';

    if (!draft.effectiveFrom) next.effectiveFrom = 'Pick the date this takes effect.';
    if (draft.note.length > 512) next.note = 'Keep the note under 512 characters.';

    const dup = existing.some(e =>
      e.id !== (estimate && estimate.id) && e.effectiveFrom === draft.effectiveFrom);
    if (dup) next.effectiveFrom = 'This account already has an estimate on that date.';

    if (Object.keys(next).length) { setErrors(next); return; }

    onSave({
      value: Number(raw.toFixed(2)),
      currencyCode: currency,
      effectiveFrom: draft.effectiveFrom,
      note: draft.note.trim() || null,
    }, estimate && estimate.id);
  };

  const sym = EST_SYM[currency] || currency;
  const preview = (() => {
    const raw = parseFloat(String(draft.valueStr).replace(/,/g, ''));
    return isNaN(raw) ? null : H.money(raw, currency);
  })();

  return (
    <Modal
      title={isEdit ? 'Edit estimate' : 'New estimate'}
      subtitle={isEdit ? 'Correct this value entry.' : `Record what ${account.name} is worth, effective from a date.`}
      icon={isEdit ? 'edit' : (leadIcon || 'monitor')}
      className="est-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={isEdit ? 'check' : 'add'} onClick={submit}>
            {isEdit ? 'Save changes' : 'Create estimate'}
          </Button>
        </React.Fragment>
      }>

      {/* Value — the hero money input */}
      <div className="est-value-block">
        <AmountField
          label="Estimated value"
          size="lg"
          prefix={sym}
          autoFocus
          value={draft.valueStr}
          onChange={set('valueStr')}
          error={errors.value}
          help={preview == null ? <React.Fragment>Amount in <b>{currency}</b></React.Fragment> : <React.Fragment>Recorded as <b>{preview}</b></React.Fragment>}
        />
      </div>

      {/* Currency (locked) + Effective date */}
      <div className="est-row2">
        <div className="field">
          <div className="label">Currency</div>
          <div className="est-currency-lock">
            <MIcon name="lock" size={16} />
            <span className="code">{currency}</span>
            <span className="note">Account currency</span>
          </div>
        </div>
        <DateField label="Effective from" value={draft.effectiveFrom} onChange={set('effectiveFrom')}
          helper={errors.effectiveFrom ? undefined : 'When this value takes effect'} />
      </div>
      {errors.effectiveFrom && <div className="helper aam-err" style={{ marginTop: -6 }}>{errors.effectiveFrom}</div>}

      {/* Note */}
      <NoteField label="Note" optional maxLength={512} value={draft.note} onChange={set('note')}
        placeholder="Where this came from — e.g. “Refinance appraisal” or “Comparable sales”."
        error={errors.note} />

      {/* Recommendation hint — never gates; just orients */}
      {!isEdit && (
        <div className="est-rec-hint">
          <MIcon name={recommended ? 'recommend' : 'info'} size={16} />
          {recommended
            ? <span>Estimates suit <b>{typeLabel}</b> accounts — their worth isn’t captured by transactions. The current estimate stands in for this account’s value in your net worth.</span>
            : <span>Estimates are typically used on asset accounts like property or vehicles. You can still record one on a <b>{typeLabel}</b> account.</span>}
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { AddEstimateModal });
