/* Net-worth history — the reconstructed series behind the dashboard chart.
   Mirrors GET /api/accounts/net-worth-history: every point is a figure as of
   that point's instant, a point is labelled and dated at its period END, and a
   point carries counts (never lists) saying how it was arrived at.

   Nothing here is scaled by today's figure. The series is built from monthly
   movements walked BACK from the real current total, so the last point equals
   the accounts rollup exactly and every earlier point differs from its
   neighbour by a real movement. */
(function () {
  const D = window.OdysseyData;

  /* Monthly movement (period end → the change over that period), oldest last.
     Two periods are not plain measurements:
       • 'partial'  — Vanguard Brokerage (EUR) has no EUR → USD rate for that
                      month, so it contributed 0 and the figure is understated.
                      Same condition the Accounts page flags for account 4.
       • 'revalued' — a Maple St Residence estimate took effect that month, so
                      the step is a real revaluation, disclosed not smoothed. */
  const NWH_PERIODS = [
    { date: '2024-12-01', label: 'Nov ’24', move:  18420 },
    { date: '2025-01-01', label: 'Dec',     move:   9880 },
    { date: '2025-02-01', label: 'Jan ’25', move:  21460 },
    { date: '2025-03-01', label: 'Feb',     move:  -6350 },
    { date: '2025-04-01', label: 'Mar',     move:  17230 },
    { date: '2025-05-01', label: 'Apr',     move:  24110 },
    { date: '2025-06-01', label: 'May',     move:  12640 },
    { date: '2025-07-01', label: 'Jun',     move:  19870 },
    { date: '2025-08-01', label: 'Jul',     move: -41500, kind: 'partial' },
    { date: '2025-09-01', label: 'Aug',     move:  63240 },
    { date: '2025-10-01', label: 'Sep',     move:  14380 },
    { date: '2025-11-01', label: 'Oct',     move:  -9120 },
    { date: '2025-12-01', label: 'Nov',     move:  22750 },
    { date: '2026-01-01', label: 'Dec',     move:  -4460 },
    { date: '2026-02-01', label: 'Jan ’26', move:  27310 },
    { date: '2026-03-01', label: 'Feb',     move:  16490 },
    { date: '2026-04-01', label: 'Mar',     move:  -2870 },
    { date: '2026-05-01', label: 'Apr',     move:  20140 },
    { date: '2026-06-01', label: 'May',     move:  11760 },
    { date: '2026-07-01', label: 'Jun',     move:  48200, kind: 'revalued' },
    { date: '2026-08-01', label: 'Jul',     move:  13920 },
    { date: '2026-09-01', label: 'Aug',     move:   8640 },
    { date: '2026-09-16', label: 'Sep',     move:  15310 },
  ];

  // The share of net worth held in liabilities, per period — the response
  // carries assets and liabilities separately, not just the net figure.
  const NWH_LIABILITY_SHARE = 0.34;

  /* Build the response for a given current net worth. `emptyReason` is one of
     NotBuilt · NoAccounts · NothingConvertible · WindowBeforeFirstAccount and
     is the ONLY thing that discriminates an empty series — the client is never
     asked to infer a cause the payload does not carry. */
  D.buildNetWorthHistory = function (currentNetWorth, options) {
    const o = options || {};
    if (o.emptyReason) {
      return {
        mainCurrencyCode: o.mainCurrencyCode || 'USD',
        interval: 'Monthly', from: NWH_PERIODS[0].date,
        to: NWH_PERIODS[NWH_PERIODS.length - 1].date,
        emptyReason: o.emptyReason, points: [], unconvertedAccounts: [],
      };
    }

    // Walk backwards from the real figure so the last point IS the real figure.
    const values = new Array(NWH_PERIODS.length);
    let v = currentNetWorth;
    for (let i = NWH_PERIODS.length - 1; i >= 0; i--) {
      values[i] = Math.round(v);
      v -= NWH_PERIODS[i].move;
    }

    const points = NWH_PERIODS.map(function (p, i) {
      const net = values[i];
      const liabilities = Math.round(Math.abs(net) * NWH_LIABILITY_SHARE);
      return {
        date: p.date,
        label: p.label,
        netWorth: net,
        totalAssets: net + liabilities,
        totalLiabilities: liabilities,
        unconvertedAccountCount: p.kind === 'partial' ? 1 : 0,
        revaluedAccountCount: p.kind === 'revalued' ? 1 : 0,
        contributingAccountCount: p.kind === 'partial' ? 5 : 6,
        kind: p.kind || 'normal',
      };
    });

    return {
      mainCurrencyCode: 'USD',
      interval: 'Monthly',
      from: NWH_PERIODS[0].date,
      to: NWH_PERIODS[NWH_PERIODS.length - 1].date,
      emptyReason: null,
      points: points,
      unconvertedAccounts: [{ accountId: '4', name: 'Vanguard Brokerage', currencyCode: 'EUR' }],
    };
  };

  // The copy for each empty cause. Never "no data" — the cause is known.
  D.netWorthEmptyCopy = {
    NotBuilt: 'Net-worth history is not available yet.',
    NoAccounts: 'No accounts to chart yet.',
    NothingConvertible: 'Net worth could not be converted to {currency} for any period.',
    WindowBeforeFirstAccount: 'No accounts existed in this period.',
    LoadFailed: 'Net-worth history could not be loaded.',
  };
})();
