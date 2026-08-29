/* Seed data + helpers for the Subscriptions feature (Subscriptions.jsx).
   ----------------------------------------------------------------------------
   Shapes mirror the spec's Odyssey.Finance entities / DTOs:
     • Subscription       { name, externalId?, contactId?, startDate,
                            endDate?, amount + currencyCode, interval,
                            intervalCount ("every N", int ≥ 1, default 1),
                            firstBillingDate, notes?, paused?, archived?,
                            createdAtUtc }
     • BillingInterval    Daily | Weekly | Monthly | Yearly (default Monthly)
     • SubscriptionContactReference — data-minimised { contactId,
                            name, type } projection (no org number / description /
                            normalized name), mirroring insurance's InsurerReference.

   A subscription is a pure record-keeping row — no transactions, no scheduling.
   The per-cycle billing position ("day 15", "15 Jan", "Wed") is DERIVED at read
   time from firstBillingDate + interval, never stored. Paused and Archived are
   two INDEPENDENT nullable timestamps (non-null ⇒ in that state); a subscription
   may be both. A subscription is additionally ENDED — a DERIVED state, never
   stored — once its endDate is set and on/before today (endDate ≤ today); Ended
   supersedes Paused and stops all billing derivations. Dates are date-only 'YYYY-MM-DD' (DateOnly on the server);
   paused / archived / createdAtUtc are full timestamps. The BillingInterval
   registry lives here (billingIntervals), sibling of the other type registries
   in data.js. Amounts are shown in their own currency — no normalization
   (a Non-Goal), so the summary buckets by interval + status only. */

