/* Seed data + helpers for the Insurance Policies feature (Insurance.jsx).
   ----------------------------------------------------------------------------
   Shapes mirror the spec's Odyssey.Finance.Context entities:
     • InsurancePolicy  { name, policyNumber?, type, insurerId, insuredAccountId?,
                          notes?, archived?, createdAtUtc, renewals[], files[] }
     • PolicyRenewal    { fromDate, toDate, premium + premiumCurrencyCode,
                          coverageAmount + coverageCurrencyCode, notes?,
                          createdAtUtc, files[] }
     • InsurancePolicyFile / PolicyRenewalFile  { fileType, effectiveDate?, … }
                          (rendered with the file shape the kit FilesTable wants:
                          { id, name, kind, size, uploaded } — `kind` is a
                          PolicyFileType key from data.js.)

   Coverage status + the "current renewal" are DERIVED, never stored — computed
   here exactly per spec §5: a single request "today", an ordered evaluation, and
   the latest-FromDate / latest-CreatedAtUtc overlap tie-break. The portfolio
   summary (spec §7) buckets by status and rolls premium + coverage up per
   currency, converting to a base currency where a direct rate exists and listing
   the rest under `unconvertedCurrencies` (never silently zeroed). The registries
   (insurancePolicyTypes / policyFileTypes) live in data.js with the others. */

