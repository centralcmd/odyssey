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
  .map(c => ({ value: c.code, label: c.name }));

const ATS_NumField = ({ help, ...props }) => <NumberField helper={help} {...props} />;

/* Declared figures are money, so they use MoneyField with the code locked to the
   statement's base currency. The draft keeps them as numbers (the DTO shape), so
   this holds the typed magnitude and its sign locally and reports the parsed
   number up — a partial entry ("1 250,") still reads as 1250 rather than null. */
const ATS_Money = ({ label, value, onChange, currency, help, signEditable = false }) => {
  const [mag, setMag] = React.useState(value == null ? '' : String(Math.abs(value)));
  const [neg, setNeg] = React.useState(value != null && value < 0);
  const report = (isNeg, m) => {
    const body = m.replace(/\s/g, '').replace(',', '.').replace(/[.]$/, '');
    const n = parseFloat(body);
    onChange(body === '' || isNaN(n) ? null : (isNeg ? -n : n));
  };
  const handle = (next) => {
    const isNeg = /^\s*-/.test(next);
    const m = next.replace(/^\s*-/, '');
    setNeg(isNeg);
    setMag(m);
    report(isNeg, m);
  };
  return (
    <MoneyField label={label} value={(neg ? '-' : '') + mag} onChange={handle}
      currency={currency} currencyEditable={false}
      signEditable={signEditable} allowNegative={signEditable}
      placeholder="0.00" help={help} />
  );
};

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
    notes: '',
    taxTags: [],
    incomeTags: [],
    filedAtUtc: '',
    taxOfficeApprovedAtUtc: '',
    declared: {
      totalAssets: null, totalLiabilities: null, netWorth: null,
      totalIncome: null, assessedTax: null, settlementAmount: null, settledAtUtc: '',
    },
  });
  const [errors, setErrors] = useState({});

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
      filedAtUtc: draft.filedAtUtc || null,
      taxOfficeApprovedAtUtc: draft.taxOfficeApprovedAtUtc || null,
      notes: draft.notes, archived: null, createdAtUtc: new Date().toISOString(),
      declared: { ...d, settledAtUtc: d.settledAtUtc || null },
      // No account-balance sync yet → derived not available, sums 0.
      derived: { available: false, totalAssets: null, totalLiabilities: null, netWorth: null, paidTax: 0, actualIncome: 0 },
      taxTags: draft.taxTags, incomeTags: draft.incomeTags,
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
        <CurrencySelect label="Base currency" value={draft.baseCurrency} onChange={set('baseCurrency')} options={ATS_CURRENCIES} searchThreshold={0} helper="Derived sums include only this currency." />
      </FormRow>

      <FormRow>
        <DateField label="Period start" value={draft.startDate} onChange={set('startDate')} />
        <DateField label="Period end" value={draft.endDate} onChange={set('endDate')} help="Defaults to the calendar year." error={errors.endDate} />
      </FormRow>

      <SectionDivider label="Declared figures" meta="from the official statement · all optional" />
      <FormRow>
        <ATS_Money label="Total assets" value={draft.declared.totalAssets} onChange={setDec('totalAssets')} currency={draft.baseCurrency} />
        <ATS_Money label="Total liabilities" value={draft.declared.totalLiabilities} onChange={setDec('totalLiabilities')} currency={draft.baseCurrency} />
      </FormRow>
      <FormRow>
        <ATS_Money label="Net worth" value={draft.declared.netWorth} onChange={setDec('netWorth')} currency={draft.baseCurrency} signEditable help="Stated directly — may differ from assets − liabilities." />
        <ATS_Money label="Total income" value={draft.declared.totalIncome} onChange={setDec('totalIncome')} currency={draft.baseCurrency} />
      </FormRow>
      <FormRow>
        <ATS_Money label="Assessed tax" value={draft.declared.assessedTax} onChange={setDec('assessedTax')} currency={draft.baseCurrency} />
        <ATS_Money label="Settlement amount" value={draft.declared.settlementAmount} onChange={setDec('settlementAmount')} currency={draft.baseCurrency} signEditable help="Positive = additional tax owed · negative = refund." />
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

      <SectionDivider label="Derivation tags" meta="tags feeding the derived figures" />
      <FormRow>
        <FieldShell label="Tax-payment tags" helper="Sum into derived advance tax paid (within the year).">
          <MultiSelect allLabel="Select tags…" value={draft.taxTags} onChange={set('taxTags')} options={taxOpts} />
        </FieldShell>
        <FieldShell label="Income tags" helper="Sum into derived actual income.">
          <MultiSelect allLabel="Select tags…" value={draft.incomeTags} onChange={set('incomeTags')} options={incOpts} />
        </FieldShell>
      </FormRow>
    </Modal>
  );
};

Object.assign(window, { AddTaxStatementModal });
