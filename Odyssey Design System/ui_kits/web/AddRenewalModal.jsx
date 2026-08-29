/* AddRenewalModal — New / Edit dialog for a PolicyRenewal (a time-versioned term
   of a policy). Opened from the renewal-history section + the policy ActionMenu.
   Field set mirrors the NewPolicyRenewal DTO and enforces the spec's validation:
     • FromDate / ToDate          — both required; ToDate ≥ FromDate
     • Premium + currency         — required; ≥ 0; currency = existing 3-letter code
     • CoverageAmount + currency  — required; ≥ 0; currency independent of premium
     • Notes                      — optional, ≤ 512
   Overlaps with other periods are PERMITTED (not rejected) — current-renewal
   selection stays deterministic via the §5 tie-break. On confirm, onSave(dto, id?)
   receives the renewal-shaped object (id present on edit). */

const ARN_CURRENCIES = window.OdysseyData.currencies
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: `${c.code} · ${c.name}` }));
const ARN_SYM = (code) => (window.OdysseyData.currencyByCode[code] || {}).symbol || code;

const ARN_MoneyField = ({ label, value, onChange, currency, onCurrency, error, help }) => (
  <div>
    <AmountField label={label} size="lg" prefix={ARN_SYM(currency)} value={value} onChange={onChange} error={error} help={help} />
    <div style={{ marginTop: 8 }}>
      <Select value={currency} onChange={onCurrency} options={ARN_CURRENCIES} />
    </div>
  </div>
);

const AddRenewalModal = ({ policy, renewal, onClose, onSave }) => {
  const { useState } = React;
  const isEdit = !!renewal;
  // Default the new period to follow the latest existing one (or this calendar year).
  const latest = (policy.renewals || []).slice().sort((a, b) => (a.toDate < b.toDate ? 1 : -1))[0];
  const defCur = (latest && latest.premiumCurrencyCode) || 'USD';
  const thisYear = new Date().getFullYear();

  const [draft, setDraft] = useState({
    fromDate: renewal ? renewal.fromDate : (latest ? null : `${thisYear}-01-01`),
    toDate: renewal ? renewal.toDate : (latest ? null : `${thisYear}-12-31`),
    premium: renewal ? String(renewal.premium) : '',
    premiumCurrencyCode: renewal ? renewal.premiumCurrencyCode : defCur,
    coverageAmount: renewal ? String(renewal.coverageAmount) : '',
    coverageCurrencyCode: renewal ? renewal.coverageCurrencyCode : defCur,
    notes: renewal ? (renewal.notes || '') : '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };

  const submit = () => {
    const next = {};
    if (!draft.fromDate) next.fromDate = 'Pick the start of cover.';
    if (!draft.toDate) next.toDate = 'Pick the end of cover.';
    if (draft.fromDate && draft.toDate && draft.toDate < draft.fromDate) next.toDate = 'End date can’t be before the start date.';
    const prem = parseFloat(String(draft.premium).replace(/,/g, ''));
    const cov = parseFloat(String(draft.coverageAmount).replace(/,/g, ''));
    if (draft.premium === '' || isNaN(prem) || prem < 0) next.premium = 'Enter a premium of 0 or more.';
    if (draft.coverageAmount === '' || isNaN(cov) || cov < 0) next.coverageAmount = 'Enter a coverage amount of 0 or more.';
    if (draft.notes.length > 512) next.notes = 'Keep the note under 512 characters.';
    if (Object.keys(next).length) { setErrors(next); return; }

    onSave({
      fromDate: draft.fromDate, toDate: draft.toDate,
      premium: Number(prem.toFixed(2)), premiumCurrencyCode: draft.premiumCurrencyCode,
      coverageAmount: Number(cov.toFixed(2)), coverageCurrencyCode: draft.coverageCurrencyCode,
      notes: draft.notes.trim() || null,
    }, renewal && renewal.id);
  };

  return (
    <Modal
      title={isEdit ? 'Edit renewal period' : 'New renewal period'}
      subtitle={isEdit ? 'Correct this period’s dates, premium or coverage.' : `Record a period of cover for ${policy.name}.`}
      icon={isEdit ? 'edit' : 'event_repeat'}
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={isEdit ? 'check' : 'add'} onClick={submit}>
            {isEdit ? 'Save changes' : 'Create period'}
          </Button>
        </React.Fragment>
      }>
      <FormRow>
        <DateField label="Cover from" value={draft.fromDate} onChange={set('fromDate')} helper={errors.fromDate} />
        <DateField label="Cover to" value={draft.toDate} onChange={set('toDate')} helper={errors.toDate || 'End of this period’s cover.'} />
      </FormRow>

      <FormRow>
        <ARN_MoneyField label="Premium" value={draft.premium} onChange={set('premium')}
          currency={draft.premiumCurrencyCode} onCurrency={set('premiumCurrencyCode')}
          error={errors.premium} help="Premium for this term, as stored (not annualized)." />
        <ARN_MoneyField label="Coverage amount" value={draft.coverageAmount} onChange={set('coverageAmount')}
          currency={draft.coverageCurrencyCode} onCurrency={set('coverageCurrencyCode')}
          error={errors.coverageAmount} help="Insured sum for this term." />
      </FormRow>

      <NoteField label="Notes" optional maxLength={512} value={draft.notes} onChange={set('notes')}
        placeholder="What changed this period — discount applied, coverage raised…"
        error={errors.notes} />
    </Modal>
  );
};

Object.assign(window, { AddRenewalModal });
