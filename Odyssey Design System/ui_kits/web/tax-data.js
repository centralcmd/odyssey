/* Tax Statements — seed data + helpers for the yearly tax-statement feature.
   Shapes mirror the backend spec (TaxStatement / TaxStatementTag / TaxStatementFile
   + the computed TaxStatementReport). Nothing is fetched; the reconciliation
   figures the API computes on read are precomputed here into each record's
   `declared` / `derived` blocks, and the variances are derived by the helpers
   below exactly as §4/§9 of the spec specify.

   Worked example is the spec's: a 2024 statement (NOK) whose 209 000 advance
   tax against an assessed 210 000 leaves a 1 000 NOK additional-tax settlement,
   matching the declared settlement paid the following year. */

(function () {
  const D = window.OdysseyData;

  /* TransactionTags selected on a statement, in two roles (TaxStatementTagRole):
       • TaxPayment — sums into derived "advance tax paid" (within the income year)
       • Income     — sums into derived "actual income"
     A subset of the workspace's TransactionTags. `Tax settlement` is deliberately
     NOT a tax-payment tag — per §9 the settlement is declared-only. */
  D.taxTagCatalog = [
    { id: 'tt-ft', name: 'Withholding tax',  role: 'TaxPayment', description: 'Withholding / advance tax deducted through the year.' },
    { id: 'tt-rt', name: 'Tax settlement',   role: 'TaxPayment', description: 'Post-assessment settlement — NOT selected (declared-only).' },
    { id: 'ti-lo', name: 'Salary',           role: 'Income',     description: 'Salary and employment income.' },
    { id: 'ti-re', name: 'Interest income',  role: 'Income',     description: 'Interest income on deposits.' },
    { id: 'ti-ak', name: 'Dividends',        role: 'Income',     description: 'Dividends from share holdings.' },
  ];
  D.taxTagByName = Object.fromEntries(D.taxTagCatalog.map(t => [t.name, t]));

  /* TaxStatement records — newest fiscal year first. */
  D.taxStatements = [
    {
      id: 'ts-2025', name: 'Tax year 2025', fiscalYear: 2025,
      startDate: '2025-01-01', endDate: '2025-12-31', baseCurrency: 'NOK',
      status: 'Flagged',
      statusComment: 'Held for review until the final assessment is issued — current figures are taken from the draft.',
      statusChangedAt: '2026-03-04T11:20:00Z',
      filedAtUtc: '2026-04-30T00:00:00Z',
      taxOfficeApprovedAtUtc: null,
      settledAtUtc: null,
      notes: 'Draft assessment received; awaiting the final tax assessment and the year-end account-balance sync.',
      archived: null,
      createdAtUtc: '2026-02-20T09:00:00Z',
      declared: {
        totalAssets: null, totalLiabilities: null, netWorth: 1720000,
        totalIncome: 910000, assessedTax: 232000,
        settlementAmount: null, settledAtUtc: null,
      },
      // Account balances stubbed → derived net-worth unavailable, but advance
      // tax + actual income still derive from tagged transactions.
      derived: {
        available: false,
        totalAssets: null, totalLiabilities: null, netWorth: null,
        paidTax: 233500, actualIncome: 905000,
      },
      taxTags: ['Withholding tax'],
      incomeTags: ['Salary', 'Dividends'],
      files: [
        { id: 'tsf-2025a', name: 'tax_return_2025_draft.pdf', kind: 'TaxReturn', size: '203 KB', uploaded: '2026-03-02' },
      ],
      excludedTransactionCount: 1,
      excludedCurrencies: { EUR: 1 },
    },
    {
      id: 'ts-2024', name: 'Tax year 2024', fiscalYear: 2024,
      startDate: '2024-01-01', endDate: '2024-12-31', baseCurrency: 'NOK',
      status: 'Approved',
      statusComment: null,
      statusChangedAt: '2025-06-22T14:05:00Z',
      filedAtUtc: '2025-04-30T00:00:00Z',
      taxOfficeApprovedAtUtc: '2025-06-20T00:00:00Z',
      notes: 'Final assessment. Additional tax of kr 1 000 paid by the October deadline.',
      archived: null,
      createdAtUtc: '2025-04-12T09:00:00Z',
      declared: {
        totalAssets: 2500000, totalLiabilities: 900000, netWorth: 1600000,
        totalIncome: 850000, assessedTax: 210000,
        settlementAmount: 1000, settledAtUtc: '2025-10-15T00:00:00Z',
      },
      derived: {
        available: true,
        totalAssets: 2485000, totalLiabilities: 900000, netWorth: 1585000,
        paidTax: 209000, actualIncome: 842000,
      },
      taxTags: ['Withholding tax'],
      incomeTags: ['Salary', 'Interest income'],
      files: [
        { id: 'tsf-2024a', name: 'tax_return_2024.pdf',     kind: 'TaxReturn',     size: '248 KB', uploaded: '2025-04-29' },
        { id: 'tsf-2024b', name: 'tax_assessment_2024.pdf', kind: 'TaxAssessment', size: '191 KB', uploaded: '2025-06-21' },
      ],
      excludedTransactionCount: 3,
      excludedCurrencies: { EUR: 2, GBP: 1 },
    },
    {
      id: 'ts-2023', name: 'Tax year 2023', fiscalYear: 2023,
      startDate: '2023-01-01', endDate: '2023-12-31', baseCurrency: 'NOK',
      status: 'Approved',
      statusComment: null,
      statusChangedAt: '2024-06-19T10:00:00Z',
      filedAtUtc: '2024-04-28T00:00:00Z',
      taxOfficeApprovedAtUtc: '2024-06-18T00:00:00Z',
      notes: 'Refund of kr 3 200 received in September 2024.',
      archived: '2025-01-05T09:00:00Z',
      createdAtUtc: '2024-04-10T09:00:00Z',
      declared: {
        totalAssets: 2310000, totalLiabilities: 950000, netWorth: 1360000,
        totalIncome: 815000, assessedTax: 198000,
        settlementAmount: -3200, settledAtUtc: '2024-09-20T00:00:00Z',
      },
      derived: {
        available: true,
        totalAssets: 2318000, totalLiabilities: 950000, netWorth: 1368000,
        paidTax: 201200, actualIncome: 815000,
      },
      taxTags: ['Withholding tax'],
      incomeTags: ['Salary', 'Interest income'],
      files: [
        { id: 'tsf-2023a', name: 'tax_return_2023.pdf',     kind: 'TaxReturn',     size: '236 KB', uploaded: '2024-04-27' },
        { id: 'tsf-2023b', name: 'tax_assessment_2023.pdf', kind: 'TaxAssessment', size: '184 KB', uploaded: '2024-06-19' },
      ],
      excludedTransactionCount: 0,
      excludedCurrencies: {},
    },
  ];

  // ---- Helpers --------------------------------------------------------------
  const H = window.OdysseyHelpers;

  Object.assign(H, {
    // Money in the statement's base currency: "kr 1,600,000" (whole kroner —
    // tax figures carry no minor units in the worked example). Symbol prefix
    // matches the app's money() house style; sign uses the en-dash minus.
    taxMoney(n, code = 'NOK') {
      if (n == null) return '—';
      const cur = D.currencyByCode[code];
      const sym = (cur && cur.symbol) || code;
      const sign = n < 0 ? '−' : '';
      return `${sign}${sym} ${Math.abs(n).toLocaleString('en-US', { maximumFractionDigits: 0 })}`;
    },
    // Variant for variances / settlement: negatives show "−", positives are
    // shown plain (no leading "+"): "kr 1,000" / "−kr 3,200".
    taxSignedMoney(n, code = 'NOK') {
      if (n == null) return '—';
      const cur = D.currencyByCode[code];
      const sym = (cur && cur.symbol) || code;
      const sign = n < 0 ? '−' : '';
      return `${sign}${sym} ${Math.abs(n).toLocaleString('en-US', { maximumFractionDigits: 0 })}`;
    },

    // Review-status chip — mirrors the transaction status vocabulary. Archived
    // overrides to the neutral outline chip (as Budgets/Accounts do).
    taxStatementStatus(s) {
      if (s.archived) return { label: 'Archived', tone: 'outline', dot: true };
      switch (s.status) {
        case 'Approved': return { label: 'Approved', tone: 'income',  dot: true };
        case 'Flagged':  return { label: 'Flagged',  tone: 'expense', dot: true };
        default:         return { label: 'New',      tone: 'info',    dot: true };
      }
    },

    // The TaxStatementReport reconciliation block, computed on read (§4/§9).
    // Any null operand ⇒ the corresponding diff is null.
    taxReconciliation(s) {
      const d = s.declared, v = s.derived;
      const num = (x) => (x == null ? null : x);
      const sub = (a, b) => (a == null || b == null ? null : a - b);
      const outstandingTax = sub(num(d.assessedTax), num(v.paidTax));
      const netWorthDerived = v.available ? num(v.netWorth) : null;
      return {
        outstandingTax,
        incomeVariance: sub(num(d.totalIncome), num(v.actualIncome)),
        netWorthVariance: sub(num(d.netWorth), netWorthDerived),
        settlementVariance: sub(num(d.settlementAmount), outstandingTax),
      };
    },

    // "kr 1,000 owed" / "kr 3,200 refund" / "settled" — a human gloss on a
    // signed settlement-or-outstanding amount (positive = owed, negative = refund).
    taxBalanceWord(n) {
      if (n == null) return '';
      if (n > 0) return 'to pay';
      if (n < 0) return 'refund';
      return 'settled';
    },
  });
})();
