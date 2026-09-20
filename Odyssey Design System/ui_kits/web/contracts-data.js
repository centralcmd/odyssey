/* Seed data + helpers for the Contracts feature (Contracts.jsx).
   ----------------------------------------------------------------------------
   Shapes mirror the spec's Odyssey.Finance.Context entities (Draft v4):
     • Contract       { name, type, description?, startDate?, endDate?,
                        completionDate?, paused?, archived?, createdAtUtc,
                        parties[], files[] }
                        — a contract is either TERM-based (startDate/endDate, either
                        optional) or ONE-OFF (a single completionDate, no term).
     • ContractParty  { id, accountId? | contactId?, role, fromDate?, toDate? }
                        — exactly one target (the XOR invariant, §6). The party
                        kind label for a contact target is "Contact".
                        `role` is a ContractPartyRole key ('Unspecified' is the
                        default and the backfill value); `fromDate`/`toDate` are
                        the party's TERM IN THE ROLE — both null is the DEFAULT
                        term (the contract's own extent), not an unset value,
                        exactly as an insurance party's term reads.
     • ContractFile   { id, fileMetadataId, fileType, attachedByUserId,
                        attachedAtUtc } — a REFERENCE to an existing FileMetadata
                        record (rendered with the FilesTable shape
                        { id, name, kind, size, uploaded }, `kind` = a
                        ContractFileType key).

   Status (Draft | Ready | Upcoming | Active | Expired | Paused | Archived) is
   DERIVED, never stored — computed here from StartDate / EndDate / Archived /
   Paused / Ready / Signed against one request "today".
   READY and SIGNED are the signature stamps: nullable UTC timestamps in the
   same shape as Paused and Archived, whose PRESENCE is the state and whose
   value is the moment. An unsigned contract (Signed null) reads Ready when the
   Ready stamp is present and Draft otherwise, and that layer sits ABOVE the
   whole date chain — a term nobody has agreed to is not Upcoming, and a
   negotiation that stalled is abandoned, not Expired. Draft and Ready
   contracts are on file but not in force: they are counted by type, and
   excluded from the run rate and the upcoming charges. Paused is a nullable UTC stamp recording
   WHEN the suspension began; it REPLACES Active in the derivation and nothing
   else, so a terminal status always wins over it. A paused contract stays
   visible, editable and fully priced on file — it simply stops counting toward
   the run rate and the upcoming charges. The registries (contractTypes / contractFileTypes) live
   here alongside Insurance's; the page reads everything off OdysseyData /
   OdysseyHelpers like every other feature. */

