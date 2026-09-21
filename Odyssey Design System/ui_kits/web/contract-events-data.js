/* contract-events-data.js — the ContractEvent registry, seed and helpers.
   Tracks "Contract Event Automation — Frontend (Draft v3)" / backend #154,
   which supersedes "Contract Events — Backend (Draft v6)" on one point.

   THE LOG HAS TWO HALVES NOW. Draft v6 said nothing here was system-generated
   ("Pausing, archiving or renewing a contract writes no event", §7.6 /
   Non-Goal 4). That is no longer true and the comment it sat in has been
   removed rather than softened: the application now records what IT does to a
   contract — pausing, resuming, marking ready, clearing a signed date,
   archiving, restoring, adding or removing a party — and the user writes the
   rest by hand. A row's half is its `source`:

     'user'   — hand-written through the dialog. No marker.
     'system' — recorded by the application. Carries the persistent
                "Recorded automatically" chip on the rail (frontend §4).

   `source` is READ-ONLY on this surface. It is absent from both write DTOs
   (#154 §7.4) and appears nowhere in the dialog — not as a control, not as a
   disabled one. A system row is otherwise an ordinary row: same `⋯` actions,
   same edit, same delete (Non-Goal 1), and editing it does NOT re-author it.

   Attribution is the PERSON who took the action, never a service account —
   pausing a contract stamps the user who paused it.

   Still deliberately absent: link columns. v5 removed `PrimaryContactId` /
   `SecondaryContactId` / `PrimaryAccountId` / `SecondaryAccountId` and
   everything they dragged with them (Non-Goal 5), so there is no blocker probe
   and no detach helper here.

   ORDINALS ARE A WIRE CONTRACT (§4) — persisted, and present in request and
   response bodies — so the enumValue column is fixed forever. READING ORDER IS
   THE REGISTRY'S, ORDINAL ORDER IS THE ENUM'S, AND THEY NOW DIFFER: `Other`
   keeps ordinal 8 but stays the LAST entry in the array, because
   `cevTypeInfo` falls back to the trailing entry for an ordinal this build
   does not know. The nine new members take 9–17. */
