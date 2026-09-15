/* contract-events-data.js — the ContractEvent registry, seed and helpers.
   Tracks "Contract Events — Backend (Draft v6)".
   ----------------------------------------------------------------------------
   An event is a USER-MAINTAINED log entry on one contract: a type, a required
   `title`, an optional `description`, optional `notes`, and when it happened.

   Two things this file deliberately does NOT have:

   • No link columns. v5 removed `PrimaryContactId` / `SecondaryContactId` /
     `PrimaryAccountId` / `SecondaryAccountId` and everything they dragged with
     them (Non-Goal 5). Contact and account deletes are untouched by this
     feature, so there is no blocker probe and no detach helper here.

   • Nothing system-generated. Pausing, archiving or renewing a contract writes
     no event (§7.6 / Non-Goal 4), so no helper here is called from a contract
     mutation.

   Ordinals are a wire contract (§4) — persisted and present in request and
   response bodies — so the enumValue column is fixed forever. */
(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  /* ---- ContractEventType (§4) — enum order, `Other` (the default) last ---- */
  D.contractEventTypes = [
    { key: 'Signed',       label: 'Signed',       enumValue: 0, icon: 'history_edu',   color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'The agreement was executed by the parties.' },
    { key: 'Amended',      label: 'Amended',      enumValue: 1, icon: 'edit_document', color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)',  desc: 'A variation or addendum changed the terms.' },
    { key: 'Renewed',      label: 'Renewed',      enumValue: 2, icon: 'autorenew',     color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)', desc: 'The agreement was taken up for a further term.' },
    { key: 'Extended',     label: 'Extended',     enumValue: 3, icon: 'more_time',     color: 'oklch(0.78 0.14 145)', soft: 'oklch(0.78 0.14 145 / 0.16)', desc: 'The existing term was lengthened.' },
    { key: 'NoticeGiven',  label: 'Notice given', enumValue: 4, icon: 'campaign',      color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)',  desc: 'Notice to end the agreement was served, by either side.' },
    { key: 'Terminated',   label: 'Terminated',   enumValue: 5, icon: 'gavel',         color: 'oklch(0.72 0.15 25)',  soft: 'oklch(0.72 0.15 25 / 0.16)',  desc: 'The agreement was brought to an end. This does not change the contract’s status.' },
    { key: 'PriceChanged', label: 'Price changed',enumValue: 6, icon: 'price_change',  color: 'oklch(0.76 0.14 320)', soft: 'oklch(0.76 0.14 320 / 0.16)', desc: 'What the agreement costs was renegotiated or re-set.' },
    { key: 'EmailSent',    label: 'Email sent',   enumValue: 7, icon: 'outgoing_mail', color: 'oklch(0.77 0.14 205)', soft: 'oklch(0.77 0.14 205 / 0.16)', desc: 'Correspondence you sent about the agreement. Name the recipient in the title or description.' },
    { key: 'Other',        label: 'Other',        enumValue: 8, icon: 'more_horiz',    color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The entity default — anything the eight members do not name. The title carries it.' },
  ];
  D.contractEventTypeByKey = Object.fromEntries(D.contractEventTypes.map(t => [t.key, t]));

  /* ---- Attribution (§7.3) — the API returns a LABEL, never a user id ---- */
  D.cevUsers = {
    'u-jane': 'Jane Doe',
    'u-sam': 'Sam Okafor',
    'u-mira': 'Mira Lindqvist',
  };

  /* ---- Seed ----------------------------------------------------------------
     `createdByUserId` stands in for what the server stamps; the read projection
     turns it into `createdBy`. A null id is an author who has since been
     deleted — SET NULL on the column (§4), read back as "Unknown user".

     The three free-text fields are seeded with their three distinct roles
     (§4.1): `title` is the short label, `description` is the account of what
     happened, `notes` is the user's own working note — which the timeline does
     not render, though every non-Guest user of the deployment can read it. */
  D.contractEventSeed = {
    'ct-lease': [
      { id: 'cev-l1', contractId: 'ct-lease', type: 'Signed', title: 'Tenancy agreement signed', description: 'Both counterparts signed at the letting office and the deposit was protected the same day.', notes: null, occurredAt: '2025-08-27T14:05:00Z', createdByUserId: 'u-jane', createdAtUtc: '2025-08-27T16:20:00Z' },
      { id: 'cev-l2', contractId: 'ct-lease', type: 'EmailSent', title: 'Emailed landlord about the damp', description: 'Photos of the back bedroom attached. Asked for a contractor visit inside two weeks.', notes: 'Keep the photos — they are dated.', occurredAt: '2026-01-12T08:44:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-01-12T08:46:00Z' },
      { id: 'cev-l3', contractId: 'ct-lease', type: 'PriceChanged', title: 'Rent renegotiated to 2,250 USD', description: 'Agreed by phone with the letting agent, then confirmed in writing. A CPI basis was requested and refused; settled at 4.7%.', notes: 'Check last year’s letter before the next review.', occurredAt: '2026-02-19T11:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2026-02-19T11:32:00Z' },
      { id: 'cev-l4', contractId: 'ct-lease', type: 'Amended', title: 'Pets permitted by amendment', description: 'One cat. 250 USD added to the deposit.', notes: null, occurredAt: '2026-04-03T09:15:00Z', createdByUserId: null, createdAtUtc: '2026-04-03T09:40:00Z' },
      { id: 'cev-l5', contractId: 'ct-lease', type: 'EmailSent', title: 'Emailed landlord about the rent increase', description: 'Asked for the CPI basis in writing and for the index date they are using.', notes: 'Chase on the 21st if no reply.', occurredAt: '2026-06-14T09:31:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-06-14T09:35:12Z' },
      { id: 'cev-l6', contractId: 'ct-lease', type: 'NoticeGiven', title: 'Two months notice served', description: 'Hand-delivered and emailed the same afternoon. The tenancy ends on 31 August.', notes: null, occurredAt: '2026-06-28T16:10:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-06-28T16:12:00Z' },
      { id: 'cev-l7', contractId: 'ct-lease', type: 'Other', title: 'Mid-term inspection', description: 'Walked the flat with the inventory clerk. Nothing flagged for deduction.', notes: null, occurredAt: '2026-08-05T13:00:00Z', createdByUserId: 'u-mira', createdAtUtc: '2026-08-05T18:02:00Z' },
    ],
    'ct-fiber': [
      { id: 'cev-f1', contractId: 'ct-fiber', type: 'Signed', title: 'Order confirmed online', description: null, notes: null, occurredAt: '2025-01-22T19:40:00Z', createdByUserId: 'u-jane', createdAtUtc: '2025-01-22T19:41:00Z' },
      { id: 'cev-f2', contractId: 'ct-fiber', type: 'PriceChanged', title: 'Monthly charge raised to $84', description: 'Annual CPI+3.9% uplift clause. No right to exit on the increase.', notes: null, occurredAt: '2026-02-01T00:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2026-02-04T08:12:00Z' },
      { id: 'cev-f3', contractId: 'ct-fiber', type: 'EmailSent', title: 'Complained about the February outage', description: 'Three days down. Asked for a pro-rata credit on the month.', notes: null, occurredAt: '2026-02-24T20:05:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-02-24T20:06:00Z' },
    ],
    'ct-solar': [
      { id: 'cev-s1', contractId: 'ct-solar', type: 'Signed', title: 'Twenty-year rooftop lease signed', description: null, notes: null, occurredAt: '2023-05-28T10:00:00Z', createdByUserId: 'u-jane', createdAtUtc: '2023-05-28T10:30:00Z' },
      /* Type `Terminated` on a contract whose status is derived elsewhere —
         §4.2: the event does not move the contract to Expired. */
      { id: 'cev-s2', contractId: 'ct-solar', type: 'Terminated', title: 'Lease transferred with the property sale', description: 'Novated to the buyer at completion. Kept on the record for reference.', notes: null, occurredAt: '2025-10-31T12:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2025-11-05T11:58:00Z' },
    ],
    // ct-employment deliberately has none — it drives the empty state.
  };

  Object.assign(H, {
    cevTypeInfo(key) {
      return D.contractEventTypeByKey[key]
        || { key, label: key || 'Other', icon: 'more_horiz', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)', unknown: true };
    },

    cevFor(contractId) {
      return (D.contractEventSeed[contractId] || []).slice();
    },

    /* "14 Jun 2026" / "09:31" — the log is date AND time (§4). */
    cevDate(iso) {
      if (!iso) return '—';
      return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
    },
    cevTime(iso) {
      if (!iso) return '';
      return new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' });
    },
    cevDateTime(iso) { return `${H.cevDate(iso)} · ${H.cevTime(iso)}`; },
    cevYear(iso) { return new Date(iso).getUTCFullYear(); },

    /* §7.3 — the read projection carries a display LABEL, resolved at the API
       edge, never the raw user id. A deleted author reads "Unknown user". */
    cevCreatedBy(userId) { return (userId && D.cevUsers[userId]) || 'Unknown user'; },

    /* A synthetic long log, for the "what does 200 entries look like" stage.
       Deterministic, so the page renders the same on every load. */
    cevLongLog(contractId, n) {
      const types = D.contractEventTypes.map(t => t.key);
      const titles = [
        'Called about the standing charge', 'Meter reading submitted', 'Emailed the account manager',
        'Tariff review letter received', 'Direct debit amount adjusted', 'Complaint escalated',
        'Annual statement filed', 'Price uplift notified', 'Renewal quote requested',
        'Service credit applied', 'Address details corrected', 'Paperless billing confirmed',
      ];
      const users = ['u-jane', 'u-sam', 'u-mira', null];
      const out = [];
      for (let i = 0; i < n; i++) {
        const t = new Date(Date.UTC(2026, 8, 12) - i * 36e5 * 41);
        const iso = t.toISOString().slice(0, 19) + 'Z';
        out.push({
          id: `cev-long-${i}`, contractId, type: types[(i * 5) % types.length],
          title: titles[(i * 7) % titles.length],
          description: i % 3 === 0 ? 'Logged from the call notes the same afternoon.' : null,
          notes: null,
          occurredAt: iso, createdByUserId: users[i % users.length], createdAtUtc: iso,
        });
      }
      return out;
    },
  });
})();