(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  // The configurable "ending soon" window — a contract Active today whose
  // EndDate falls within this many days reads as ending soon on the card.
  D.CONTRACTS_ENDING_WINDOW_DAYS = 45;

  /* ---- Canonical ContractType registry — label · icon · color, the same
     categorical band (L ~0.74–0.80, C ~0.13–0.16) as accountTypes /
     contactTypes / the file-type registries. `Other` (the default) last. */
  D.contractTypes = [
    { key: 'Employment', label: 'Employment', enumValue: 0, icon: 'work',                color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)', desc: 'An employment agreement — offer letter, contract of employment.' },
    { key: 'Service',    label: 'Service',    enumValue: 1, icon: 'home_repair_service', color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)', desc: 'A service or subscription agreement — utilities, telecoms, memberships.' },
    { key: 'Rental',     label: 'Rental',     enumValue: 2, icon: 'cottage',             color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)',  desc: 'A tenancy or lease — residential, parking, or storage.' },
    { key: 'Insurance',  label: 'Insurance',  enumValue: 4, icon: 'shield',              color: 'oklch(0.75 0.14 290)', soft: 'oklch(0.75 0.14 290 / 0.16)', desc: 'A policy held as an agreement — the contract of insurance itself.' },
    { key: 'Subscription', label: 'Subscription', enumValue: 5, icon: 'autorenew',       color: 'oklch(0.76 0.14 320)', soft: 'oklch(0.76 0.14 320 / 0.16)', desc: 'A recurring supply agreement — software, media, delivery.' },
    { key: 'Purchase',   label: 'Purchase',   enumValue: 6, icon: 'shopping_bag',        color: 'oklch(0.78 0.14 140)', soft: 'oklch(0.78 0.14 140 / 0.16)', desc: 'A one-off acquisition recorded by its completion date.' },
    { key: 'Membership', label: 'Membership', enumValue: 7, icon: 'card_membership',     color: 'oklch(0.77 0.13 20)',  soft: 'oklch(0.77 0.13 20 / 0.16)',  desc: 'A club, gym, union, or association membership.' },
    { key: 'Other',      label: 'Other',      enumValue: 3, icon: 'description',         color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The entity default — anything outside the categories above.' },
  ];

  /* ---- Canonical ContractFileType registry — the documents that attach to a
     contract. Enum order with `Other` (the default) pulled last. */
  D.contractFileTypes = [
    { key: 'Signed',         label: 'Signed',         enumValue: 0, icon: 'history_edu',       color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'The executed, signed agreement — the document of record.' },
    { key: 'Amendment',      label: 'Amendment',      enumValue: 1, icon: 'edit_document',     color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)',  desc: 'An addendum, variation, or amendment to the signed contract.' },
    { key: 'Correspondence', label: 'Correspondence', enumValue: 2, icon: 'forum',             color: 'oklch(0.77 0.14 205)', soft: 'oklch(0.77 0.14 205 / 0.16)', desc: 'Letters, notices, or email relating to the agreement.' },
    { key: 'Other',          label: 'Other',          enumValue: 3, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The enum default — anything outside the categories above.' },
  ];

  /* ---- Canonical ContractPartyRole registry (Draft v4 §4) — what a linked
     record DOES in the agreement, orthogonal to its kind (an account party may
     carry any role; no role–type matrix in v1). ORDINALS ARE A WIRE AND
     PERSISTENCE CONTRACT: later members append, none is renumbered.
     `Unspecified` (0) and `Other` (6) are deliberately distinct — "nobody has
     said" versus "somebody looked and none of these fit" — and this kit never
     conflates them. Colours sit in the same categorical band as the other
     registries; `Unspecified` stays neutral so an unstated role never reads as
     a category. */
  D.contractPartyRoles = [
    { key: 'Unspecified',     label: 'Unspecified',      enumValue: 0, icon: 'help_outline',         color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.14)', desc: 'No role stated — the default, and what every pre-existing party reads as.' },
    { key: 'Employee',        label: 'Employee',         enumValue: 1, icon: 'badge',                color: 'oklch(0.76 0.13 265)', soft: 'oklch(0.76 0.13 265 / 0.16)', desc: 'The person employed under this agreement.' },
    { key: 'Employer',        label: 'Employer',         enumValue: 2, icon: 'corporate_fare',       color: 'oklch(0.75 0.14 300)', soft: 'oklch(0.75 0.14 300 / 0.16)', desc: 'The party that employs.' },
    { key: 'Buyer',           label: 'Buyer',            enumValue: 3, icon: 'shopping_bag',         color: 'oklch(0.79 0.14 145)', soft: 'oklch(0.79 0.14 145 / 0.16)', desc: 'The party acquiring under this agreement.' },
    { key: 'Seller',          label: 'Seller',           enumValue: 4, icon: 'sell',                 color: 'oklch(0.80 0.13 90)',  soft: 'oklch(0.80 0.13 90 / 0.16)',  desc: 'The party disposing under this agreement.' },
    { key: 'ServiceProvider', label: 'Service provider', enumValue: 5, icon: 'home_repair_service',  color: 'oklch(0.78 0.14 195)', soft: 'oklch(0.78 0.14 195 / 0.16)', desc: 'The party delivering the service.' },
    { key: 'Other',           label: 'Other',            enumValue: 6, icon: 'more_horiz',           color: 'oklch(0.77 0.10 25)',  soft: 'oklch(0.77 0.10 25 / 0.16)',  desc: 'A deliberate role that is none of the above — not the same as Unspecified.' },
  ];

  /* ---- The file library (the user's files.read-visible FileMetadata records).
     The attach picker (§3/B2) is fed these as PRE-LOADED Combobox options; a
     ContractFile references one by id. Shape is FileMetadata-like; rendered
     through FilesTable with { id, name, kind, size, uploaded }. ---- */
  D.contractFileLibrary = [
    { id: 'fm-emp-offer',   name: 'acme_offer_letter_signed.pdf',     contentType: 'application/pdf', size: '214 KB', uploaded: '2024-02-20' },
    { id: 'fm-emp-handbook',name: 'employee_handbook_v6.pdf',         contentType: 'application/pdf', size: '1.8 MB',  uploaded: '2024-02-20' },
    { id: 'fm-lease-signed',name: 'maple_st_lease_2025.pdf',          contentType: 'application/pdf', size: '402 KB', uploaded: '2025-08-14' },
    { id: 'fm-lease-amend',  name: 'lease_amendment_pets.pdf',         contentType: 'application/pdf', size: '96 KB',  uploaded: '2026-01-08' },
    { id: 'fm-lease-letter', name: 'rent_review_notice_2026.pdf',      contentType: 'application/pdf', size: '54 KB',  uploaded: '2026-05-30' },
    { id: 'fm-fiber-signed', name: 'fiber_service_agreement.pdf',      contentType: 'application/pdf', size: '320 KB', uploaded: '2025-01-22' },
    { id: 'fm-gym-signed',   name: 'fitzone_membership_terms.pdf',     contentType: 'application/pdf', size: '180 KB', uploaded: '2026-06-10' },
    { id: 'fm-storage-signed',name: 'storage_unit_b12_contract.pdf',   contentType: 'application/pdf', size: '142 KB', uploaded: '2024-01-03' },
    { id: 'fm-solar-signed', name: 'solar_lease_agreement.pdf',        contentType: 'application/pdf', size: '512 KB', uploaded: '2023-05-28' },
    { id: 'fm-solar-corr',   name: 'solar_transfer_correspondence.pdf',contentType: 'application/pdf', size: '70 KB',  uploaded: '2025-11-02' },
    { id: 'fm-misc-1',       name: 'broadband_speed_report.pdf',       contentType: 'application/pdf', size: '38 KB',  uploaded: '2026-03-15' },
    { id: 'fm-misc-2',       name: 'id_verification_scan.jpg',         contentType: 'image/jpeg',      size: '1.1 MB', uploaded: '2025-08-14' },
    { id: 'fm-parking-signed', name: 'harbor_point_parking_licence.pdf', contentType: 'application/pdf', size: '88 KB', uploaded: '2025-10-20' },
    { id: 'fm-energy-signed', name: 'northwind_fixed_tariff_2026.pdf', contentType: 'application/pdf', size: '210 KB', uploaded: '2026-09-02' },
  ];

  /* ---- Seed contracts. Dates anchored around mid-2026 so the derived statuses
     are stable: covers all four types and both party kinds (Account /
     Contact), plus one Upcoming, one Expired, and one
     Archived record. ---- */
  D.contracts = [
    {
      id: 'ct-employment', name: 'ACME Co — Employment', type: 'Employment',
      description: 'Permanent, full-time. Salary paid monthly into the Chase Checking account. 3-month notice either side.',
      startDate: '2024-03-01', endDate: null, ready: '2024-02-20T09:00:00Z', signed: '2024-02-24T09:00:00Z', paused: null, archived: null, createdAtUtc: '2024-02-20T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-emp-1', contactId: 'c2', role: 'Employer', fromDate: null, toDate: null },
        // The salary account is a party to the agreement with no role in the
        // v1 vocabulary — Unspecified, not Other: nobody has stated one.
        { id: 'cp-emp-2', accountId: '1', role: 'Unspecified', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-emp-1', fileMetadataId: 'fm-emp-offer', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2024-02-20T09:05:00Z' },
        { id: 'cf-emp-2', fileMetadataId: 'fm-emp-handbook', kind: 'Other', attachedByUserId: 'u-owner', attachedAtUtc: '2024-02-20T09:06:00Z' },
      ],
    },
    {
      id: 'ct-lease', name: 'Maple St Residence — Lease', type: 'Rental',
      description: 'Twelve-month assured shorthold tenancy on the Maple St residence. Rent due on the 1st. Pets permitted by amendment.',
      startDate: '2025-09-01', endDate: '2026-08-31', ready: '2025-08-14T09:00:00Z', signed: '2025-08-20T09:00:00Z', paused: null, archived: null, createdAtUtc: '2025-08-14T10:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-lease-1', accountId: '7', role: 'Unspecified', fromDate: null, toDate: null },
        // A party that joined partway through the term — the case the term
        // exists for. Landlord/tenant are not in the v1 vocabulary, so this is
        // a deliberate Other, not an unstated role.
        { id: 'cp-lease-2', contactId: 'c9', role: 'Other', fromDate: '2026-02-01', toDate: null },
      ],
      files: [
        { id: 'cf-lease-1', fileMetadataId: 'fm-lease-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2025-08-14T10:02:00Z' },
        { id: 'cf-lease-2', fileMetadataId: 'fm-lease-amend', kind: 'Amendment', attachedByUserId: 'u-owner', attachedAtUtc: '2026-01-08T14:00:00Z' },
        { id: 'cf-lease-3', fileMetadataId: 'fm-lease-letter', kind: 'Correspondence', attachedByUserId: 'u-owner', attachedAtUtc: '2026-05-30T11:00:00Z' },
      ],
    },
    {
      id: 'ct-house', name: 'Maple St Residence — Purchase', type: 'Purchase',
      description: 'Purchase of the Maple St property — a one-off agreement recorded by its completion (closing) date, not a term. Kept as the deed of record for the property.',
      startDate: null, endDate: null, completionDate: '2021-04-15', ready: '2021-03-02T09:00:00Z', signed: '2021-03-30T09:00:00Z', paused: null, archived: null, createdAtUtc: '2021-03-02T09:00:00Z', createdByUserId: null,
      parties: [
        { id: 'cp-house-1', accountId: '7', role: 'Buyer', fromDate: null, toDate: null },
        { id: 'cp-house-2', contactId: 'c9', role: 'Seller', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-house-1', fileMetadataId: 'fm-house-deed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2021-04-15T12:00:00Z' },
      ],
    },
    {
      id: 'ct-fiber', name: 'Fiber Internet — 24 Month', type: 'Service',
      description: 'Symmetric 1 Gbps fiber. 24-month term, early-termination fee applies. Auto-renews monthly at term end.',
      startDate: '2025-02-01', endDate: '2027-01-31', ready: '2025-01-22T09:00:00Z', signed: '2025-01-24T09:00:00Z', paused: null, archived: null, createdAtUtc: '2025-01-22T09:00:00Z', createdByUserId: 'u-sam',
      parties: [
        { id: 'cp-fiber-1', contactId: 'c3', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-fiber-1', fileMetadataId: 'fm-fiber-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2025-01-22T09:03:00Z' },
        { id: 'cf-fiber-2', fileMetadataId: 'fm-misc-1', kind: 'Correspondence', attachedByUserId: 'u-owner', attachedAtUtc: '2026-03-15T09:00:00Z' },
      ],
    },
    {
      id: 'ct-gym', name: 'FitZone — Membership', type: 'Membership',
      description: 'Annual gym membership. Direct debit, monthly. Frozen over the winter — resuming in the spring.',
      startDate: '2026-09-01', endDate: '2027-08-31', ready: '2026-06-10T09:00:00Z', signed: '2026-06-12T09:00:00Z', paused: '2026-09-14T10:30:00Z', archived: null, createdAtUtc: '2026-06-10T09:00:00Z', createdByUserId: 'u-mira',
      parties: [
        { id: 'cp-gym-1', contactId: 'c11', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-gym-1', fileMetadataId: 'fm-gym-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2026-06-10T09:02:00Z' },
      ],
    },
    {
      // The ending-soon case: Active today, term inside the 45-day window, so it
      // populates the header signal's warning group beside the next charges.
      id: 'ct-parking', name: 'Harbor Point Parking — Space 14', type: 'Rental',
      description: 'Twelve-month parking licence on space 14. Renews only by a fresh agreement — give notice 30 days before the end date.',
      startDate: '2025-11-01', endDate: '2026-10-31', ready: '2025-10-20T09:00:00Z', signed: '2025-10-22T09:00:00Z', paused: null, archived: null, createdAtUtc: '2025-10-20T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-parking-1', contactId: 'c8', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-parking-1', fileMetadataId: 'fm-parking-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2025-10-20T09:02:00Z' },
      ],
    },
    {
      // Signed but not yet begun — the Upcoming case, inside the window, so it
      // populates the header signal's "Starting soon" group.
      id: 'ct-energy', name: 'Northwind Energy — Fixed Tariff', type: 'Service',
      description: 'Twelve-month fixed electricity tariff. Switch completes on the start date; the standing charge and unit rate are fixed for the term.',
      startDate: '2026-10-15', endDate: '2027-10-14', ready: '2026-09-02T09:00:00Z', signed: '2026-09-04T09:00:00Z', paused: null, archived: null, createdAtUtc: '2026-09-02T09:00:00Z', createdByUserId: 'u-sam',
      parties: [
        { id: 'cp-energy-1', contactId: 'c3', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-energy-1', fileMetadataId: 'fm-energy-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2026-09-02T09:04:00Z' },
      ],
    },
    {
      id: 'ct-storage', name: 'Storage Unit B12 — Rental', type: 'Rental',
      description: 'Self-storage unit, 50 sq ft. Twelve-month term, not renewed — kept for record.',
      startDate: '2024-01-01', endDate: '2025-12-31', ready: '2024-01-03T09:00:00Z', signed: '2024-01-03T09:00:00Z', paused: null, archived: null, createdAtUtc: '2024-01-03T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        // Left the role when the unit was handed back, while the contract row
        // stays on record — a closed term, rendered as a past party.
        { id: 'cp-storage-1', contactId: 'c8', role: 'ServiceProvider', fromDate: null, toDate: '2025-12-31' },
      ],
      files: [
        { id: 'cf-storage-1', fileMetadataId: 'fm-storage-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2024-01-03T09:01:00Z' },
      ],
    },
    {
      id: 'ct-solar', name: 'Solar Panel Lease', type: 'Other',
      description: 'Twenty-year rooftop solar lease — transferred to the new owner on sale of the property. Retained for reference.',
      startDate: '2023-06-01', endDate: '2025-10-31', ready: '2023-05-28T09:00:00Z', signed: '2023-05-30T09:00:00Z', paused: null, archived: '2025-11-05T12:00:00Z', createdAtUtc: '2023-05-28T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-solar-1', accountId: '7', role: 'Other', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-solar-1', fileMetadataId: 'fm-solar-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2023-05-28T09:04:00Z' },
        { id: 'cf-solar-2', fileMetadataId: 'fm-solar-corr', kind: 'Correspondence', attachedByUserId: 'u-owner', attachedAtUtc: '2025-11-02T16:00:00Z' },
      ],
    },
    /* DRAFT — recorded while it is still being negotiated. Note the start date
       is in the future and the status is still Draft, not Upcoming: the dates
       describe a term nobody has agreed to. It carries a priced Fee term and
       contributes nothing to the run rate or the upcoming charges. */
    {
      id: 'ct-cleaning', name: 'Beacon Home Services — Cleaning', type: 'Service',
      description: 'Fortnightly whole-house clean. Quote received; terms still under discussion — nothing has been marked ready for signature yet.',
      startDate: '2026-11-01', endDate: '2027-10-31', ready: null, signed: null, paused: null, archived: null, createdAtUtc: '2026-09-12T11:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-cleaning-1', contactId: 'c3', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [],
    },
    /* READY — marked ready for signature and never signed, with a term that
       has since run out. It reads Ready, NOT Expired: a term cannot lapse
       before it begins, and the thing to act on is an abandoned negotiation,
       not a retired agreement. Archivable under the widened rule. */
    {
      id: 'ct-tutoring', name: 'Westbrook Tutoring — Weekly Sessions', type: 'Service',
      description: 'Weekly maths tuition over the school year. Sent for signature in August 2025 and never returned — the term it describes has since run out.',
      startDate: '2025-09-01', endDate: '2026-06-30', ready: '2025-08-20T15:30:00Z', signed: null, paused: null, archived: null, createdAtUtc: '2025-08-18T09:00:00Z', createdByUserId: 'u-mira',
      parties: [
        { id: 'cp-tutoring-1', contactId: 'c8', role: 'ServiceProvider', fromDate: null, toDate: null },
      ],
      files: [],
    },
  ];

  // ---- Lookups + helpers -----------------------------------------------------
  D.contractTypeByKey = Object.fromEntries(D.contractTypes.map(t => [t.key, t]));
  D.contractFileTypeByKey = Object.fromEntries(D.contractFileTypes.map(t => [t.key, t]));
  D.contractFileById = Object.fromEntries(D.contractFileLibrary.map(f => [f.id, f]));
  D.contractPartyRoleByKey = Object.fromEntries(D.contractPartyRoles.map(r => [r.key, r]));

  Object.assign(H, {
    contractTypeInfo(key) {
      return D.contractTypeByKey[key]
        || { key, label: key || 'Other', icon: 'description', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
    },
    contractFileTypeInfo(key) {
      return D.contractFileTypeByKey[key]
        || { key, label: key || 'Other', icon: 'insert_drive_file', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
    },

    // The request's UTC "today" as 'YYYY-MM-DD' (a single value per call site).
    conToday() { return new Date().toISOString().slice(0, 10); },
    conDateOnly(iso) { return iso ? String(iso).slice(0, 10) : null; },

    // Whole-day difference dateIso − today (negative = already past).
    conDaysUntil(dateIso, today) {
      if (!dateIso) return null;
      const a = new Date(String(dateIso).slice(0, 10) + 'T00:00:00Z');
      const b = new Date((today || H.conToday()) + 'T00:00:00Z');
      return Math.round((a - b) / 86400000);
    },

    // The base derivation (pause-blind), evaluated in the fixed order:
    //   Archived → one-off completion (Upcoming before / Active on-or-after) →
    //   Upcoming (start in future) → Expired (end in past) → Active.
    conBaseStatus(contract, today) {
      const t = today || H.conToday();
      if (contract.archived) return 'Archived';
      // One-off (point-in-time) contract — a single completion date, no term:
      // pending completion reads Upcoming, on/after the date reads Active. A
      // one-off never Expires (a purchase is fulfilled, not expired).
      if (contract.completionDate) {
        return H.conDateOnly(contract.completionDate) > t ? 'Upcoming' : 'Active';
      }
      const start = H.conDateOnly(contract.startDate);
      const end = H.conDateOnly(contract.endDate);
      if (start && start > t) return 'Upcoming';
      if (end && end < t) return 'Expired';
      return 'Active';
    },

    /* The SIGNATURE layer, between the archive check and the date chain.
       Signed present ⇒ null (the date chain runs). Signed null ⇒ Ready when
       the Ready stamp is present, Draft otherwise — and it short-circuits the
       date chain entirely, which is the whole point: an unsigned contract
       whose start date is in the future is not Upcoming (that would assert a
       commitment nobody has made), and one whose end date has passed is not
       Expired (a term cannot lapse before it begins). */
    conSignatureStatus(contract) {
      if (contract.signed) return null;
      return contract.ready ? 'Ready' : 'Draft';
    },

    /* True for the two states that are on file but not in force. The SINGLE
       gate the money roll-ups read — never a re-test of the stamps. */
    conIsUnsigned(status) { return status === 'Draft' || status === 'Ready'; },

    /* Derived status. Paused REPLACES Active and nothing else — it is applied
       ONCE to the RESULT of the base derivation, never inserted as a step in
       its chain. The one-off branch above returns early for BOTH its outcomes,
       so a pause check written late in that chain would be unreachable for a
       settled one-off: the stamp would be stored and every read would keep
       saying Active while the contract kept costing money.
       Read as precedence:
       Archived > Draft/Ready > Upcoming > Expired > Paused > Active —
       a terminal fact outranks a temporary one, and an agreement nobody has
       signed outranks everything its dates would otherwise say. */
    conStatus(contract, today) {
      if (contract.archived) return 'Archived';
      const sig = H.conSignatureStatus(contract);
      if (sig) return sig;
      const status = H.conBaseStatus(contract, today);
      return status === 'Active' && contract.paused ? 'Paused' : status;
    },

    /* Lifecycle READING order — what a Status sort uses, and the order every
       status list on the page is written in. NOT the wire ordinal: Draft and
       Ready are appended enum members (5, 6), so sorting on the ordinal would
       put the two earliest states last, behind Archived. */
    CON_STATUS_RANK: ['Draft', 'Ready', 'Upcoming', 'Active', 'Paused', 'Expired', 'Archived'],
    conStatusRank(key) {
      const i = H.CON_STATUS_RANK.indexOf(key);
      return i < 0 ? H.CON_STATUS_RANK.length : i;
    },

    /* The three signature guards, shared by create and edit exactly as the
       server shares them between POST and PUT. Returns { field, code, message }
       or null. Clearing a stamp is NEVER refused — a guard on the way out is
       how a row gets stranded. */
    conSignatureError(draft, today) {
      const t = today || H.conToday();
      const ready = H.conDateOnly(draft.ready);
      const signed = H.conDateOnly(draft.signed);
      if (ready && ready > t) {
        return { field: 'ready', code: 'contract_signature_date_in_future',
          message: 'Unable to save. A ready date records something that has happened — it cannot be in the future.' };
      }
      if (signed && signed > t) {
        return { field: 'signed', code: 'contract_signature_date_in_future',
          message: 'Unable to save. A signed date records something that has happened — it cannot be in the future.' };
      }
      if (signed && !ready) {
        return { field: 'signed', code: 'contract_signed_requires_ready',
          message: 'Unable to save. A signed contract needs a ready date too — set when it was ready for signature, or clear the signed date.' };
      }
      if (signed && ready && String(draft.signed) < String(draft.ready)) {
        return { field: 'signed', code: 'contract_signed_before_ready',
          message: 'Unable to save. A contract cannot be signed before it was ready for signature.' };
      }
      return null;
    },

    /* Status display vocabulary: label, chip tone, status dot, and a glyph.
       Draft=muted/outline (on file, nothing agreed) · Ready=amber/pending
       (waiting on a signature, the same tone a pause gets) ·
       Active=mint/income · Upcoming=sea/info · Expired=coral/expense ·
       Paused=amber/pending (the tone Subscriptions already gives a pause) ·
       Archived=muted/outline. Tones map to the same finance accents Insurance /
       Accounts use — no new status hue enters.
       An UNKNOWN member falls back NEUTRAL, under its own name. ContractStatus
       appends server-side, so a client older than the deployment can be handed
       a member it has never heard of; resolving that to Active would report a
       WRONG state (a green pill on a paused contract), not a degraded one. */
    conStatusMeta(key) {
      const map = {
        Draft:    { key: 'Draft',    label: 'Draft',    tone: 'outline', dot: true,  icon: 'edit_note' },
        Ready:    { key: 'Ready',    label: 'Ready',    tone: 'pending', dot: true,  icon: 'draw' },
        Active:   { key: 'Active',   label: 'Active',   tone: 'income',  dot: true,  icon: 'task_alt' },
        Upcoming: { key: 'Upcoming', label: 'Upcoming', tone: 'info',    dot: true,  icon: 'schedule' },
        Expired:  { key: 'Expired',  label: 'Expired',  tone: 'expense', dot: true,  icon: 'event_busy' },
        Archived: { key: 'Archived', label: 'Archived', tone: 'outline', dot: true,  icon: 'inventory_2' },
        Paused:   { key: 'Paused',   label: 'Paused',   tone: 'pending', dot: true,  icon: 'pause_circle' },
      };
      return map[key] || { key: String(key), label: String(key), tone: 'outline', dot: true, icon: 'help', unknown: true };
    },

    // Resolve a party row to the minimal display projection (spec §10 #2) —
    // id + display name + type only, never the fuller cross-claim DTO. Returns
    // { kind, kindLabel, name, typeLabel, icon, color, soft, target }.
    /* A ContractPartyRole key → its registry row. An UNKNOWN key is a real
       runtime state, not a bug: ordinals append server-side, so a client older
       than the deployment can be handed a member it has never heard of. It is
       rendered honestly (neutral, named as unrecognised) rather than silently
       collapsed into Unspecified, which would read as "no role stated". */
    conPartyRoleInfo(key) {
      if (key == null || key === '') return D.contractPartyRoleByKey.Unspecified;
      return D.contractPartyRoleByKey[key]
        || { key, label: 'Unrecognised role', icon: 'help', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)',
             unknown: true, desc: 'This role was added after this app version — update to read it.' };
    },
    conPartyRoleOptions() {
      return D.contractPartyRoles.map(r => ({ value: r.key, label: r.label, icon: r.icon, iconColor: r.color, sub: r.desc }));
    },

    // Short date for the tile caption: 'YYYY-MM-DD' → "Feb 1 2026".
    conDateShort(iso) {
      if (!iso) return '';
      const d = new Date(String(iso).slice(0, 10) + 'T00:00:00');
      return isNaN(d) ? String(iso) : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }).replace(',', '');
    },

    /* The party's TERM IN THE ROLE, as a caption — null when it is the DEFAULT
       term (both dates null = the contract's own extent), which needs no
       caption. Kept short: it sits on one line above a fixed-height tile. */
    conPartyTermText(party) {
      const f = party.fromDate, t = party.toDate;
      if (f && t) return `${H.conDateShort(f)} – ${H.conDateShort(t)}`;
      if (f) return `from ${H.conDateShort(f)}`;
      if (t) return `to ${H.conDateShort(t)}`;
      return null;
    },
    // A party whose term has closed before today — still a party of record,
    // drawn quieter than one currently in its role.
    conPartyPast(party, today) {
      const t = H.conDateOnly(party.toDate);
      return !!t && t < (today || H.conToday());
    },

    conResolveParty(party) {
      if (party.accountId) {
        const a = D.accountById[party.accountId];
        const m = (a && D.accountTypeById[a.type]) || {};
        return { kind: 'account', kindLabel: 'Account', name: a ? a.name : 'Unknown account',
          typeLabel: m.label || '', icon: m.icon || 'account_balance_wallet', color: m.color, soft: m.soft, target: a };
      }
      if (party.contactId) {
        const c = D.contactById[party.contactId];
        const m = (c && D.contactTypeByKey[c.type]) || {};
        return { kind: 'contact', kindLabel: 'Contact', name: c ? c.name : 'Unknown contact',
          typeLabel: m.label || '', icon: m.icon || 'groups', color: m.color, soft: m.soft, target: c };
      }
      return { kind: 'unknown', kindLabel: 'Party', name: '—', typeLabel: '', icon: 'help', color: undefined, soft: undefined, target: null };
    },

    /* The duplicate rule, as the picker reads it: uniqueness is
       (contract, target, ROLE), so a record already linked in one role is
       still offerable in another. `exceptId` excludes the party being edited
       from its own check (the PUT's edit-row exclusion). */
    conPartyTaken(parties, field, role, exceptId) {
      const taken = new Set();
      (parties || []).forEach(p => {
        if (exceptId && p.id === exceptId) return;
        if ((p.role || 'Unspecified') !== role) return;
        if (p[field]) taken.add(p[field]);
      });
      return taken;
    },

    // The two selectable party-kind option sets (pre-loaded for the picker).
    conAccountOptions() {
      return D.accounts.filter(a => !a.archived).map(a => {
        const m = D.accountTypeById[a.type] || {};
        return { value: a.id, label: a.name, icon: m.icon, iconColor: m.color };
      });
    },
    conInstitutionOptions() {
      return D.activeContacts().map(c => {
        const m = D.contactTypeByKey[c.type] || {};
        return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color };
      });
    },

    // The user's file-library options (pre-loaded for the attach picker).
    conFileLibraryOptions() {
      return D.contractFileLibrary.map(f => ({ value: f.id, label: f.name, icon: 'description' }));
    },
    // Resolve a ContractFile (id reference + fileType) to the FilesTable row
    // shape { id, name, kind, size, uploaded } from the referenced FileMetadata.
    conFileRow(cf) {
      const meta = D.contractFileById[cf.fileMetadataId] || {};
      return { id: cf.id, fileMetadataId: cf.fileMetadataId, name: cf.name || meta.name || cf.fileMetadataId,
        kind: cf.kind, size: cf.size || meta.size || '—', uploaded: cf.uploaded || meta.uploaded || (cf.attachedAtUtc || '').slice(0, 10),
        contentType: cf.contentType || meta.contentType };
    },

    // Long date 'YYYY-MM-DD' → "Jan 1, 2026".
    conDate(iso) {
      if (!iso) return '—';
      const d = new Date(String(iso).slice(0, 10) + 'T00:00:00');
      return isNaN(d) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    },

    // The collapsed-card headline: the period anchor + a relative-days word,
    // by derived status. { value, word, cls }.
    conHeadline(contract, today, windowDays) {
      const t = today || H.conToday();
      const status = H.conStatus(contract, t);
      const win = windowDays != null ? windowDays : D.CONTRACTS_ENDING_WINDOW_DAYS;
      if (status === 'Archived') {
        const d = contract.completionDate || contract.endDate || contract.startDate;
        return { value: d ? H.conDate(d) : '—', word: 'archived', cls: 'archived' };
      }
      /* Unsigned: the term's dates describe something nobody has agreed to,
         so counting down to them would assert a commitment that does not
         exist. The headline says where the signature got to instead. */
      if (status === 'Draft') {
        return { value: H.conDate(contract.createdAtUtc), word: 'drafted', cls: 'archived' };
      }
      if (status === 'Ready') {
        return { value: H.conDate(contract.ready), word: 'ready', cls: 'soon' };
      }
      /* Paused: the countdown is meaningless while nothing is running, so the
         headline says when the pause began instead. Checked HERE, above the
         one-off branch, for the same reason the status derivation applies the
         member to the result: the one-off branch returns early, so a paused
         settled one-off would otherwise keep counting down. */
      if (status === 'Paused') {
        return { value: H.conDate(contract.paused), word: 'paused', cls: 'paused' };
      }
      // One-off (point-in-time): pending completion, or completed.
      if (contract.completionDate) {
        const days = H.conDaysUntil(contract.completionDate, t);
        if (days > 0) return { value: H.conDate(contract.completionDate), word: days === 1 ? 'completes tomorrow' : `completes in ${days} days`, cls: '' };
        return { value: H.conDate(contract.completionDate), word: days === 0 ? 'completes today' : 'completed', cls: '' };
      }
      if (status === 'Upcoming') {
        const days = H.conDaysUntil(contract.startDate, t);
        return { value: H.conDate(contract.startDate), word: days <= 0 ? 'starts today' : `starts in ${days} day${days === 1 ? '' : 's'}`, cls: '' };
      }
      if (status === 'Expired') {
        const days = -H.conDaysUntil(contract.endDate, t);
        return { value: H.conDate(contract.endDate), word: days <= 0 ? 'expired today' : `expired ${days} day${days === 1 ? '' : 's'} ago`, cls: 'expired' };
      }
      // Active (term)
      if (!contract.endDate) {
        return contract.startDate
          ? { value: 'Open-ended', word: 'no end date', cls: '' }
          : { value: '—', word: 'no dates', cls: '' };
      }
      const days = H.conDaysUntil(contract.endDate, t);
      const soon = days != null && days <= win;
      return { value: H.conDate(contract.endDate), word: days <= 0 ? 'ends today' : `ends in ${days} day${days === 1 ? '' : 's'}`, cls: soon ? 'soon' : '' };
    },

    // Non-archived contracts — the default (active) set.
    conActiveContracts(contracts) { return (contracts || D.contracts).filter(c => !c.archived); },

    // Page summary (spec §7 GET /summary): total + counts by status and type,
    // over the whole set (archived included) so the status filter can surface
    // archived records. The card list hides archived by default itself.
    conSummary(contracts, today) {
      const t = today || H.conToday();
      const all = contracts || D.contracts;
      // Seven mutually exclusive buckets that partition the set and sum to
      // total. EndingSoon stays a slice of Active and is not one of them.
      const counts = { Draft: 0, Ready: 0, Active: 0, Upcoming: 0, Expired: 0, Archived: 0, Paused: 0 };
      const byType = {};
      for (const c of all) {
        counts[H.conStatus(c, t)] = (counts[H.conStatus(c, t)] || 0) + 1;
        // By type is a HEADCOUNT of the agreements on file, not a cost split:
        // a draft is still a contract of its type, so it is counted here even
        // though it contributes nothing to the run rate.
        if (!c.archived) byType[c.type] = (byType[c.type] || 0) + 1;
      }
      return {
        total: all.length,
        active: all.filter(c => !c.archived).length,
        countsByStatus: counts,
        typeRows: D.contractTypes
          .map(t2 => ({ key: t2.key, label: t2.label, icon: t2.icon, color: t2.color, soft: t2.soft, count: byType[t2.key] || 0 }))
          .filter(r => r.count > 0),
      };
    },
  });

  /* ==========================================================================
     Contract TERMS — the price history of an agreement (backend Draft v2)
     --------------------------------------------------------------------------
     The SAME Term rows, table and domain rules as an account's terms: a series
     is (owner, TermKind, LabelKey), the latest entry on/before a date is the one
     in force, supersession is implicit. Only the owner differs — a term hangs
     off exactly one of AccountId / ContractId.

     Two things are contract-specific, and both are visible in the UI:
       • KIND ELIGIBILITY — Fee and InterestRate on every contract type;
         ExpectedReturn is refused (it prices invested principal, which a
         contract does not hold). No per-ContractType matrix.
       • CURRENCY — a contract has no currency of its own, so an Amount term
         must name one explicitly. There is nothing to default from.
     Plus a per-contract cap (ContractMaxTermsPerContract, default 500) and
     archived contracts being read-only for terms. */

  // The look-ahead for the header's "Next charges" group — the same 45 days
  // Subscriptions uses for its upcoming renewals.
  D.CONTRACTS_CHARGE_WINDOW_DAYS = 45;

  D.CONTRACT_MAX_TERMS_PER_CONTRACT = 500;
  D.contractTermKinds = ['InterestRate', 'Fee'];

  /* Seed term history, keyed by contractId. EffectiveFrom ascending here for
     readability; the helpers sort as needed. Every row carries an explicit
     currency when its unit is Amount — the contract rule, seeded as it writes. */
  D.contractTerms = {
    // Maple St lease — the rent as a dated series (a review, plus a scheduled
    // increase), beside the charges the tenancy carries.
    'ct-lease': [
      { id: 'ctm-lease-1', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 2150.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2025-09-01', anchorDate: '2025-09-01', label: 'Monthly rent', labelKey: 'monthly rent', note: 'Due on the 1st.', createdAtUtc: '2025-08-14T10:00:00Z' },
      { id: 'ctm-lease-2', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 2250.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-03-01', label: 'Monthly rent', labelKey: 'monthly rent', note: 'Indexed to CPI at the mid-term review.', createdAtUtc: '2026-01-28T09:00:00Z' },
      // Future effective date → reads "Scheduled", not in force yet.
      { id: 'ctm-lease-3', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 2350.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-10-01', label: 'Monthly rent', labelKey: 'monthly rent', note: 'Notified 30 May 2026.', createdAtUtc: '2026-05-30T09:00:00Z' },
      { id: 'ctm-lease-4', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 85.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2025-09-01', label: 'Parking space', labelKey: 'parking space', note: null, createdAtUtc: '2025-08-14T10:00:00Z' },
      { id: 'ctm-lease-5', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 50.00, currency: 'USD', interval: 'PerOccurrence', intervalCount: null, effectiveFrom: '2025-09-01', label: 'Late payment', labelKey: 'late payment', note: 'Charged after five days in arrears.', createdAtUtc: '2025-08-14T10:00:00Z' },
      { id: 'ctm-lease-6', contractId: 'ct-lease', kind: 'Fee', unit: 'Amount', value: 300.00, currency: 'USD', interval: 'OneTime', intervalCount: null, effectiveFrom: '2025-09-01', label: 'End-of-tenancy cleaning', labelKey: 'end-of-tenancy cleaning', note: null, createdAtUtc: '2025-08-14T10:00:00Z' },
    ],
    // Fiber service — a price rise on the monthly charge, plus two one-offs.
    'ct-fiber': [
      { id: 'ctm-fiber-1', contractId: 'ct-fiber', kind: 'Fee', unit: 'Amount', value: 79.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2025-02-01', label: 'Monthly service', labelKey: 'monthly service', note: '1 Gbps symmetric.', createdAtUtc: '2025-01-22T09:00:00Z' },
      { id: 'ctm-fiber-2', contractId: 'ct-fiber', kind: 'Fee', unit: 'Amount', value: 84.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-02-01', label: 'Monthly service', labelKey: 'monthly service', note: 'Annual CPI + 3.9% uplift.', createdAtUtc: '2026-01-04T09:00:00Z' },
      { id: 'ctm-fiber-3', contractId: 'ct-fiber', kind: 'Fee', unit: 'Amount', value: 240.00, currency: 'USD', interval: 'OneTime', intervalCount: null, effectiveFrom: '2025-02-01', label: 'Early termination', labelKey: 'early termination', note: 'Falls away at the end of the 24-month term.', createdAtUtc: '2025-01-22T09:00:00Z' },
      { id: 'ctm-fiber-4', contractId: 'ct-fiber', kind: 'Fee', unit: 'Amount', value: 99.00, currency: 'USD', interval: 'OneTime', intervalCount: null, effectiveFrom: '2025-02-01', label: 'Installation', labelKey: 'installation', note: null, createdAtUtc: '2025-01-22T09:00:00Z' },
    ],
    // Vendor note on the house purchase — the one seeded InterestRate, which a
    // contract carries unlabelled exactly as an account does.
    'ct-house': [
      { id: 'ctm-house-1', contractId: 'ct-house', kind: 'InterestRate', unit: 'Percentage', value: 0.0425, currency: null, interval: null, intervalCount: null, effectiveFrom: '2021-04-15', label: null, labelKey: null, note: 'Vendor financing on the balance of the purchase price.', createdAtUtc: '2021-04-15T09:00:00Z' },
      { id: 'ctm-house-2', contractId: 'ct-house', kind: 'InterestRate', unit: 'Percentage', value: 0.0399, currency: null, interval: null, intervalCount: null, effectiveFrom: '2024-05-01', label: null, labelKey: null, note: 'Renegotiated at the three-year review.', createdAtUtc: '2024-05-01T09:00:00Z' },
    ],
    'ct-parking': [
      { id: 'ctm-parking-1', contractId: 'ct-parking', kind: 'Fee', unit: 'Amount', value: 165.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2025-11-01', label: 'Space licence', labelKey: 'space licence', note: 'Due on the 1st.', createdAtUtc: '2025-10-20T09:00:00Z' },
      { id: 'ctm-parking-2', contractId: 'ct-parking', kind: 'Fee', unit: 'Amount', value: 40.00, currency: 'USD', interval: 'OneTime', intervalCount: null, effectiveFrom: '2025-11-01', label: 'Access fob', labelKey: 'access fob', note: null, createdAtUtc: '2025-10-20T09:00:00Z' },
    ],
    'ct-energy': [
      { id: 'ctm-energy-1', contractId: 'ct-energy', kind: 'Fee', unit: 'Amount', value: 28.50, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-10-15', label: 'Standing charge', labelKey: 'standing charge', note: 'Fixed for the term.', createdAtUtc: '2026-09-02T09:00:00Z' },
    ],
    'ct-storage': [
      { id: 'ctm-storage-1', contractId: 'ct-storage', kind: 'Fee', unit: 'Amount', value: 95.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2024-01-01', label: 'Unit rent', labelKey: 'unit rent', note: null, createdAtUtc: '2024-01-03T09:00:00Z' },
      { id: 'ctm-storage-2', contractId: 'ct-storage', kind: 'Fee', unit: 'Amount', value: 105.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2025-01-01', label: 'Unit rent', labelKey: 'unit rent', note: 'Second-year rate.', createdAtUtc: '2024-12-02T09:00:00Z' },
    ],
    // Archived contract — its history stays readable; every write is refused.
    'ct-solar': [
      { id: 'ctm-solar-1', contractId: 'ct-solar', kind: 'Fee', unit: 'Amount', value: 130.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2023-06-01', label: 'Lease payment', labelKey: 'lease payment', note: null, createdAtUtc: '2023-05-28T09:00:00Z' },
      { id: 'ctm-solar-2', contractId: 'ct-solar', kind: 'Fee', unit: 'Amount', value: 138.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2024-06-01', label: 'Lease payment', labelKey: 'lease payment', note: 'Annual 3% escalator.', createdAtUtc: '2024-06-01T09:00:00Z' },
    ],
    'ct-gym': [
      { id: 'ctm-gym-1', contractId: 'ct-gym', kind: 'Fee', unit: 'Amount', value: 39.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-09-01', label: 'Membership', labelKey: 'membership', note: null, createdAtUtc: '2026-06-10T09:00:00Z' },
      { id: 'ctm-gym-2', contractId: 'ct-gym', kind: 'Fee', unit: 'Amount', value: 25.00, currency: 'USD', interval: 'OneTime', intervalCount: null, effectiveFrom: '2026-09-01', label: 'Joining fee', labelKey: 'joining fee', note: null, createdAtUtc: '2026-06-10T09:00:00Z' },
    ],
    /* A DRAFT with a fully priced fee — the demonstration that the money
       roll-ups gate on STATUS, not on whether a price exists. This 180/month
       appears in the term history and in nothing else: not the run rate, not
       the by-type cost split, not the upcoming charges. */
    'ct-cleaning': [
      { id: 'ctm-cleaning-1', contractId: 'ct-cleaning', kind: 'Fee', unit: 'Amount', value: 180.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, effectiveFrom: '2026-11-01', label: 'Cleaning', labelKey: 'cleaning', note: 'Quoted rate — not agreed until the contract is signed.', createdAtUtc: '2026-09-12T11:00:00Z' },
    ],
    // ct-employment intentionally has no terms — drives the empty state.
  };

  Object.assign(H, {
    // All terms on a contract, EffectiveFrom DESC (the history listing order).
    conTermsFor(contractId) {
      return (D.contractTerms[contractId] || [])
        .slice()
        .sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0));
    },
    // TermKinds a CONTRACT may carry, in registry order. Not a per-type matrix:
    // the same two kinds on every ContractType.
    conEligibleTermKinds() {
      return D.termKinds.filter(k => D.contractTermKinds.includes(k.key)).map(k => k.key);
    },
    // Why a write is refused, or null when it is allowed. One place, so the
    // disabled menu item, the section notice and the dialog all say the same.
    /* ---- Next recurring charge, derived from the terms in force ------------
       A contract has no billing schedule of its own. What it has is a term
       history, and a term in force with a PERIODIC interval (Daily / Weekly /
       Monthly / Annually, times IntervalCount) describes a recurring charge.
       The next occurrence is projected from the term's cadence anchor —
       AnchorDate when set, otherwise EffectiveFrom — exactly as a subscription
       projects from its first billing date. Nothing is scheduled or stored;
       this is a read. */

    // The k-th occurrence of a periodic term, as 'YYYY-MM-DD'. Month and year
    // steps are calendar steps (clamped into short months), never 30/365 days.
    conOccurrence(term, k) {
      const iv = D.intervalByKey[term.interval];
      if (!iv || !iv.periodic) return null;
      const anchor = H.conDateOnly(term.anchorDate || term.effectiveFrom);
      if (!anchor) return null;
      const [ay, am, ad] = anchor.split('-').map(Number);
      const n = Math.max(1, term.intervalCount || 1) * k;
      const pad = (x) => String(x).padStart(2, '0');
      const dim = (y, m) => new Date(Date.UTC(y, m, 0)).getUTCDate();
      if (iv.key === 'Daily' || iv.key === 'Weekly') {
        const d = new Date(Date.UTC(ay, am - 1, ad + n * (iv.key === 'Weekly' ? 7 : 1)));
        return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
      }
      const months = iv.key === 'Annually' ? n * 12 : n;
      const total = (am - 1) + months;
      const y = ay + Math.floor(total / 12);
      const m = (total % 12) + 1;
      return `${y}-${pad(m)}-${pad(Math.min(ad, dim(y, m)))}`;
    },

    // The first occurrence of a periodic term falling on or after `today`.
    conNextOccurrence(term, today) {
      const t = today || H.conToday();
      for (let k = 0; k < 6000; k++) {
        const iso = H.conOccurrence(term, k);
        if (!iso) return null;
        if (iso >= t) return iso;
      }
      return null;
    },

    /* The contract's soonest recurring charge: over the FEE terms in force
       (amounts only — a percentage fee has no due amount to show), the
       earliest next occurrence that still falls inside the contract's term.
       Returns { date, days, term } or null. */
    conNextCharge(contract, today) {
      const t = today || H.conToday();
      if (!contract || contract.archived) return null;
      const status = H.conStatus(contract, t);
      if (status === 'Expired') return null;
      // A paused contract is not charging — no next charge, no run rate.
      if (status === 'Paused') return null;
      // Nor is an unsigned one: a draft may be fully priced, but nothing has
      // been agreed, so there is no charge to expect. Same single gate the
      // run rate uses (status !== 'Active').
      if (H.conIsUnsigned(status)) return null;
      const inForce = window.trmCurrentFromList
        ? window.trmCurrentFromList(H.conTermsFor(contract.id))
        : [];
      const end = H.conDateOnly(contract.endDate);
      const start = H.conDateOnly(contract.startDate);
      let best = null;
      for (const term of (inForce || [])) {
        if (term.kind !== 'Fee' || term.unit !== 'Amount') continue;
        const iv = D.intervalByKey[term.interval];
        if (!iv || !iv.periodic) continue;
        const date = H.conNextOccurrence(term, start && start > t ? start : t);
        if (!date) continue;
        // A charge never falls outside the agreement it is priced under.
        if (end && date > end) continue;
        if (!best || date < best.date) best = { date, term };
      }
      return best ? { ...best, days: H.conDaysUntil(best.date, t) } : null;
    },

    // The soonest next charges within `windowDays` (default 45), one row per
    // contract, ascending and capped — the Subscriptions renewal list's shape.
    conUpcomingCharges(contracts, today, opts) {
      const t = today || H.conToday();
      const windowDays = (opts && opts.windowDays != null) ? opts.windowDays : D.CONTRACTS_CHARGE_WINDOW_DAYS;
      const limit = (opts && opts.limit != null) ? opts.limit : 6;
      const out = [];
      for (const c of (contracts || D.contracts)) {
        const next = H.conNextCharge(c, t);
        if (!next || next.days == null || next.days > windowDays) continue;
        out.push({ contract: c, ...next });
      }
      out.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
      return out.slice(0, limit);
    },

    // 'Oct 1' — the compact charge date, parsed as UTC so it never drifts.
    conDateMd(iso) {
      if (!iso) return '—';
      const [y, m, d] = String(iso).slice(0, 10).split('-').map(Number);
      const MON = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
      return `${MON[m - 1]} ${d}`;
    },
    // 'today' / 'tomorrow' / 'in 12 days' — the relative word beside it.
    conRelDays(days) {
      if (days == null) return '';
      if (days <= 0) return 'today';
      if (days === 1) return 'tomorrow';
      return `in ${days} days`;
    },

    /* ---- Run rate ----------------------------------------------------------
       What the agreements on file cost per month and per year, read off the
       FEE terms in force exactly as the next-charge rows are. Each amount is
       projected by its cadence (Amount ÷ IntervalCount × periods), then
       converted to the workspace base via the shared FX helper — a currency
       with no rate is listed as unconverted, never silently zeroed. Only
       contracts currently running count: Draft, Ready, Upcoming, Expired and
       Archived records carry no run rate. The unsigned two are excluded by the
       same single status gate as the rest — a priced draft is a quote, not a
       commitment, and inflating the run rate with it is the defect this
       feature exists to close.
       One-time and per-occurrence fees are excluded by construction — they
       have no cadence, so there is no rate to project. */
    conRunRate(contracts, today, baseCurrency) {
      const t = today || H.conToday();
      const base = baseCurrency || (D.currencies.find(c => c.base) || {}).code || 'USD';
      const F = {
        Daily:    { mo: 365.25 / 12, yr: 365.25 },
        Weekly:   { mo: 52.1775 / 12, yr: 52.1775 },
        Monthly:  { mo: 1, yr: 12 },
        Annually: { mo: 1 / 12, yr: 1 },
      };
      const convert = H.insConvert || ((amt, from, to) => (from === to ? amt : null));
      const unconverted = new Set();
      const byType = {};
      let monthly = 0, yearly = 0, any = false;
      for (const c of (contracts || D.contracts)) {
        if (c.archived) continue;
        if (H.conStatus(c, t) !== 'Active') continue;
        const inForce = window.trmCurrentFromList ? window.trmCurrentFromList(H.conTermsFor(c.id)) : [];
        for (const term of (inForce || [])) {
          if (term.kind !== 'Fee' || term.unit !== 'Amount') continue;
          const iv = D.intervalByKey[term.interval];
          if (!iv || !iv.periodic || !F[iv.key]) continue;
          const every = Math.max(1, term.intervalCount || 1);
          const cur = term.currency || base;
          const mo = convert((term.value * F[iv.key].mo) / every, cur, base);
          const yr = convert((term.value * F[iv.key].yr) / every, cur, base);
          if (mo == null || yr == null) { unconverted.add(cur); continue; }
          monthly += mo; yearly += yr; any = true;
          if (!byType[c.type]) byType[c.type] = { monthly: 0, yearly: 0, count: 0 };
          byType[c.type].monthly += mo;
          byType[c.type].yearly += yr;
          byType[c.type].count += 1;
        }
      }
      return {
        baseCurrency: base,
        monthly: any ? monthly : null,
        yearly: any ? yearly : null,
        unconvertedCurrencies: [...unconverted].sort(),
        // Registry order, only the types that actually carry a rate.
        typeRows: D.contractTypes
          .filter(ty => byType[ty.key])
          .map(ty => ({ key: ty.key, label: ty.label, icon: ty.icon, color: ty.color,
            monthly: byType[ty.key].monthly, yearly: byType[ty.key].yearly, count: byType[ty.key].count })),
      };
    },

    /* Archivability, widened. A contract may be archived once it has ENDED,
       or while it is still UNSIGNED — abandoning a negotiation is the single
       most likely reason to archive a draft, and a draft typically has no end
       date at all, so the ended-only rule would strand it forever.
       Unarchiving is never refused. */
    conArchivable(contract, today) {
      if (!contract) return false;
      if (contract.archived) return true;
      if (!contract.signed) return true;
      const t = today || H.conToday();
      const status = H.conStatus(contract, t);
      return status === 'Expired' || (!!contract.completionDate && H.conDateOnly(contract.completionDate) <= t);
    },

    conTermWriteBlock(contract, termCount, cap) {
      if (contract && contract.archived) {
        return { reason: 'archived', text: 'This contract is archived. Restore it to record or change a term — its history stays readable either way.' };
      }
      const limit = cap != null ? cap : D.CONTRACT_MAX_TERMS_PER_CONTRACT;
      if (termCount >= limit) {
        return { reason: 'cap', text: `This contract has reached the limit of ${limit} term${limit === 1 ? '' : 's'}. Delete an entry, or raise ContractMaxTermsPerContract in system settings.` };
      }
      return null;
    },
  });

  // Convenience index used by conResolveParty / policy options.
  D.insurancePolicyById = Object.fromEntries((D.insurancePolicies || []).map(p => [p.id, p]));

  // Register the contract file-kinds in the shared file-type lookup so the
  // reused AfmUpload rows (which resolve icons via OdysseyData.fileTypeByKey)
  // render the correct glyph/color. Additive only — never overwrites an
  // existing account/transaction kind (e.g. the shared 'Other').
  if (D.fileTypeByKey) {
    D.contractFileTypes.forEach(t => { if (!D.fileTypeByKey[t.key]) D.fileTypeByKey[t.key] = t; });
  }
})();
