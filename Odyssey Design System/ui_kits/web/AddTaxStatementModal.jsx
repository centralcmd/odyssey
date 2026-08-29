/* AddTaxStatementModal — the dialog opened from the Tax Statements page
   "New tax statement" button (and the dashed add-row at the bottom of the list).

   Fields mirror the NewTaxStatement creation DTO:
     • name           (required)   — TaxStatement.Name
     • fiscalYear     (required)   — TaxStatement.FiscalYear
     • startDate      (required)   — TaxStatement.StartDate
     • endDate        (required)   — TaxStatement.EndDate
     • baseCurrency   (required)   — TaxStatement.BaseCurrencyCode
     • declared figures (optional) — a skeleton statement can be saved and
       completed later (the declared figures are nullable). Tags, documents,
       settlement and lifecycle dates are added afterwards from the detail.
   A new statement is always Status=New and not archived. */

const ATS_CURRENCIES = window.OdysseyData.currencies
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: `${c.code} · ${c.name}` }));

const ATS_NumField = ({ help, ...props }) => <NumberField helper={help} {...props} />;

const AddTaxStatementModal = ({ onClose, onCreate, onSave, statement = null }) => {
  const { useState } = React;
  const editing = !!statement;
  const thisYear = new Date().getFullYear();
  const defaultYear = thisYear - 1; // most recent completed tax year
  const [draft, setDraft] = useState(editing ? {
    name: statement.name,
    fiscalYear: statement.fiscalYear,
    startDate: statement.startDate,
    endDate: statement.endDate,
    baseCurrency: statement.baseCurrency,
    notes: statement.notes || '',
    taxTags: statement.taxTags || [],
    incomeTags: statement.incomeTags || [],
    filedAtUtc: statement.filedAtUtc ? statement.filedAtUtc.slice(0, 10) : '',
    taxOfficeApprovedAtUtc: statement.taxOfficeApprovedAtUtc ? statement.taxOfficeApprovedAtUtc.slice(0, 10) : '',
    declared: { ...statement.declared, settledAtUtc: statement.declared.settledAtUtc ? statement.declared.settledAtUtc.slice(0, 10) : '' },
  } : {
    name: `Tax year ${defaultYear}`,
    fiscalYear: defaultYear,
    startDate: `${defaultYear}-01-01`,
    endDate: `${defaultYear}-12-31`,
    baseCurrency: 'NOK',
    declared: { totalIncome: null, assessedTax: null, netWorth: null },
  });
  const [errors, setErrors] = useState({});
  const [showFigures, setShowFigures] = useState(false);

  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };
  const setDec = (k) => (v) => setDraft(d => ({ ...d, declared: { ...d.declared, [k]: v } }));

  // Keep name + period in step when the fiscal year changes (create only, until
  // the user edits the name themselves).
  const setYear = (v) => {
    const y = v ? Math.round(v) : '';
    setDraft(d => ({
      ...d,
      fiscalYear: y,
      startDate: y ? `${y}-01-01` : d.startDate,
      endDate: y ? `${y}-12-31` : d.endDate,
      name: d.name === `Tax year ${d.fiscalYear}` ? `Tax year ${y}` : d.name,
    }));
    if (errors.fiscalYear) setErrors(e => ({ ...e, fiscalYear: undefined }));
  };

  // Derivation-tag options from the shared catalog (edit mode only).
  const taxCatalog = (window.OdysseyData.taxTagCatalog) || [];
  const taxOpts = taxCatalog.filter(t => t.role === 'TaxPayment').map(t => ({ value: t.name, label: t.name }));
  const incOpts = taxCatalog.filter(t => t.role === 'Income').map(t => ({ value: t.name, label: t.name }));

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the statement a name.';
    if (!draft.fiscalYear || draft.fiscalYear < 1900 || draft.fiscalYear > 2200) next.fiscalYear = 'Enter a valid year (1900–2200).';
    if (draft.startDate && draft.endDate && draft.endDate < draft.startDate) next.endDate = 'End date can’t be before the start date.';
    if (Object.keys(next).length) { setErrors(next); return; }

    if (editing) {
      onSave && onSave({
        name: draft.name.trim(),
        fiscalYear: draft.fiscalYear,
        startDate: draft.startDate, endDate: draft.endDate,
        baseCurrency: draft.baseCurrency, notes: draft.notes,
        taxTags: draft.taxTags, incomeTags: draft.incomeTags,
        filedAtUtc: draft.filedAtUtc || null,
        taxOfficeApprovedAtUtc: draft.taxOfficeApprovedAtUtc || null,
        declared: { ...draft.declared, settledAtUtc: draft.declared.settledAtUtc || null },
      });
      return;
    }

    const d = draft.declared;
    onCreate && onCreate({
      id: `ts-new-${Date.now()}`,
      name: draft.name.trim(),
      fiscalYear: draft.fiscalYear,
      startDate: draft.startDate, endDate: draft.endDate,
      baseCurrency: draft.baseCurrency,
      status: 'New', statusComment: null, statusChangedAt: new Date().toISOString(),
      filedAtUtc: null, taxOfficeApprovedAtUtc: null,
      notes: '', archived: null, createdAtUtc: new Date().toISOString(),
      declared: {
        totalAssets: null, totalLiabilities: null, netWorth: d.netWorth,
        totalIncome: d.totalIncome, assessedTax: d.assessedTax,
        settlementAmount: null, settledAtUtc: null,
      },
      // No account-balance sync and no tags yet → derived not available, sums 0.
      derived: { available: false, totalAssets: null, totalLiabilities: null, netWorth: null, paidTax: 0, actualIncome: 0 },
      taxTags: [], incomeTags: [],
      files: [],
      excludedTransactionCount: 0, excludedCurrencies: {},
    });
  };

  return (
    <Modal
      title={editing ? 'Edit tax statement' : 'New tax statement'}
      subtitle={editing
        ? 'Update the fiscal year, declared figures, derivation tags and lifecycle dates.'
        : "Record a fiscal year's official assessment, then reconcile it against your accounts and tagged transactions."}
      icon="request_quote"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create statement'}
          </Button>
        </React.Fragment>
      }>
      <Field
        label="Statement name"
        value={draft.name}
        onChange={set('name')}
        placeholder="e.g. Tax year 2025"
        error={errors.name}
        autoFocus
      />

      <FormRow>
        <ATS_NumField label="Fiscal year" value={draft.fiscalYear}
          onChange={editing ? ((v) => set('fiscalYear')(v ? Math.round(v) : draft.fiscalYear)) : setYear}
          help={errors.fiscalYear || 'The income year.'} />
        <Select label="Base currency" value={draft.baseCurrency} onChange={set('baseCurrency')} options={ATS_CURRENCIES} helper="Derived sums include only this currency." />
      </FormRow>

      <FormRow>
        <DateField label="Period start" value={draft.startDate} onChange={set('startDate')} />
        <DateField label="Period end" value={draft.endDate} onChange={set('endDate')} helper={errors.endDate || 'Defaults to the calendar year.'} />
      </FormRow>

      {editing ? (
        <React.Fragment>
          <div className="tx-section-h" style={{ marginTop: 6 }}><MIcon name="receipt_long" size={18} />Declared figures<span className="tx-section-cap">From the official statement — all optional</span></div>
          <FormRow>
            <ATS_NumField label="Total assets" value={draft.declared.totalAssets} onChange={setDec('totalAssets')} />
            <ATS_NumField label="Total liabilities" value={draft.declared.totalLiabilities} onChange={setDec('totalLiabilities')} />
          </FormRow>
          <FormRow>
            <ATS_NumField label="Net worth" value={draft.declared.netWorth} onChange={setDec('netWorth')} help="Stated directly — may differ from assets − liabilities." />
            <ATS_NumField label="Total income" value={draft.declared.totalIncome} onChange={setDec('totalIncome')} />
          </FormRow>
          <FormRow>
            <ATS_NumField label="Assessed tax" value={draft.declared.assessedTax} onChange={setDec('assessedTax')} />
            <ATS_NumField label="Settlement amount" value={draft.declared.settlementAmount} onChange={setDec('settlementAmount')} help="Positive = additional tax owed · negative = refund." />
          </FormRow>
          <FormRow>
            <DateField label="Settlement paid" value={draft.declared.settledAtUtc} onChange={setDec('settledAtUtc')} />
            <DateField label="Filed to authority" value={draft.filedAtUtc} onChange={set('filedAtUtc')} />
          </FormRow>
          <FormRow>
            <DateField label="Authority approved" value={draft.taxOfficeApprovedAtUtc} onChange={set('taxOfficeApprovedAtUtc')} />
            <div />
          </FormRow>

          <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
            placeholder="Anything worth remembering about this statement…" />

          <div className="tx-section-h" style={{ marginTop: 6 }}><MIcon name="local_offer" size={18} />Derivation tags<span className="tx-section-cap">Which transaction tags feed the derived figures</span></div>
          <FormRow>
            <FieldShell label="Tax-payment tags" helper="Sum into derived advance tax paid (within the year).">
              <MultiSelect allLabel="Select tags…" value={draft.taxTags} onChange={set('taxTags')} options={taxOpts} />
            </FieldShell>
            <FieldShell label="Income tags" helper="Sum into derived actual income.">
              <MultiSelect allLabel="Select tags…" value={draft.incomeTags} onChange={set('incomeTags')} options={incOpts} />
            </FieldShell>
          </FormRow>
        </React.Fragment>
      ) : (
        /* optional declared figures — collapsible to keep the create path light */
        <div className="atm-adv">
          <button type="button" className="atm-adv-toggle" onClick={() => setShowFigures(v => !v)}>
            <MIcon name="expand_more" size={20} className={`chev ${showFigures ? 'open' : ''}`} />
            Declared figures
            <span className="atm-adv-hint">Optional — add now or later</span>
          </button>
          {showFigures && (
            <div className="atm-adv-body">
              <FormRow>
                <ATS_NumField label="Total income" value={draft.declared.totalIncome} onChange={setDec('totalIncome')} />
                <ATS_NumField label="Assessed tax" value={draft.declared.assessedTax} onChange={setDec('assessedTax')} />
              </FormRow>
              <ATS_NumField label="Net worth" value={draft.declared.netWorth} onChange={setDec('netWorth')} help="Stated on the assessment — may differ from assets − liabilities." />
            </div>
          )}
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { AddTaxStatementModal });