(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  /* ---- BillingInterval registry. Mirrors the DS BILLING_INTERVALS export and
     the C# enum: key · label · numeric enum value (the sort order) · icon ·
     color · soft tint. Hues sit in the shared categorical band with the other
     registries so the interval glyph reads as one family; brand tide stays out. */
  D.billingIntervals = [
    { key: 'Daily',   label: 'Daily',   enumValue: 0, icon: 'today',          color: 'oklch(0.79 0.13 205)', soft: 'oklch(0.79 0.13 205 / 0.16)' },
    { key: 'Weekly',  label: 'Weekly',  enumValue: 1, icon: 'view_week',      color: 'oklch(0.78 0.14 168)', soft: 'oklch(0.78 0.14 168 / 0.16)' },
    { key: 'Monthly', label: 'Monthly', enumValue: 2, icon: 'calendar_month', color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)' },
    { key: 'Yearly',  label: 'Yearly',  enumValue: 3, icon: 'event_repeat',   color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  ];
  D.billingIntervalByKey = Object.fromEntries(D.billingIntervals.map((t) => [t.key, t]));

  /* ---- Seed subscriptions. Anchored around mid-2026 so states are stable:
     a mix of intervals (Daily / Weekly / Monthly / Yearly), currencies
     (USD / EUR / NOK), external ids present and absent, contacts linked
     and not, one Paused, one Archived, one that is both, and one Ended (a past
     end date, not archived). ---- */
  D.subscriptions = [
    {
      id: 'sub-netflix', name: 'Netflix', externalId: 'A-8841-2205', contactId: null,
      startDate: '2021-03-01', endDate: null, amount: 15.49, currencyCode: 'USD',
      interval: 'Monthly', firstBillingDate: '2021-03-15', notes: 'Standard with ads → Premium since 2024.',
      paused: null, archived: null, createdAtUtc: '2021-03-01T09:00:00Z',
    },
    {
      id: 'sub-spotify', name: 'Spotify Premium', externalId: 'SPOT-4471', contactId: 'c3',
      startDate: '2020-01-10', endDate: null, amount: 11.99, currencyCode: 'USD',
      interval: 'Monthly', firstBillingDate: '2020-01-10', notes: null,
      paused: null, archived: null, createdAtUtc: '2020-01-10T09:00:00Z',
    },
    {
      id: 'sub-icloud', name: 'iCloud+ 200GB', externalId: null, contactId: null,
      startDate: '2019-06-01', endDate: null, amount: 2.99, currencyCode: 'USD',
      interval: 'Monthly', firstBillingDate: '2019-06-08', notes: 'Family sharing enabled.',
      paused: null, archived: null, createdAtUtc: '2019-06-01T09:00:00Z',
    },
    {
      id: 'sub-home-ins', name: 'Home & Contents (tracked)', externalId: 'HC-2026-99182', contactId: 'c12',
      startDate: '2026-01-01', endDate: null, amount: 1840.00, currencyCode: 'USD',
      interval: 'Yearly', firstBillingDate: '2026-01-15', notes: 'Billed elsewhere — tracked here for the record. Mirrors the insurance policy.',
      paused: null, archived: null, createdAtUtc: '2025-12-18T10:00:00Z',
    },
    {
      id: 'sub-rent', name: 'Apartment Rent', externalId: 'LEASE-22B', contactId: 'c7',
      startDate: '2024-09-01', endDate: '2026-08-31', amount: 2400.00, currencyCode: 'USD',
      interval: 'Monthly', firstBillingDate: '2024-09-01', notes: 'Lease renews Sep 2026.',
      paused: null, archived: null, createdAtUtc: '2024-08-20T09:00:00Z',
    },
    {
      id: 'sub-figma', name: 'Figma — Organization seat', externalId: 'ORG-5567', contactId: null,
      startDate: '2023-02-01', endDate: null, amount: 45.00, currencyCode: 'EUR',
      interval: 'Monthly', intervalCount: 2, firstBillingDate: '2023-02-01', notes: 'One editor seat, billed every other month.',
      paused: null, archived: null, createdAtUtc: '2023-02-01T09:00:00Z',
    },
    {
      id: 'sub-adobe', name: 'Adobe Creative Cloud', externalId: null, contactId: null,
      startDate: '2022-11-01', endDate: null, amount: 599.88, currencyCode: 'USD',
      interval: 'Yearly', firstBillingDate: '2022-11-03', notes: 'Paused while on the free trial of an alternative.',
      paused: '2026-05-02T09:00:00Z', archived: null, createdAtUtc: '2022-11-01T09:00:00Z',
    },
    {
      id: 'sub-news', name: 'The Daily Ledger', externalId: 'RDR-90183', contactId: null,
      startDate: '2025-01-01', endDate: null, amount: 0.99, currencyCode: 'USD',
      interval: 'Daily', firstBillingDate: '2025-01-01', notes: 'Per-issue digital edition.',
      paused: null, archived: null, createdAtUtc: '2025-01-01T09:00:00Z',
    },
    {
      id: 'sub-cabin-power', name: 'Hytte Power Plan', externalId: 'NO-77-4410', contactId: 'c5',
      startDate: '2025-10-01', endDate: null, amount: 420.00, currencyCode: 'NOK',
      interval: 'Weekly', firstBillingDate: '2025-10-01', notes: 'Winter tariff, billed weekly.',
      paused: null, archived: null, createdAtUtc: '2025-09-25T09:00:00Z',
    },
    {
      id: 'sub-meal', name: 'FreshBox Meal Kit', externalId: null, contactId: 'c8',
      startDate: '2025-04-01', endDate: null, amount: 79.00, currencyCode: 'USD',
      interval: 'Weekly', firstBillingDate: '2025-04-02', notes: 'Paused over the summer — resuming in autumn.',
      paused: '2026-06-10T09:00:00Z', archived: null, createdAtUtc: '2025-04-01T09:00:00Z',
    },
    {
      id: 'sub-streamly', name: 'Streamly (annual trial)', externalId: 'TRIAL-3390', contactId: null,
      startDate: '2025-06-01', endDate: '2026-06-01', amount: 89.00, currencyCode: 'USD',
      interval: 'Yearly', firstBillingDate: '2025-06-01', notes: 'One-year promo — term lapsed, kept for the record.',
      paused: null, archived: null, createdAtUtc: '2025-06-01T09:00:00Z',
    },
    {
      id: 'sub-gym', name: 'FitZone Gym', externalId: 'MBR-12345', contactId: 'c11',
      startDate: '2022-01-05', endDate: '2025-03-01', amount: 39.00, currencyCode: 'USD',
      interval: 'Monthly', firstBillingDate: '2022-01-05', notes: 'Cancelled — kept for history.',
      paused: null, archived: '2025-03-02T09:00:00Z', createdAtUtc: '2022-01-05T09:00:00Z',
    },
    {
      id: 'sub-domain', name: 'Domain — odyssey.app', externalId: 'DN-2019-0007', contactId: null,
      startDate: '2019-01-01', endDate: null, amount: 32.00, currencyCode: 'USD',
      interval: 'Yearly', intervalCount: 2, firstBillingDate: '2019-01-12', notes: 'Registered on a two-year cycle.',
      paused: null, archived: null, createdAtUtc: '2019-01-01T09:00:00Z',
    },
  ];

  // ---- Lookups + helpers -----------------------------------------------------
  const WD_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const MON_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  // Singular unit noun per interval — for the "Every N …" multiplier label.
  const SUB_UNIT_NOUN = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };

  Object.assign(H, {
    subIntervalInfo(key) {
      return D.billingIntervalByKey[key]
        || { key, label: key || 'Monthly', enumValue: 2, icon: 'autorenew', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
    },

    // Currency-aware money — symbol prefix + grouped digits at the currency's
    // minor units. Mirrors insMoney so amounts read identically across features.
    subMoney(n, cur = 'USD') {
      if (n == null) return '—';
      const c = D.currencyByCode[cur] || { symbol: cur, minorUnits: 2 };
      const sign = n < 0 ? '−' : '';
      const abs = Math.abs(n);
      const digits = c.minorUnits != null ? c.minorUnits : 2;
      return `${sign}${c.symbol || cur} ${abs.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
    },

    // Derived per-cycle billing position (display only) — never stored.
    // Monthly → "day 15"; Yearly → "15 Jan"; Weekly → "Wed"; Daily → null.
    // firstBillingDate is parsed as UTC so the day/weekday never drifts by a zone.
    subBillingAnchor(sub) {
      if (!sub || !sub.firstBillingDate) return null;
      const [y, m, d] = String(sub.firstBillingDate).slice(0, 10).split('-').map(Number);
      if (!y || !m || !d) return null;
      switch (sub.interval) {
        case 'Monthly': return `day ${d}`;
        case 'Yearly':  return `${d} ${MON_SHORT[m - 1]}`;
        case 'Weekly':  return WD_SHORT[new Date(Date.UTC(y, m - 1, d)).getUTCDay()];
        default:        return null;
      }
    },
    // "Monthly · day 15" — interval label + derived anchor as one text string.
    subCadenceText(sub) {
      const anchor = H.subBillingAnchor(sub);
      return anchor ? `${H.subIntervalLabel(sub)} · ${anchor}` : H.subIntervalLabel(sub);
    },

    // The billing multiplier — "every N intervals". Stored as an integer that
    // defaults to 1; coerced to a whole number ≥ 1 here so every derivation is
    // safe against a missing / bad value.
    subIntervalCount(sub) {
      const n = Math.round(Number(sub && sub.intervalCount));
      return Number.isFinite(n) && n > 0 ? n : 1;
    },
    // The cadence label, honouring the multiplier: count 1 → the plain enum label
    // ("Monthly"); count > 1 → "Every N months / years / weeks / days".
    subIntervalLabel(sub) {
      const info = H.subIntervalInfo(sub.interval);
      const n = H.subIntervalCount(sub);
      if (n <= 1) return info.label;
      const noun = SUB_UNIT_NOUN[sub.interval] || 'cycle';
      return `Every ${n} ${noun}${n === 1 ? '' : 's'}`;
    },

    // Data-minimised contact projection (spec §10) — id, name, type only.
    // Resolves the linked contact even when archived (so its name still
    // shows), returning null for no link or a dangling id.
    subContact(sub) {
      const c = sub && sub.contactId && D.contactById[sub.contactId];
      return c ? { contactId: c.id, name: c.name, type: c.type } : null;
    },

    // Non-archived subscriptions — the default (Active) list set.
    subActive(subs) { return (subs || D.subscriptions).filter((s) => !s.archived); },

    // DERIVED terminal state (never stored): a subscription is Ended once its
    // endDate is set and falls on/before today (endDate ≤ today). Ending it from
    // the row action sets endDate to today, so it reads Ended immediately. This
    // is the single source of truth for "no longer billing by date" — the
    // next-billing and run-rate derivations both defer to it.
    subEnded(sub, today) {
      if (!sub || !sub.endDate) return false;
      return String(sub.endDate).slice(0, 10) <= (today || H.subToday());
    },

    // Summary buckets (spec: no multi-currency spend normalization). Counts over
    // ALL subscriptions for status (so Archived can surface), and by-interval over
    // the LIVE (non-archived) set — an archived subscription is no longer running.
    subSummary(subs) {
      const all = subs || D.subscriptions;
      const live = all.filter((s) => !s.archived);
      const byInterval = {};
      for (const s of live) byInterval[s.interval] = (byInterval[s.interval] || 0) + 1;
      const status = {
        active: live.filter((s) => !s.paused && !H.subEnded(s)).length,
        paused: live.filter((s) => !!s.paused && !H.subEnded(s)).length,
        ended: live.filter((s) => H.subEnded(s)).length,
        archived: all.filter((s) => !!s.archived).length,
      };
      return {
        total: live.length,
        countsByStatus: status,
        intervalRows: D.billingIntervals
          .map((t) => ({ key: t.key, label: t.label, icon: t.icon, color: t.color, count: byInterval[t.key] || 0 }))
          .filter((r) => r.count > 0),
      };
    },

    // ---- Derived scheduling / run-rate (all display-only; nothing is stored) ----

    // The request's "today" as 'YYYY-MM-DD' (a single value per call site).
    subToday() { return new Date().toISOString().slice(0, 10); },
    // Whole-day difference dateIso − today (negative = already past).
    subDaysUntil(dateIso, today) {
      if (!dateIso) return null;
      const a = new Date(String(dateIso).slice(0, 10) + 'T00:00:00Z');
      const b = new Date((today || H.subToday()) + 'T00:00:00Z');
      return Math.round((a - b) / 86400000);
    },
    // 'Jul 8' — compact month + day, parsed as UTC so it never drifts by a zone.
    subDateMd(iso) {
      if (!iso) return '—';
      const [y, m, d] = String(iso).slice(0, 10).split('-').map(Number);
      return `${MON_SHORT[m - 1]} ${d}`;
    },
    // 'in 3 days' / 'today' / 'tomorrow' — the relative renewal word.
    subRelDays(days) {
      if (days == null) return '';
      if (days <= 0) return 'today';
      if (days === 1) return 'tomorrow';
      if (days < 7) return `in ${days} days`;
      if (days < 14) return 'in 1 week';
      return `in ${Math.round(days / 7)} weeks`;
    },

    // The NEXT billing date on/after `today`, DERIVED from firstBillingDate +
    // interval (never stored). Rolls the anchor forward by whole intervals;
    // month/year steps clamp to the month length (a day-31 anchor → the 30th /
    // 28th). Returns null when there is nothing to bill: archived, paused, or the
    // next date would fall past the subscription's end date.
    subNextBilling(sub, today) {
      if (!sub || sub.archived || sub.paused || H.subEnded(sub, today) || !sub.firstBillingDate) return null;
      const toUTC = (iso) => { const [y, m, d] = String(iso).slice(0, 10).split('-').map(Number); return new Date(Date.UTC(y, m - 1, d)); };
      const addMonths = (dt, n) => {
        const y = dt.getUTCFullYear(), mo = dt.getUTCMonth(), d = dt.getUTCDate();
        const first = new Date(Date.UTC(y, mo + n, 1));
        const dim = new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 1, 0)).getUTCDate();
        first.setUTCDate(Math.min(d, dim));
        return first;
      };
      const t = toUTC(today || H.subToday());
      const end = sub.endDate ? toUTC(sub.endDate) : null;
      const step = H.subIntervalCount(sub); // "every N intervals"
      let cur = toUTC(sub.firstBillingDate);
      if (cur < t) {
        switch (sub.interval) {
          case 'Daily': {
            const days = Math.ceil((t - cur) / 86400000);
            const n = Math.ceil(days / step);
            cur = new Date(cur.getTime() + n * step * 86400000);
            break;
          }
          case 'Weekly': {
            const weeks = (t - cur) / (7 * 86400000);
            const n = Math.ceil(weeks / step);
            cur = new Date(cur.getTime() + n * step * 7 * 86400000);
            break;
          }
          case 'Yearly': { while (cur < t) cur = addMonths(cur, 12 * step); break; }
          default:       { while (cur < t) cur = addMonths(cur, step); break; } // Monthly
        }
      }
      if (end && cur > end) return null;
      return cur.toISOString().slice(0, 10);
    },

    // Estimated run-rate, bucketed PER CURRENCY (amounts are never converted
    // across currencies — a spec Non-Goal). Cadence IS normalized: each price is
    // projected to a monthly and a yearly figure via the interval. Paused,
    // archived, and already-ended subscriptions are excluded (not billing).
    subRunRate(subs, today, baseCurrency) {
      const t = today || H.subToday();
      const base = baseCurrency || (D.currencies.find((c) => c.base) || {}).code || 'USD';
      const F = {
        Daily:   { mo: 365.25 / 12, yr: 365.25 },
        Weekly:  { mo: 52.1775 / 12, yr: 52.1775 },
        Monthly: { mo: 1, yr: 12 },
        Yearly:  { mo: 1 / 12, yr: 1 },
      };
      const map = {};
      let topDriver = null;
      for (const s of (subs || D.subscriptions)) {
        if (s.archived || s.paused) continue;
        if (H.subEnded(s, t)) continue;
        const f = F[s.interval] || F.Monthly;
        // Divide by the "every N" multiplier: billing every 2 months halves the
        // monthly-equivalent spend.
        const every = H.subIntervalCount(s);
        const moRate = (s.amount * f.mo) / every;
        const yrRate = (s.amount * f.yr) / every;
        if (!map[s.currencyCode]) {
          const c = D.currencyByCode[s.currencyCode] || {};
          map[s.currencyCode] = { currency: s.currencyCode, symbol: c.symbol || s.currencyCode, base: !!c.base, monthly: 0, yearly: 0, count: 0 };
        }
        map[s.currencyCode].monthly += moRate;
        map[s.currencyCode].yearly += yrRate;
        map[s.currencyCode].count += 1;
        // Track the single biggest cost driver, compared on a monthly-equivalent
        // basis in the base currency (falls back to raw amount if no FX rate).
        const moBase = (H.insConvert ? H.insConvert(moRate, s.currencyCode, base) : null);
        const rank = moBase != null ? moBase : moRate;
        if (!topDriver || rank > topDriver.rank) {
          topDriver = { id: s.id, name: s.name, amount: s.amount, currencyCode: s.currencyCode, interval: s.interval, rank };
        }
      }
      // Base currency first, then by yearly run-rate descending.
      const rows = Object.values(map).sort((a, b) => (b.base - a.base) || (b.yearly - a.yearly));
      // Blended base-currency total — the Subscriptions analogue of the Insurance
      // portfolio's "≈ <total> / year": convert each currency subtotal to the
      // workspace base via the shared FX helper (hops through USD). A currency
      // without a rate is listed as unconverted, never silently zeroed.
      const convert = H.insConvert || ((amt, from, to) => (from === to ? amt : null));
      const unconverted = new Set();
      let convertedMonthly = 0, convertedYearly = 0, any = false;
      for (const r of rows) {
        const mo = convert(r.monthly, r.currency, base);
        const yr = convert(r.yearly, r.currency, base);
        if (mo == null || yr == null) { unconverted.add(r.currency); continue; }
        convertedMonthly += mo; convertedYearly += yr; any = true;
      }
      return {
        rows,
        baseCurrency: base,
        topDriver,
        convertedMonthly: any ? convertedMonthly : null,
        convertedYearly: any ? convertedYearly : null,
        unconvertedCurrencies: [...unconverted].sort(),
      };
    },

    // The soonest upcoming renewals within `windowDays` (default 45), each the
    // subscription's derived next-billing date; sorted ascending, capped.
    subUpcomingRenewals(subs, today, opts) {
      const t = today || H.subToday();
      const windowDays = (opts && opts.windowDays != null) ? opts.windowDays : 45;
      const limit = (opts && opts.limit != null) ? opts.limit : 6;
      const out = [];
      for (const s of (subs || D.subscriptions)) {
        const date = H.subNextBilling(s, t);
        if (!date) continue;
        const days = H.subDaysUntil(date, t);
        if (days == null || days > windowDays) continue;
        out.push({ sub: s, date, days });
      }
      out.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
      return out.slice(0, limit);
    },
  });
})();