(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  // The configurable "expiring soon" window (Insurance:ExpiringSoonWindowDays).
  D.INSURANCE_EXPIRING_WINDOW_DAYS = 30;

  /* ---- Seed policies. Dates are anchored around mid-2026 so the derived
     statuses are stable: one Active, one ExpiringSoon, one Lapsed, one Upcoming,
     one NoCoverage (+ multi-currency: USD / EUR / NOK / CHF — CHF has no rate, so
     it exercises the summary's "unconverted" path). ---- */
  D.insurancePolicies = [
    {
      id: 'ip-home', name: 'Home & Contents 2026', policyNumber: 'HC-2026-99182', type: 'Contents',
      insurerId: 'c12', insuredAccountId: '7', notes: 'Buildings + contents on the Maple St residence. Accidental-damage rider included.',
      archived: null, createdAtUtc: '2024-12-18T10:00:00Z',
      files: [
        { id: 'ipf-home-1', name: 'home_policy_certificate_2026.pdf', kind: 'PolicyDocument', size: '410 KB', uploaded: '2025-12-18', effectiveDate: '2026-01-01' },
        { id: 'ipf-home-2', name: 'policy_wording_v4.pdf', kind: 'TermsAndConditions', size: '1.2 MB', uploaded: '2025-12-18' },
      ],
      renewals: [
        { id: 'rn-home-26', fromDate: '2026-01-01', toDate: '2026-12-31', premium: 1840.00, premiumCurrencyCode: 'USD', coverageAmount: 1500000.00, coverageCurrencyCode: 'USD', notes: 'Premium up 4% on prior year; coverage unchanged.', createdAtUtc: '2025-12-18T10:00:00Z', files: [
          { id: 'rnf-home-26-1', name: 'renewal_invoice_2026.pdf', kind: 'Invoice', size: '88 KB', uploaded: '2025-12-18', effectiveDate: '2026-01-01' },
        ] },
        { id: 'rn-home-25', fromDate: '2025-01-01', toDate: '2025-12-31', premium: 1768.00, premiumCurrencyCode: 'USD', coverageAmount: 1500000.00, coverageCurrencyCode: 'USD', notes: null, createdAtUtc: '2024-12-18T10:00:00Z', files: [] },
      ],
    },
    {
      id: 'ip-auto', name: 'Honda Civic — Comprehensive', policyNumber: 'MV-55-220714', type: 'Vehicle',
      insurerId: 'c20', insuredAccountId: '5', notes: 'Comprehensive motor cover, €500 excess. Named drivers: 2.',
      archived: null, createdAtUtc: '2024-07-10T09:00:00Z',
      files: [
        { id: 'ipf-auto-1', name: 'motor_certificate.pdf', kind: 'PolicyDocument', size: '256 KB', uploaded: '2025-07-12', effectiveDate: '2025-07-16' },
      ],
      renewals: [
        { id: 'rn-auto-25', fromDate: '2025-07-16', toDate: '2026-07-15', premium: 1260.00, premiumCurrencyCode: 'USD', coverageAmount: 24500.00, coverageCurrencyCode: 'USD', notes: 'No-claims discount applied (40%).', createdAtUtc: '2025-07-12T09:00:00Z', files: [
          { id: 'rnf-auto-25-1', name: 'schedule_of_cover_2025.pdf', kind: 'Contract', size: '180 KB', uploaded: '2025-07-12' },
        ] },
        { id: 'rn-auto-24', fromDate: '2024-07-16', toDate: '2025-07-15', premium: 1340.00, premiumCurrencyCode: 'USD', coverageAmount: 26000.00, coverageCurrencyCode: 'USD', notes: null, createdAtUtc: '2024-07-10T09:00:00Z', files: [] },
      ],
    },
    {
      id: 'ip-travel', name: 'Annual Multi-Trip Travel', policyNumber: 'TRV-EU-7741', type: 'Travel',
      insurerId: 'c22', insuredAccountId: null, notes: 'Worldwide ex-US. Winter-sports add-on. Renew before the next trip.',
      archived: null, createdAtUtc: '2025-05-20T09:00:00Z',
      files: [],
      renewals: [
        { id: 'rn-travel-25', fromDate: '2025-06-01', toDate: '2026-05-31', premium: 340.00, premiumCurrencyCode: 'EUR', coverageAmount: 150000.00, coverageCurrencyCode: 'EUR', notes: 'Covered the Lofoten + Lisbon trips.', createdAtUtc: '2025-05-20T09:00:00Z', files: [
          { id: 'rnf-travel-25-1', name: 'travel_policy_2025.pdf', kind: 'PolicyDocument', size: '120 KB', uploaded: '2025-05-20' },
        ] },
      ],
    },
    {
      id: 'ip-life', name: 'Term Life — 20 Year', policyNumber: 'LIFE-20Y-33180', type: 'Life',
      insurerId: 'c21', insuredAccountId: null, notes: 'Level term, 20-year. Beneficiary on file. Cover starts at the next anniversary.',
      archived: null, createdAtUtc: '2026-06-02T09:00:00Z',
      files: [
        { id: 'ipf-life-1', name: 'term_life_contract.pdf', kind: 'Contract', size: '320 KB', uploaded: '2026-06-02', effectiveDate: '2026-09-01' },
      ],
      renewals: [
        { id: 'rn-life-26', fromDate: '2026-09-01', toDate: '2027-08-31', premium: 540.00, premiumCurrencyCode: 'USD', coverageAmount: 750000.00, coverageCurrencyCode: 'USD', notes: 'First annual term — cover begins Sep 1.', createdAtUtc: '2026-06-02T09:00:00Z', files: [] },
      ],
    },
    {
      id: 'ip-health', name: 'Family Health Plan', policyNumber: 'HLT-FAM-90021', type: 'Health',
      insurerId: 'c24', insuredAccountId: null, notes: 'Family of four. Outpatient + dental module.',
      archived: null, createdAtUtc: '2024-12-22T09:00:00Z',
      files: [
        { id: 'ipf-health-1', name: 'membership_handbook.pdf', kind: 'TermsAndConditions', size: '2.1 MB', uploaded: '2025-12-20' },
      ],
      renewals: [
        { id: 'rn-health-26', fromDate: '2026-01-01', toDate: '2026-12-31', premium: 6240.00, premiumCurrencyCode: 'USD', coverageAmount: 1000000.00, coverageCurrencyCode: 'USD', notes: 'Annual limit raised to $1M.', createdAtUtc: '2025-12-20T09:00:00Z', files: [
          { id: 'rnf-health-26-1', name: 'health_invoice_2026.pdf', kind: 'Invoice', size: '64 KB', uploaded: '2025-12-20', effectiveDate: '2026-01-01' },
        ] },
        { id: 'rn-health-25', fromDate: '2025-01-01', toDate: '2025-12-31', premium: 5880.00, premiumCurrencyCode: 'USD', coverageAmount: 750000.00, coverageCurrencyCode: 'USD', notes: null, createdAtUtc: '2024-12-22T09:00:00Z', files: [] },
      ],
    },
    {
      id: 'ip-pet', name: 'Bella — Pet Cover', policyNumber: null, type: 'Pet',
      insurerId: 'c24', insuredAccountId: null, notes: 'Quote received — no cover purchased yet.',
      archived: null, createdAtUtc: '2026-06-15T09:00:00Z',
      files: [],
      renewals: [],
    },
    {
      id: 'ip-cabin', name: 'Hytte — Cabin (Norway)', policyNumber: 'NO-HYT-44120', type: 'Property',
      insurerId: 'c23', insuredAccountId: null, notes: 'Mountain cabin, Hemsedal. Building + contents.',
      archived: null, createdAtUtc: '2025-12-10T09:00:00Z',
      files: [
        { id: 'ipf-cabin-1', name: 'forsikringsbevis_2026.pdf', kind: 'PolicyDocument', size: '300 KB', uploaded: '2025-12-12', effectiveDate: '2026-01-01' },
      ],
      renewals: [
        { id: 'rn-cabin-26', fromDate: '2026-01-01', toDate: '2026-12-31', premium: 8400.00, premiumCurrencyCode: 'NOK', coverageAmount: 3100000.00, coverageCurrencyCode: 'NOK', notes: null, createdAtUtc: '2025-12-12T09:00:00Z', files: [] },
      ],
    },
    {
      id: 'ip-art', name: 'Fine Art & Valuables Rider', policyNumber: 'CH-ART-2010', type: 'Contents',
      insurerId: 'c23', insuredAccountId: null, notes: 'Scheduled valuables — worldwide cover. Premium billed in CHF.',
      archived: null, createdAtUtc: '2026-01-20T09:00:00Z',
      files: [],
      renewals: [
        { id: 'rn-art-26', fromDate: '2026-02-01', toDate: '2027-01-31', premium: 980.00, premiumCurrencyCode: 'CHF', coverageAmount: 220000.00, coverageCurrencyCode: 'CHF', notes: 'No CHF→USD rate on file — excluded from converted totals.', createdAtUtc: '2026-01-20T09:00:00Z', files: [] },
      ],
    },
  ];

  // ---- Lookups + helpers -----------------------------------------------------
  Object.assign(H, {
    insurancePolicyTypeInfo(key) {
      return D.insurancePolicyTypeByKey[key]
        || { key, label: key || 'Other', icon: 'shield', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
    },
    policyFileTypeInfo(key) {
      return D.policyFileTypeByKey[key]
        || { key, label: key || 'Other', icon: 'insert_drive_file', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
    },

    // Currency-aware money. Symbol prefix + grouped digits at the currency's
    // minor units. Mirrors taxMoney's style for cross-feature consistency.
    insMoney(n, cur = 'USD') {
      if (n == null) return '—';
      const c = D.currencyByCode[cur] || { symbol: cur, minorUnits: 2 };
      const sign = n < 0 ? '−' : '';
      const abs = Math.abs(n);
      const digits = c.minorUnits != null ? c.minorUnits : 2;
      return `${sign}${c.symbol || cur} ${abs.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
    },
    // Compact money for tight figures / axes: 1500000 → "$ 1.5M".
    insMoneyCompact(n, cur = 'USD') {
      if (n == null) return '—';
      const c = D.currencyByCode[cur] || { symbol: cur };
      const sym = c.symbol || cur;
      const sign = n < 0 ? '−' : '';
      const abs = Math.abs(n);
      let s;
      if (abs >= 1e6) s = (abs / 1e6).toFixed(abs % 1e6 ? 2 : 0).replace(/\.?0+$/, '') + 'M';
      else if (abs >= 1e3) s = (abs / 1e3).toFixed(abs % 1e3 ? 1 : 0).replace(/\.?0+$/, '') + 'k';
      else s = abs.toLocaleString('en-US', { maximumFractionDigits: 0 });
      return `${sign}${sym} ${s}`;
    },

    // The request's UTC "today" as 'YYYY-MM-DD' (a single value per call site).
    insToday() { return new Date().toISOString().slice(0, 10); },
    insDateOnly(iso) { return iso ? String(iso).slice(0, 10) : null; },

    // Whole-day difference toDate − today (negative = already past).
    insDaysUntil(dateIso, today) {
      if (!dateIso) return null;
      const a = new Date(String(dateIso).slice(0, 10) + 'T00:00:00Z');
      const b = new Date((today || H.insToday()) + 'T00:00:00Z');
      return Math.round((a - b) / 86400000);
    },

    // The current renewal: the one whose [FromDate, ToDate] window contains
    // today. If overlaps make several match, tie-break on latest FromDate, then
    // latest CreatedAtUtc. Null when none contains today (Upcoming/Lapsed/None).
    insCurrentRenewal(policy, today) {
      const t = today || H.insToday();
      const inWindow = (policy.renewals || []).filter(r =>
        H.insDateOnly(r.fromDate) <= t && t <= H.insDateOnly(r.toDate));
      if (!inWindow.length) return null;
      return inWindow.slice().sort((a, b) => {
        if (a.fromDate !== b.fromDate) return a.fromDate < b.fromDate ? 1 : -1;
        return (a.createdAtUtc || '') < (b.createdAtUtc || '') ? 1 : -1;
      })[0];
    },

    // Ordered, deterministic coverage-status evaluation (spec §5). Returns
    // { key, expiringSoon } where key ∈ Active|ExpiringSoon|Upcoming|Lapsed|NoCoverage.
    // ExpiringSoon is a sub-state of Active (both mean "covered today").
    insCoverageStatus(policy, today, windowDays) {
      const t = today || H.insToday();
      // Archived takes precedence over the derived coverage state (mirrors
      // Contracts): an archived policy reads as Archived, its terminal status.
      if (policy.archived) return { key: 'Archived', expiringSoon: false };
      const win = windowDays != null ? windowDays : D.INSURANCE_EXPIRING_WINDOW_DAYS;
      const renewals = policy.renewals || [];
      if (!renewals.length) return { key: 'NoCoverage', expiringSoon: false };

      const current = H.insCurrentRenewal(policy, t);
      if (current) {
        const days = H.insDaysUntil(current.toDate, t);
        const expiring = days != null && days <= win;
        return { key: expiring ? 'ExpiringSoon' : 'Active', expiringSoon: expiring };
      }
      // No window contains today: Upcoming if the earliest start is in the
      // future, else Lapsed (latest end is in the past).
      const earliestFrom = renewals.reduce((m, r) => (H.insDateOnly(r.fromDate) < m ? H.insDateOnly(r.fromDate) : m), H.insDateOnly(renewals[0].fromDate));
      if (earliestFrom > t) return { key: 'Upcoming', expiringSoon: false };
      return { key: 'Lapsed', expiringSoon: false };
    },

    // The status's display vocabulary: label, chip tone, status dot, and a glyph.
    // Active=mint/income · ExpiringSoon=amber/pending · Lapsed=coral/expense ·
    // Upcoming=sea/info · NoCoverage=muted/outline · Archived=muted/outline.
    // Icons for the statuses shared with Contracts (Active · Upcoming · the
    // ended state · Archived) mirror conStatusMeta so a status reads identically
    // across the Contracts and Policies pages; the policy-only states
    // (ExpiringSoon, NoCoverage) keep their own distinct glyphs.
    insCoverageStatusMeta(key) {
      const map = {
        Active:       { key: 'Active',       label: 'Active',        tone: 'income',  dot: true,  icon: 'task_alt' },
        ExpiringSoon: { key: 'ExpiringSoon', label: 'Expiring soon', tone: 'pending', dot: true,  icon: 'hourglass_bottom' },
        Lapsed:       { key: 'Lapsed',       label: 'Expired',       tone: 'expense', dot: true,  icon: 'event_busy' },
        Upcoming:     { key: 'Upcoming',     label: 'Upcoming',      tone: 'info',    dot: true,  icon: 'schedule' },
        NoCoverage:   { key: 'NoCoverage',   label: 'No coverage',   tone: 'outline', dot: true,  icon: 'remove_moderator' },
        Archived:     { key: 'Archived',     label: 'Archived',      tone: 'outline', dot: true,  icon: 'inventory_2' },
      };
      return map[key] || map.NoCoverage;
    },

    // Minimal cross-claim projections (spec §10 #4) — id, name, type only.
    insInsurer(policy) {
      const c = policy.insurerId && D.contactById[policy.insurerId];
      return c ? { contactId: c.id, name: c.name, type: c.type } : null;
    },
    insInsuredAccount(policy) {
      const a = policy.insuredAccountId && D.accountById[policy.insuredAccountId];
      return a ? { accountId: a.id, name: a.name, type: a.type } : null;
    },

    // Non-archived policies — the set the summary aggregates over.
    insActivePolicies(policies) { return (policies || D.insurancePolicies).filter(p => !p.archived); },

    // File count across policy-level + all renewal-level attachments.
    insFileCount(policy) {
      return (policy.files || []).length + (policy.renewals || []).reduce((s, r) => s + (r.files || []).length, 0);
    },

    // Latest directed exchange rate for a (from,to) pair, or null.
    insLatestRate(from, to) {
      let best = null;
      for (const r of D.exchangeRates) {
        if (r.from === from && r.to === to && (!best || r.asOf > best.asOf)) best = r;
      }
      return best ? best.rate : null;
    },
    // Convert `amount` from one currency to `base`. USD is the workspace base, so
    // we hop via USD using the stored USD→X rates. A missing leg → null (the
    // caller lists the currency as unconverted; never silently zeroed).
    insConvert(amount, from, base) {
      if (amount == null) return null;
      if (from === base) return amount;
      const toUsd = from === 'USD' ? 1 : (() => { const r = H.insLatestRate('USD', from); return r ? 1 / r : null; })();
      if (toUsd == null) return null;
      const usdToBase = base === 'USD' ? 1 : H.insLatestRate('USD', base);
      if (usdToBase == null) return null;
      return amount * toUsd * usdToBase;
    },

    // Portfolio summary (spec §7 InsurancePortfolioSummary). Buckets non-archived
    // policies by derived status; sums each policy's CURRENT renewal premium +
    // coverage grouped by currency; optionally converts per-currency subtotals to
    // a base currency, excluding any currency lacking a rate.
    insPortfolioSummary(policies, today, baseCurrency) {
      const t = today || H.insToday();
      const all = policies || D.insurancePolicies;
      const live = H.insActivePolicies(policies);
      const counts = { Active: 0, ExpiringSoon: 0, Lapsed: 0, Upcoming: 0, NoCoverage: 0, Archived: 0 };
      // Status counts span EVERY policy (archived included) so the status filter
      // and By-status breakdown can surface archived records — mirrors Contracts.
      for (const p of all) {
        const k = H.insCoverageStatus(p, t).key;
        counts[k] = (counts[k] || 0) + 1;
      }

      const premium = {}; // currency → amount
      const coverage = {};
      const byType = {}; // policy-type key → count
      // Premium / coverage / type distribution are over LIVE (non-archived)
      // policies only — an archived policy is no longer in force.
      for (const p of live) {
        byType[p.type] = (byType[p.type] || 0) + 1;
        const cur = H.insCurrentRenewal(p, t);
        if (cur) {
          premium[cur.premiumCurrencyCode] = (premium[cur.premiumCurrencyCode] || 0) + cur.premium;
          coverage[cur.coverageCurrencyCode] = (coverage[cur.coverageCurrencyCode] || 0) + cur.coverageAmount;
        }
      }

      const toRows = (obj) => Object.entries(obj)
        .map(([currencyCode, amount]) => ({ currencyCode, amount }))
        .sort((a, b) => (a.currencyCode < b.currencyCode ? -1 : 1));

      const out = {
        totalPolicies: live.length,
        countsByStatus: counts,
        typeRows: (D.insurancePolicyTypes || [])
          .map(t2 => ({ key: t2.key, label: t2.label, icon: t2.icon, color: t2.color, count: byType[t2.key] || 0 }))
          .filter(r => r.count > 0),
        premiumByCurrency: toRows(premium),
        coverageByCurrency: toRows(coverage),
        baseCurrency: baseCurrency || null,
        convertedTotalPremium: null,
        convertedTotalCoverage: null,
        unconvertedCurrencies: [],
      };

      if (baseCurrency) {
        const unconverted = new Set();
        const sum = (rows) => rows.reduce((acc, { currencyCode, amount }) => {
          const v = H.insConvert(amount, currencyCode, baseCurrency);
          if (v == null) { unconverted.add(currencyCode); return acc; }
          return acc + v;
        }, 0);
        out.convertedTotalPremium = sum(out.premiumByCurrency);
        out.convertedTotalCoverage = sum(out.coverageByCurrency);
        out.unconvertedCurrencies = [...unconverted].sort();
      }
      return out;
    },
  });
})();