(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  /* ---- ContractEventSource — the wire values behind `?source=` -------------
     The endpoint serves the filter and the typed client exposes it; v1
     surfaces NO control for it (frontend Non-Goal 3). Kept here so the two
     halves have names, and so the deferred filter is a page change rather than
     a data change. */
  D.contractEventSources = [
    { key: 'User', label: 'Written by hand', enumValue: 0 },
    { key: 'System', label: 'Recorded automatically', enumValue: 1 },
  ];

  /* ---- ContractEventType (§4) ---------------------------------------------
     Reading order: the eight original members, the nine automation members,
     then `Other` LAST. `auto` is the clause the dialog splices into a system
     row's Title help text — "Recorded automatically when …" (frontend §6.9);
     it is not shown on the rail. */
  D.contractEventTypes = [
    { key: 'Signed',       label: 'Signed',              enumValue: 0,  icon: 'history_edu',          color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', auto: 'the contract was marked signed',        desc: 'The agreement was executed by the parties.' },
    { key: 'Amended',      label: 'Amended',             enumValue: 1,  icon: 'edit_document',        color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)',                                                 desc: 'A variation or addendum changed the terms.' },
    { key: 'Renewed',      label: 'Renewed',             enumValue: 2,  icon: 'autorenew',            color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)',                                                desc: 'The agreement was taken up for a further term.' },
    { key: 'Extended',     label: 'Extended',            enumValue: 3,  icon: 'more_time',            color: 'oklch(0.78 0.14 145)', soft: 'oklch(0.78 0.14 145 / 0.16)',                                                desc: 'The existing term was lengthened.' },
    { key: 'NoticeGiven',  label: 'Notice given',        enumValue: 4,  icon: 'campaign',             color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)',                                                 desc: 'Notice to end the agreement was served, by either side.' },
    { key: 'Terminated',   label: 'Terminated',          enumValue: 5,  icon: 'gavel',                color: 'oklch(0.72 0.15 25)',  soft: 'oklch(0.72 0.15 25 / 0.16)',                                                 desc: 'The agreement was brought to an end. This does not change the contract’s status.' },
    { key: 'PriceChanged', label: 'Price changed',       enumValue: 6,  icon: 'price_change',         color: 'oklch(0.76 0.14 320)', soft: 'oklch(0.76 0.14 320 / 0.16)', auto: 'the agreement was re-priced',           desc: 'What the agreement costs was renegotiated or re-set.' },
    { key: 'EmailSent',    label: 'Email sent',          enumValue: 7,  icon: 'outgoing_mail',        color: 'oklch(0.77 0.14 205)', soft: 'oklch(0.77 0.14 205 / 0.16)',                                                desc: 'Correspondence you sent about the agreement. Name the recipient in the title or description.' },
    /* The nine automation members. Ordinals 9–17 — after `Other`'s 8, which is
       exactly why reading order and ordinal order part company here. The verbs
       are the ones a user would use: Resumed, not Unpaused; Restored, not
       Unarchived. */
    { key: 'Paused',       label: 'Paused',              enumValue: 9,  icon: 'pause_circle',         color: 'oklch(0.79 0.12 70)',  soft: 'oklch(0.79 0.12 70 / 0.16)',  auto: 'the contract was paused',               desc: 'The contract was paused. Recording this by hand does not pause anything.' },
    { key: 'Unpaused',     label: 'Resumed',             enumValue: 10, icon: 'play_circle',          color: 'oklch(0.79 0.14 155)', soft: 'oklch(0.79 0.14 155 / 0.16)', auto: 'the contract was resumed',              desc: 'A paused contract was taken off pause.' },
    { key: 'Ready',        label: 'Marked ready',        enumValue: 11, icon: 'rule',                 color: 'oklch(0.76 0.13 260)', soft: 'oklch(0.76 0.13 260 / 0.16)', auto: 'the contract was marked ready',         desc: 'The contract was marked ready.' },
    { key: 'Unready',      label: 'Ready withdrawn',     enumValue: 12, icon: 'remove_done',          color: 'oklch(0.75 0.10 240)', soft: 'oklch(0.75 0.10 240 / 0.16)', auto: 'the ready mark was withdrawn',          desc: 'The ready mark was taken off the contract.' },
    { key: 'Unsigned',     label: 'Signed date cleared', enumValue: 13, icon: 'history_toggle_off',   color: 'oklch(0.74 0.10 285)', soft: 'oklch(0.74 0.10 285 / 0.16)', auto: 'the signed date was cleared',           desc: 'The contract’s signed date was removed.' },
    { key: 'Archived',     label: 'Archived',            enumValue: 14, icon: 'inventory_2',          color: 'oklch(0.75 0.06 250)', soft: 'oklch(0.75 0.06 250 / 0.16)', auto: 'the contract was archived',             desc: 'The contract was archived. Archival hides a contract; it does not lock it.' },
    { key: 'Unarchived',   label: 'Restored',            enumValue: 15, icon: 'unarchive',            color: 'oklch(0.78 0.12 185)', soft: 'oklch(0.78 0.12 185 / 0.16)', auto: 'the contract was restored',             desc: 'An archived contract was brought back.' },
    { key: 'PartyAdded',   label: 'Party added',         enumValue: 16, icon: 'person_add',           color: 'oklch(0.79 0.13 130)', soft: 'oklch(0.79 0.13 130 / 0.16)', auto: 'a party was added to the agreement',    desc: 'Someone joined the agreement.' },
    { key: 'PartyRemoved', label: 'Party removed',       enumValue: 17, icon: 'person_remove',        color: 'oklch(0.74 0.13 15)',  soft: 'oklch(0.74 0.13 15 / 0.16)',  auto: 'a party was removed from the agreement', desc: 'Someone left the agreement.' },
    /* LAST, and it must stay last — the unknown-ordinal fallback is positional. */
    { key: 'Other',        label: 'Other',               enumValue: 8,  icon: 'more_horiz',           color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)',                                                desc: 'The entity default — anything the named members do not cover. The title carries it.' },
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
     deleted — SET NULL on the column (§4), read back as "Unknown user". This
     applies to system rows identically: the person who paused the contract can
     be deleted like anyone else.

     The three free-text fields are seeded with their three distinct roles
     (§4.1): `title` is the short label, `description` is the account of what
     happened, `notes` is the user's own working note — which the timeline does
     not render, though every non-Guest user of the deployment can read it. A
     system row has a server-authored title and description and no notes.

     `source` is on every row. Rows marked 'system' are what the application
     recorded; they mix freely into the same chronology as the hand-written
     ones, which is the whole point. */
  D.contractEventSeed = {
    'ct-lease': [
      { id: 'cev-l1', contractId: 'ct-lease', source: 'user', type: 'Signed', title: 'Tenancy agreement signed', description: 'Both counterparts signed at the letting office and the deposit was protected the same day.', notes: null, occurredAt: '2025-08-27T14:05:00Z', createdByUserId: 'u-jane', createdAtUtc: '2025-08-27T16:20:00Z' },
      { id: 'cev-l2', contractId: 'ct-lease', source: 'user', type: 'EmailSent', title: 'Emailed landlord about the damp', description: 'Photos of the back bedroom attached. Asked for a contractor visit inside two weeks.', notes: 'Keep the photos — they are dated.', occurredAt: '2026-01-12T08:44:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-01-12T08:46:00Z' },
      { id: 'cev-l3', contractId: 'ct-lease', source: 'user', type: 'PriceChanged', title: 'Rent renegotiated to 2,250 USD', description: 'Agreed by phone with the letting agent, then confirmed in writing. A CPI basis was requested and refused; settled at 4.7%.', notes: 'Check last year’s letter before the next review.', occurredAt: '2026-02-19T11:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2026-02-19T11:32:00Z' },
      { id: 'cev-l4', contractId: 'ct-lease', source: 'user', type: 'Amended', title: 'Pets permitted by amendment', description: 'One cat. 250 USD added to the deposit.', notes: null, occurredAt: '2026-04-03T09:15:00Z', createdByUserId: null, createdAtUtc: '2026-04-03T09:40:00Z' },
      /* System — a party joining used to change the party tiles and leave the
         log silent. Server-authored title and description. */
      { id: 'cev-l5s', contractId: 'ct-lease', source: 'system', type: 'PartyAdded', title: 'Northgate Lettings added as a party', description: 'Added to the agreement as the managing agent.', notes: null, occurredAt: '2026-05-12T10:22:00Z', createdByUserId: 'u-sam', createdAtUtc: '2026-05-12T10:22:00Z' },
      { id: 'cev-l5', contractId: 'ct-lease', source: 'user', type: 'EmailSent', title: 'Emailed landlord about the rent increase', description: 'Asked for the CPI basis in writing and for the index date they are using.', notes: 'Chase on the 21st if no reply.', occurredAt: '2026-06-14T09:31:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-06-14T09:35:12Z' },
      { id: 'cev-l6', contractId: 'ct-lease', source: 'user', type: 'NoticeGiven', title: 'Two months notice served', description: 'Hand-delivered and emailed the same afternoon. The tenancy ends on 31 August.', notes: null, occurredAt: '2026-06-28T16:10:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-06-28T16:12:00Z' },
      { id: 'cev-l7', contractId: 'ct-lease', source: 'user', type: 'Other', title: 'Mid-term inspection', description: 'Walked the flat with the inventory clerk. Nothing flagged for deduction.', notes: null, occurredAt: '2026-08-05T13:00:00Z', createdByUserId: 'u-mira', createdAtUtc: '2026-08-05T18:02:00Z' },
      /* System, and the newest row on the rail — the "when did we pause this?"
         question the frontend spec opens with. Edited by hand afterwards: the
         chip stays, because editing does not re-author a row (#154 §4). */
      { id: 'cev-l8s', contractId: 'ct-lease', source: 'system', type: 'Paused', title: 'Contract paused', description: 'Paused while the damp claim is open. The agreement itself is unchanged.', notes: null, occurredAt: '2026-09-02T15:41:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-09-02T15:41:00Z' },
    ],
    'ct-fiber': [
      { id: 'cev-f1', contractId: 'ct-fiber', source: 'user', type: 'Signed', title: 'Order confirmed online', description: null, notes: null, occurredAt: '2025-01-22T19:40:00Z', createdByUserId: 'u-jane', createdAtUtc: '2025-01-22T19:41:00Z' },
      { id: 'cev-f2', contractId: 'ct-fiber', source: 'user', type: 'PriceChanged', title: 'Monthly charge raised to $84', description: 'Annual CPI+3.9% uplift clause. No right to exit on the increase.', notes: null, occurredAt: '2026-02-01T00:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2026-02-04T08:12:00Z' },
      { id: 'cev-f3', contractId: 'ct-fiber', source: 'user', type: 'EmailSent', title: 'Complained about the February outage', description: 'Three days down. Asked for a pro-rata credit on the month.', notes: null, occurredAt: '2026-02-24T20:05:00Z', createdByUserId: 'u-jane', createdAtUtc: '2026-02-24T20:06:00Z' },
      { id: 'cev-f4s', contractId: 'ct-fiber', source: 'system', type: 'Ready', title: 'Marked ready', description: 'All required details are present, so the contract was marked ready.', notes: null, occurredAt: '2026-03-04T11:09:00Z', createdByUserId: 'u-mira', createdAtUtc: '2026-03-04T11:09:00Z' },
      /* An author since deleted, on a system row — reads "Unknown user", drawn
         quieter by .cev-by-unknown, exactly as a hand-written row would. */
      { id: 'cev-f5s', contractId: 'ct-fiber', source: 'system', type: 'Unready', title: 'Ready withdrawn', description: 'The ready mark was taken off the contract.', notes: null, occurredAt: '2026-05-19T08:30:00Z', createdByUserId: null, createdAtUtc: '2026-05-19T08:30:00Z' },
    ],
    'ct-solar': [
      { id: 'cev-s1', contractId: 'ct-solar', source: 'user', type: 'Signed', title: 'Twenty-year rooftop lease signed', description: null, notes: null, occurredAt: '2023-05-28T10:00:00Z', createdByUserId: 'u-jane', createdAtUtc: '2023-05-28T10:30:00Z' },
      /* Type `Terminated` on a contract whose status is derived elsewhere —
         §4.2: the event does not move the contract to Expired. */
      { id: 'cev-s2', contractId: 'ct-solar', source: 'user', type: 'Terminated', title: 'Lease transferred with the property sale', description: 'Novated to the buyer at completion. Kept on the record for reference.', notes: null, occurredAt: '2025-10-31T12:00:00Z', createdByUserId: 'u-sam', createdAtUtc: '2025-11-05T11:58:00Z' },
      { id: 'cev-s3s', contractId: 'ct-solar', source: 'system', type: 'PartyRemoved', title: 'Helen Voss removed as a party', description: 'Removed from the agreement at completion.', notes: null, occurredAt: '2025-10-31T12:04:00Z', createdByUserId: 'u-sam', createdAtUtc: '2025-10-31T12:04:00Z' },
      { id: 'cev-s4s', contractId: 'ct-solar', source: 'system', type: 'Archived', title: 'Contract archived', description: 'Archived after the property sale completed.', notes: null, occurredAt: '2025-11-06T09:15:00Z', createdByUserId: 'u-sam', createdAtUtc: '2025-11-06T09:15:00Z' },
    ],
    // ct-employment deliberately has none — it drives the empty state.
  };

  Object.assign(H, {
    cevTypeInfo(key) {
      return D.contractEventTypeByKey[key]
        || { key, label: key || 'Other', icon: 'more_horiz', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)', unknown: true };
    },

    /* The one predicate the rail branches on. A row with no `source` at all —
       an older payload — reads as hand-written, which is the safe default: it
       withholds a claim rather than asserting a false one. */
    cevIsSystem(ev) { return !!ev && ev.source === 'system'; },

    /* The clause the dialog splices into a system row's Title help text
       (frontend §6.9). Falls back to a type-free phrasing, so a member this
       build does not recognise still gets an honest sentence. */
    cevAutoClause(typeKey) {
      const info = D.contractEventTypeByKey[typeKey];
      return (info && info.auto) || 'the application made this change';
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

    /* A synthetic long log, for the "what does 200 entries look like" stage —
       and now also for the question automation raises: what does a rail look
       like when a third of its rows are chipped? Deterministic, so the page
       renders the same on every load. */
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
        const type = types[(i * 5) % types.length];
        const info = D.contractEventTypeByKey[type];
        // Only a type automation can actually produce is ever marked system.
        const system = !!(info && info.auto) && i % 3 === 0;
        out.push({
          id: `cev-long-${i}`, contractId, type,
          source: system ? 'system' : 'user',
          title: system ? info.label : titles[(i * 7) % titles.length],
          description: system ? info.desc : (i % 3 === 0 ? 'Logged from the call notes the same afternoon.' : null),
          notes: null,
          occurredAt: iso, createdByUserId: users[i % users.length], createdAtUtc: iso,
        });
      }
      return out;
    },
  });
})();
