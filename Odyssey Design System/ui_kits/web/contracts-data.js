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
                        `role` is a ContractPartyRole key (REQUIRED — legal
                        values depend on the contract's type, see the matrix);
                        `fromDate`/`toDate` are
                        the party's TERM IN THE ROLE — both null is the DEFAULT
                        term (the contract's own extent), not an unset value,
                        exactly as an insurance party's term reads.
     • ContractFile   { id, fileMetadataId, fileType, attachedByUserId,
                        attachedAtUtc, validFrom, validTo, issuedAt, issuedBy } — a REFERENCE to an existing FileMetadata
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
    /* Loan (8) is APPENDED in ordinal and placed here in READING order, after
       Purchase — a mortgage was filed as a Purchase before this member existed.
       The hue is the one wide gap left on the wheel between Rental (60) and
       Purchase (140); it clears both by 40° at the same L/C as its neighbours. */
    { key: 'Loan',       label: 'Loan',       enumValue: 8, icon: 'account_balance',     color: 'oklch(0.77 0.13 100)', soft: 'oklch(0.77 0.13 100 / 0.16)', desc: 'A loan or mortgage — money advanced under an agreement to repay.' },
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

  /* ---- Canonical ContractPartyRole registry — what a linked record DOES in
     the agreement, orthogonal to its kind. EIGHTEEN live members; `Unspecified`
     (0) and `ServiceProvider` (5) are RETIRED and their ordinals are permanent
     holes that must never be reused — reusing one would make an unmigrated row
     mean something new rather than nothing. ORDINALS ARE A WIRE AND PERSISTENCE
     CONTRACT: later members append, none is renumbered.

     With `Unspecified` gone a role is REQUIRED on every party write, so this
     kit has no "no role stated" member and no default selection anywhere; the
     only remaining unset role is a legacy row, drawn as an absence.
     Colours sit in the same categorical band as the other registries; `Guarantor`
     and `Broker` are deliberately low-chroma because they are legal on every
     type and should not read as a category of their own.
     The three OBJECT roles (`Object`, `Property`, `Collateral` — 17/18/19) name
     the THING the agreement is about rather than a side of it; `object: true`
     is what the party tile reads to draw them apart. Role stays orthogonal to
     kind: an object party is expected to be an Account, but a Contact target is
     equally legal and never refused. */
  D.contractPartyRoles = [
    { key: 'Employee',     label: 'Employee',     enumValue: 1,  icon: 'badge',              color: 'oklch(0.76 0.13 265)', soft: 'oklch(0.76 0.13 265 / 0.16)', desc: 'The person employed under this agreement.' },
    { key: 'Employer',     label: 'Employer',     enumValue: 2,  icon: 'corporate_fare',     color: 'oklch(0.75 0.14 300)', soft: 'oklch(0.75 0.14 300 / 0.16)', desc: 'The party that employs.' },
    { key: 'Buyer',        label: 'Buyer',        enumValue: 3,  icon: 'shopping_bag',       color: 'oklch(0.79 0.14 145)', soft: 'oklch(0.79 0.14 145 / 0.16)', desc: 'The party acquiring under this agreement.' },
    { key: 'Seller',       label: 'Seller',       enumValue: 4,  icon: 'sell',               color: 'oklch(0.80 0.13 90)',  soft: 'oklch(0.80 0.13 90 / 0.16)',  desc: 'The party disposing under this agreement — including supplying a service.' },
    { key: 'Other',        label: 'Other',        enumValue: 6,  icon: 'more_horiz',         color: 'oklch(0.77 0.10 25)',  soft: 'oklch(0.77 0.10 25 / 0.16)',  desc: 'A deliberate role that is none of the others.' },
    { key: 'Landlord',     label: 'Landlord',     enumValue: 7,  icon: 'vpn_key',            color: 'oklch(0.79 0.13 55)',  soft: 'oklch(0.79 0.13 55 / 0.16)',  desc: 'The party letting the property under this tenancy.' },
    { key: 'Tenant',       label: 'Tenant',       enumValue: 8,  icon: 'home',               color: 'oklch(0.78 0.13 35)',  soft: 'oklch(0.78 0.13 35 / 0.16)',  desc: 'The party occupying under this tenancy.' },
    { key: 'Insurer',      label: 'Insurer',      enumValue: 9,  icon: 'shield',             color: 'oklch(0.75 0.14 285)', soft: 'oklch(0.75 0.14 285 / 0.16)', desc: 'The party carrying the risk.' },
    { key: 'Policyholder', label: 'Policyholder', enumValue: 10, icon: 'assignment_ind',     color: 'oklch(0.76 0.13 255)', soft: 'oklch(0.76 0.13 255 / 0.16)', desc: 'The party that holds the policy and owes the premium.' },
    { key: 'Insured',      label: 'Insured',      enumValue: 11, icon: 'health_and_safety',  color: 'oklch(0.77 0.13 215)', soft: 'oklch(0.77 0.13 215 / 0.16)', desc: 'The person, account or thing covered — one member for both party kinds.' },
    { key: 'Beneficiary',  label: 'Beneficiary',  enumValue: 12, icon: 'volunteer_activism', color: 'oklch(0.78 0.13 185)', soft: 'oklch(0.78 0.13 185 / 0.16)', desc: 'The party that receives on the policy. Blocks deletion of the linked contact.' },
    { key: 'Lender',       label: 'Lender',       enumValue: 13, icon: 'savings',            color: 'oklch(0.78 0.13 120)', soft: 'oklch(0.78 0.13 120 / 0.16)', desc: 'The party advancing the money.' },
    { key: 'Borrower',     label: 'Borrower',     enumValue: 14, icon: 'request_quote',      color: 'oklch(0.78 0.13 165)', soft: 'oklch(0.78 0.13 165 / 0.16)', desc: 'The party that owes the money back.' },
    { key: 'Guarantor',    label: 'Guarantor',    enumValue: 15, icon: 'verified_user',      color: 'oklch(0.76 0.07 330)', soft: 'oklch(0.76 0.07 330 / 0.16)', desc: 'A party standing behind another’s obligation. Legal on every type.' },
    { key: 'Broker',       label: 'Broker',       enumValue: 16, icon: 'handshake',          color: 'oklch(0.76 0.07 245)', soft: 'oklch(0.76 0.07 245 / 0.16)', desc: 'An intermediary that arranged the agreement. Legal on every type.' },
    { key: 'Object',       label: 'Object',       enumValue: 17, icon: 'category',           color: 'oklch(0.78 0.11 75)',  soft: 'oklch(0.78 0.11 75 / 0.16)',  object: true, desc: 'The thing the agreement concerns — the record it is about, not a side of it.' },
    { key: 'Property',     label: 'Property',     enumValue: 18, icon: 'holiday_village',    color: 'oklch(0.78 0.11 45)',  soft: 'oklch(0.78 0.11 45 / 0.16)',  object: true, desc: 'Real property or goods — the let premises, the purchased asset.' },
    { key: 'Collateral',   label: 'Collateral',   enumValue: 19, icon: 'lock',               color: 'oklch(0.78 0.11 105)', soft: 'oklch(0.78 0.11 105 / 0.16)', object: true, desc: 'Security pledged against the loan.' },
  ];

  /* ---- The contract type × party role MATRIX — the client half of the shared
     server declaration, not a copy of a rule the client invented. Per type:
     `suggested` (legal, offered first) and `allowed` (legal, offered after);
     anything in neither is rejected server-side with a 422. 69 of the 162 cells
     are legal. Every type carries at least one suggested role, so the picker's
     first group is never empty — `Other`-the-type suggests `Other`-the-role,
     which is the only honest suggestion for "none of the above".
     `Guarantor`, `Broker` and `Other` are the universal trio: legal on every
     type, suggested on none, always in that order at the end of `allowed`.
     `Object` is deliberately NOT legal on Employment (`Employee` already names
     the object) nor on Insurance (`Insured` already covers "the thing covered"). */
  D.contractPartyRoleMatrix = {
    Employment:   { suggested: ['Employee', 'Employer'],             allowed: ['Guarantor', 'Broker', 'Other'] },
    Service:      { suggested: ['Buyer', 'Seller'],                  allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Rental:       { suggested: ['Landlord', 'Tenant', 'Property'],   allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Insurance:    { suggested: ['Insurer', 'Policyholder', 'Insured', 'Beneficiary'], allowed: ['Guarantor', 'Broker', 'Other'] },
    Subscription: { suggested: ['Buyer', 'Seller'],                  allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Purchase:     { suggested: ['Buyer', 'Seller', 'Property'],      allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Loan:         { suggested: ['Lender', 'Borrower', 'Collateral'], allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Membership:   { suggested: ['Buyer', 'Seller'],                  allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
    Other:        { suggested: ['Other'], allowed: ['Employee', 'Employer', 'Buyer', 'Seller', 'Landlord', 'Tenant', 'Insurer', 'Policyholder', 'Insured', 'Beneficiary', 'Lender', 'Borrower', 'Object', 'Property', 'Collateral', 'Guarantor', 'Broker'] },
  };

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
        // The salary account is a party to the agreement but plays neither
        // side of it — a deliberate `Other`, which is where the migration
        // moved every old `Unspecified` row and where this one belongs.
        { id: 'cp-emp-2', accountId: '1', role: 'Other', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-emp-1', fileMetadataId: 'fm-emp-offer', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2024-02-20T09:05:00Z', validFrom: '2024-03-01', validTo: null, issuedAt: '2024-02-18', issuedBy: 'c3' },
        { id: 'cf-emp-2', fileMetadataId: 'fm-emp-handbook', kind: 'Other', attachedByUserId: 'u-owner', attachedAtUtc: '2024-02-20T09:06:00Z' },
      ],
    },
    {
      id: 'ct-lease', name: 'Maple St Residence — Lease', type: 'Rental',
      description: 'Twelve-month assured shorthold tenancy on the Maple St residence. Rent due on the 1st. Pets permitted by amendment.',
      startDate: '2025-09-01', endDate: '2026-08-31', ready: '2025-08-14T09:00:00Z', signed: '2025-08-20T09:00:00Z', paused: null, archived: null, createdAtUtc: '2025-08-14T10:00:00Z', createdByUserId: 'u-jane',
      parties: [
        // The let flat itself — an OBJECT party. Before the object roles this
        // was filed under the catch-all `Other` and lost what it meant.
        { id: 'cp-lease-1', accountId: '7', role: 'Property', fromDate: null, toDate: null },
        // A party that joined partway through the term — the case the term
        // exists for. Rental now has its own vocabulary, so this is a Landlord
        // rather than the `Other` the pre-matrix seeder had to settle for.
        { id: 'cp-lease-2', contactId: 'c9', role: 'Landlord', fromDate: '2026-02-01', toDate: null },
      ],
      files: [
        { id: 'cf-lease-1', fileMetadataId: 'fm-lease-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2025-08-14T10:02:00Z', validFrom: '2025-09-01', validTo: '2026-08-31', issuedAt: '2025-08-12', issuedBy: 'c9' },
        { id: 'cf-lease-2', fileMetadataId: 'fm-lease-amend', kind: 'Amendment', attachedByUserId: 'u-owner', attachedAtUtc: '2026-01-08T14:00:00Z', validFrom: '2026-02-01', validTo: null, issuedAt: '2026-01-07', issuedBy: 'c9' },
        { id: 'cf-lease-3', fileMetadataId: 'fm-lease-letter', kind: 'Correspondence', attachedByUserId: 'u-owner', attachedAtUtc: '2026-05-30T11:00:00Z', validFrom: null, validTo: null, issuedAt: '2026-05-29', issuedBy: 'c9' },
      ],
    },
    {
      id: 'ct-house', name: 'Maple St Residence — Purchase', type: 'Purchase',
      description: 'Purchase of the Maple St property — a one-off agreement recorded by its completion (closing) date, not a term. Kept as the deed of record for the property.',
      startDate: null, endDate: null, completionDate: '2021-04-15', ready: '2021-03-02T09:00:00Z', signed: '2021-03-30T09:00:00Z', paused: null, archived: null, createdAtUtc: '2021-03-02T09:00:00Z', createdByUserId: null,
      parties: [
        // The property bought is the OBJECT of the purchase, not its buyer.
        { id: 'cp-house-1', accountId: '7', role: 'Property', fromDate: null, toDate: null },
        { id: 'cp-house-3', contactId: 'c2', role: 'Buyer', fromDate: null, toDate: null },
        { id: 'cp-house-2', contactId: 'c9', role: 'Seller', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-house-1', fileMetadataId: 'fm-house-deed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2021-04-15T12:00:00Z', validFrom: '2021-04-09', validTo: null, issuedAt: '2021-04-09', issuedBy: 'c9' },
      ],
    },
    /* LOAN — the new contract type, and the reason it exists: before it, this
       was filed as a Purchase with a Buyer and a Seller. All three of its
       parties come from the Loan column of the matrix. */
    {
      id: 'ct-auto-loan', name: 'Citi Auto Loan — 60 Month', type: 'Loan',
      description: 'Fixed-rate 60-month auto loan against the vehicle. Monthly repayment by direct debit; early settlement permitted without penalty after month 12.',
      startDate: '2023-06-01', endDate: '2028-05-31', ready: '2023-05-20T09:00:00Z', signed: '2023-05-26T09:00:00Z', paused: null, archived: null, createdAtUtc: '2023-05-20T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-loan-1', contactId: 'c13', role: 'Lender', fromDate: null, toDate: null },
        { id: 'cp-loan-2', accountId: '5', role: 'Borrower', fromDate: null, toDate: null },
        // Guarantor is universal now — legal on every type, suggested on none.
        { id: 'cp-loan-3', contactId: 'c9', role: 'Guarantor', fromDate: null, toDate: null },
        // The security pledged against the loan — a Loan's own object role.
        { id: 'cp-loan-4', accountId: '5', role: 'Collateral', fromDate: null, toDate: null },
      ],
      files: [],
    },
    /* INSURANCE — the only type with four suggested roles, mirroring the four
       link collections on an insurance policy. The Beneficiary here is the
       party whose contact can no longer be deleted silently. */
    {
      id: 'ct-home-cover', name: 'Pacific Home Insurance — Buildings & Contents', type: 'Insurance',
      description: 'Buildings and contents cover on the Maple St residence. Annual premium, paid in one instalment on renewal.',
      startDate: '2026-04-01', endDate: '2027-03-31', ready: '2026-03-10T09:00:00Z', signed: '2026-03-18T09:00:00Z', paused: null, archived: null, createdAtUtc: '2026-03-10T09:00:00Z', createdByUserId: 'u-jane',
      parties: [
        { id: 'cp-cover-1', contactId: 'c12', role: 'Insurer', fromDate: null, toDate: null },
        // One `Insured` member serves both party kinds — the kind discriminator
        // already says whether the covered thing is an account or a contact.
        { id: 'cp-cover-2', accountId: '7', role: 'Insured', fromDate: null, toDate: null },
        { id: 'cp-cover-3', contactId: 'c9', role: 'Beneficiary', fromDate: null, toDate: null },
      ],
      files: [],
    },
    {
      id: 'ct-fiber', name: 'Fiber Internet — 24 Month', type: 'Service',
      description: 'Symmetric 1 Gbps fiber. 24-month term, early-termination fee applies. Auto-renews monthly at term end.',
      startDate: '2025-02-01', endDate: '2027-01-31', ready: '2025-01-22T09:00:00Z', signed: '2025-01-24T09:00:00Z', paused: null, archived: null, createdAtUtc: '2025-01-22T09:00:00Z', createdByUserId: 'u-sam',
      parties: [
        { id: 'cp-fiber-1', contactId: 'c3', role: 'Seller', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-fiber-1', fileMetadataId: 'fm-fiber-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2025-01-22T09:03:00Z', validFrom: '2025-02-01', validTo: '2027-01-31', issuedAt: '2025-01-20', issuedBy: 'c3' },
        { id: 'cf-fiber-2', fileMetadataId: 'fm-misc-1', kind: 'Correspondence', attachedByUserId: 'u-owner', attachedAtUtc: '2026-03-15T09:00:00Z' },
      ],
    },
    {
      id: 'ct-gym', name: 'FitZone — Membership', type: 'Membership',
      description: 'Annual gym membership. Direct debit, monthly. Frozen over the winter — resuming in the spring.',
      startDate: '2026-09-01', endDate: '2027-08-31', ready: '2026-06-10T09:00:00Z', signed: '2026-06-12T09:00:00Z', paused: '2026-09-14T10:30:00Z', archived: null, createdAtUtc: '2026-06-10T09:00:00Z', createdByUserId: 'u-mira',
      parties: [
        { id: 'cp-gym-1', contactId: 'c11', role: 'Seller', fromDate: null, toDate: null },
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
        { id: 'cp-parking-1', contactId: 'c8', role: 'Landlord', fromDate: null, toDate: null },
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
        { id: 'cp-energy-1', contactId: 'c3', role: 'Seller', fromDate: null, toDate: null },
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
        { id: 'cp-storage-1', contactId: 'c8', role: 'Landlord', fromDate: null, toDate: '2025-12-31' },
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
        // The roof the panels sit on — `Object` on an Other-type contract.
        { id: 'cp-solar-1', accountId: '7', role: 'Object', fromDate: null, toDate: null },
      ],
      files: [
        { id: 'cf-solar-1', fileMetadataId: 'fm-solar-signed', kind: 'Signed', attachedByUserId: 'u-owner', attachedAtUtc: '2023-05-28T09:04:00Z', validFrom: '2023-06-01', validTo: '2033-05-31', issuedAt: '2023-05-24', issuedBy: 'c8' },
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
        { id: 'cp-cleaning-1', contactId: 'c3', role: 'Seller', fromDate: null, toDate: null },
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
        { id: 'cp-tutoring-1', contactId: 'c8', role: 'Seller', fromDate: null, toDate: null },
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
    /* A ContractPartyRole key → its registry row. Two honest non-members:
       an EMPTY key is a legacy row written before a role was required (drawn
       as an absence, never as the deliberate `Other`), and an UNKNOWN key is a
       real runtime state — ordinals append server-side, so a client older than
       the deployment can be handed a member it has never heard of. */
    conPartyRoleInfo(key) {
      if (key == null || key === '') {
        return { key: '', label: 'No role set', icon: 'help_outline', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)',
                 unset: true, desc: 'Written before a role was required. Edit the party to state one.' };
      }
      return D.contractPartyRoleByKey[key]
        || { key, label: 'Unrecognised role', icon: 'help', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)',
             unknown: true, desc: 'This role was added after this app version — update to read it.' };
    },

    /* ---- The matrix, read three ways -------------------------------------
       `conRoleLegality` is the single cell lookup every other reader is built
       on. An UNKNOWN contract type reports 'allowed' rather than refusing:
       a type this client has never heard of must not make the server's legal
       roles unpickable. */
    conRoleLegality(contractType, roleKey) {
      const cell = D.contractPartyRoleMatrix[contractType];
      if (!cell) return 'allowed';
      if (cell.suggested.indexOf(roleKey) !== -1) return 'suggested';
      if (cell.allowed.indexOf(roleKey) !== -1) return 'allowed';
      return 'rejected';
    },
    // The legal registry rows for a type, suggested first, each tagged `group`.
    conRolesForType(contractType) {
      const cell = D.contractPartyRoleMatrix[contractType];
      if (!cell) return D.contractPartyRoles.map(r => ({ ...r, group: 'allowed' }));
      const pick = (keys, group) => keys
        .map(k => D.contractPartyRoleByKey[k]).filter(Boolean).map(r => ({ ...r, group }));
      return pick(cell.suggested, 'suggested').concat(pick(cell.allowed, 'allowed'));
    },
    // "Lender, Borrower, Guarantor, Broker or Other" — the sentence the 422
    // body and the picker's helper both need.
    conRoleListText(contractType) {
      const labels = H.conRolesForType(contractType).map(r => r.label);
      if (labels.length < 2) return labels[0] || '';
      return labels.slice(0, -1).join(', ') + ' or ' + labels[labels.length - 1];
    },
    /* The client half of the type-change 422: the parties an INCOMING type
       would reject, projected the way the server's problem body lists them.
       Empty means the change is safe. */
    conPartiesRejectedByType(parties, contractType) {
      return (parties || [])
        .filter(p => H.conRoleLegality(contractType, p.role) === 'rejected')
        .map(p => ({
          partyId: p.id,
          role: p.role,
          roleLabel: H.conPartyRoleInfo(p.role).label,
          displayName: H.conResolveParty(p).name,
        }));
    },
    /* Object parties first (what the contract is about), then everyone else
       in stored order — a stable partition, not a re-sort. */
    conSortParties(parties) {
      const isObj = p => !!(D.contractPartyRoleByKey[p.role] || {}).object;
      return (parties || []).filter(isObj).concat((parties || []).filter(p => !isObj(p)));
    },
    conPartyRoleOptions(contractType) {
      const rows = contractType ? H.conRolesForType(contractType) : D.contractPartyRoles;
      return rows.map(r => ({ value: r.key, label: r.label, icon: r.icon, iconColor: r.color, sub: r.desc, group: r.group }));
    },
    /* Contracts naming a contact as a BENEFICIARY — the contract half of the
       widened contact-delete 409. The payload the server sends is a count plus,
       only for a `contracts.read` holder, the { contractId, contractName }
       pairs; this returns the pairs and the caller decides what it may show. */
    conContractsWithBeneficiary(contactId) {
      return (D.contracts || [])
        .filter(c => (c.parties || []).some(p => p.contactId === contactId && p.role === 'Beneficiary'))
        .map(c => ({ contractId: c.id, contractName: c.name, type: c.type }));
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
        if ((p.role || '') !== role) return;
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
        // The four validity fields ride on the LINK row, not the FileMetadata:
        // the same stored file filed against two contracts can carry a
        // different period on each. Null on every document attached before the
        // feature existed — the table prints an em dash, never a guess.
        validFrom: cf.validFrom || null, validTo: cf.validTo || null,
        issuedAt: cf.issuedAt || null, issuedBy: cf.issuedBy || null,
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
     Plus a per-contract cap (ContractMaxTermsPerContract, default 500) — the
     only thing that refuses a term write. */

  // The look-ahead for the header's "Next charges" group — the same 45 days
  // Subscriptions uses for its upcoming renewals.
  D.CONTRACTS_CHARGE_WINDOW_DAYS = 45;

  D.CONTRACT_MAX_TERMS_PER_CONTRACT = 500;
  /* ContractMaxSmartTagsPerContract — a system setting (default 20, range
     1–50). The ceiling is ListDefaults.MaxFilterArrayLength: past it the
     feature's own resolution query (GET /api/transactions?tagIds=…) refuses
     the array the section builds. */
  D.CONTRACT_MAX_SMART_TAGS_PER_CONTRACT = 20;
  D.CONTRACT_SMART_TAGS_CEILING = 50;
  /* Seed associations by contractId (GET /api/contracts/{id}/smart-tags in the
     real app), ordered as AddedAt ascending. */
  D.contractSmartTagSeed = {
    'ct-lease': ['t4', 't7'],     // Maple St lease — Rent, Utilities
    'ct-fiber': ['t2'],           // Fiber service — Subscriptions
    'ct-employment': ['t5'],      // ACME Co — Salary
  };
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
    // Archived contract — hidden from the default list, still fully writable.
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
    /* The income-bearing contract. An employment agreement is the clearest
       case for direction: the salary is money IN and the deductions taken
       under the same agreement are money OUT, and neither cancels the other —
       they sit on opposite sides of the header's two grosses.

       Note the two anchor dates. The salary lands on the 25th and the dues on
       the 1st, so this contract has TWO next movements, and the header's
       collapse is per (contract, direction): one row in Next charges, one in
       Next receipts. Collapsing on the contract alone would silently discard
       whichever fell later.

       The signing bonus is the one-off case: a OneTime fee accepts a direction
       and keeps it on the record, and is still excluded from the run rate —
       the exclusion is about having no rate to project, not about direction. */
    'ct-employment': [
      { id: 'ctm-emp-1', contractId: 'ct-employment', kind: 'Fee', unit: 'Amount', value: 6250.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, direction: 'Incoming', anchorDate: '2024-03-25', effectiveFrom: '2024-03-01', label: 'Base salary', labelKey: 'base salary', note: 'Paid on the 25th.', createdAtUtc: '2024-02-24T09:00:00Z' },
      { id: 'ctm-emp-2', contractId: 'ct-employment', kind: 'Fee', unit: 'Amount', value: 6600.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, direction: 'Incoming', anchorDate: '2024-03-25', effectiveFrom: '2026-04-01', label: 'Base salary', labelKey: 'base salary', note: 'Annual review, effective April.', createdAtUtc: '2026-03-18T09:00:00Z' },
      { id: 'ctm-emp-3', contractId: 'ct-employment', kind: 'Fee', unit: 'Amount', value: 42.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, direction: 'Outgoing', anchorDate: '2024-03-01', effectiveFrom: '2024-03-01', label: 'Union dues', labelKey: 'union dues', note: 'Deducted at source.', createdAtUtc: '2024-02-24T09:00:00Z' },
      { id: 'ctm-emp-4', contractId: 'ct-employment', kind: 'Fee', unit: 'Amount', value: 380.00, currency: 'USD', interval: 'Monthly', intervalCount: 1, direction: 'Outgoing', anchorDate: '2025-01-25', effectiveFrom: '2025-01-01', label: 'Pension contribution', labelKey: 'pension contribution', note: 'Employee share, 5% of base.', createdAtUtc: '2024-12-11T09:00:00Z' },
      { id: 'ctm-emp-5', contractId: 'ct-employment', kind: 'Fee', unit: 'Amount', value: 3000.00, currency: 'USD', interval: 'OneTime', intervalCount: null, direction: 'Incoming', effectiveFrom: '2024-03-01', label: 'Signing bonus', labelKey: 'signing bonus', note: 'Paid with the first salary. One-off — recorded, never projected.', createdAtUtc: '2024-02-24T09:00:00Z' },
    ],
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
    /* The contract's soonest movement on ONE side. Direction is read off the
       in-force entry, so a superseded entry's direction never reaches a row. */
    conNextMovement(contract, today, direction) {
      const dir = direction || 'Outgoing';
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
        if (H.termDirection(term) !== dir) continue;
        const iv = D.intervalByKey[term.interval];
        if (!iv || !iv.periodic) continue;
        const date = H.conNextOccurrence(term, start && start > t ? start : t);
        if (!date) continue;
        // A movement never falls outside the agreement it is priced under.
        if (end && date > end) continue;
        if (!best || date < best.date) best = { date, term };
      }
      return best ? { ...best, direction: dir, days: H.conDaysUntil(best.date, t) } : null;
    },
    conNextCharge(contract, today) { return H.conNextMovement(contract, today, 'Outgoing'); },
    conNextReceipt(contract, today) { return H.conNextMovement(contract, today, 'Incoming'); },

    /* The soonest movements within `windowDays` (default 45) on ONE side, one
       row per contract, ascending and capped — the Subscriptions renewal
       list's shape. The cap applies PER LIST, so a file with many charges
       cannot starve the receipts beside it. */
    conUpcomingMovements(contracts, today, opts) {
      const t = today || H.conToday();
      const dir = (opts && opts.direction) || 'Outgoing';
      const windowDays = (opts && opts.windowDays != null) ? opts.windowDays : D.CONTRACTS_CHARGE_WINDOW_DAYS;
      const limit = (opts && opts.limit != null) ? opts.limit : 6;
      const out = [];
      for (const c of (contracts || D.contracts)) {
        const next = H.conNextMovement(c, t, dir);
        if (!next || next.days == null || next.days > windowDays) continue;
        out.push({ contract: c, ...next });
      }
      out.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
      return out.slice(0, limit);
    },
    conUpcomingCharges(contracts, today, opts) {
      return H.conUpcomingMovements(contracts, today, Object.assign({}, opts, { direction: 'Outgoing' }));
    },
    conUpcomingReceipts(contracts, today, opts) {
      return H.conUpcomingMovements(contracts, today, Object.assign({}, opts, { direction: 'Incoming' }));
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
      /* Two buckets, filled from the SAME read of the same in-force terms.
         Nothing is queried twice, and no gross ever mixes the two sides: the
         only figure that crosses them is the net, and it says so in its name. */
      const side = {
        Outgoing: { monthly: 0, yearly: 0, any: false, byType: {} },
        Incoming: { monthly: 0, yearly: 0, any: false, byType: {} },
      };
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
          // Named and excluded from BOTH sides and from the net — never 1:1.
          if (mo == null || yr == null) { unconverted.add(cur); continue; }
          // Bucketed AFTER the series collapse, on the winning entry's own
          // direction, so a superseded direction never reaches a total.
          const s = side[H.termDirection(term)];
          s.monthly += mo; s.yearly += yr; s.any = true;
          if (!s.byType[c.type]) s.byType[c.type] = { monthly: 0, yearly: 0, count: 0 };
          s.byType[c.type].monthly += mo;
          s.byType[c.type].yearly += yr;
          s.byType[c.type].count += 1;
        }
      }
      // Registry order, only the types that actually carry a rate on that side.
      const rowsFor = (s) => D.contractTypes
        .filter(ty => s.byType[ty.key])
        .map(ty => ({ key: ty.key, label: ty.label, icon: ty.icon, color: ty.color,
          monthly: s.byType[ty.key].monthly, yearly: s.byType[ty.key].yearly, count: s.byType[ty.key].count }));
      const out = side.Outgoing, inc = side.Incoming;
      /* The net is computed from the UNROUNDED sums and rounded once —
         differencing two already-rounded figures compounds the rounding
         rather than cancelling it. It is null only when BOTH sides are:
         a household with income and no recorded costs has a good net. */
      const anySide = out.any || inc.any;
      const net = (v) => Math.round(v * 100) / 100;
      return {
        baseCurrency: base,
        monthly: out.any ? out.monthly : null,
        yearly: out.any ? out.yearly : null,
        incomingMonthly: inc.any ? inc.monthly : null,
        incomingYearly: inc.any ? inc.yearly : null,
        netMonthly: anySide ? net(inc.monthly - out.monthly) : null,
        netYearly: anySide ? net(inc.yearly - out.yearly) : null,
        unconvertedCurrencies: [...unconverted].sort(),
        typeRows: rowsFor(out),
        incomingTypeRows: rowsFor(inc),
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

    /* The only thing that refuses a term write is the per-contract CAP.
       Archiving hides a contract from the default list; it never blocks
       recording what the agreement did or cost — a lease can be archived and
       still gain the final rent entry. */
    conTermWriteBlock(contract, termCount, cap) {
      const limit = cap != null ? cap : D.CONTRACT_MAX_TERMS_PER_CONTRACT;
      if (termCount >= limit) {
        return { reason: 'cap', text: `This contract has reached the limit of ${limit} term${limit === 1 ? '' : 's'}. Delete an entry, or raise ContractMaxTermsPerContract in system settings.` };
      }
      return null;
    },
  });

  // Register the contract file-kinds in the shared file-type lookup so the
  // reused AfmUpload rows (which resolve icons via OdysseyData.fileTypeByKey)
  // render the correct glyph/color. Additive only — never overwrites an
  // existing account/transaction kind (e.g. the shared 'Other').
  if (D.fileTypeByKey) {
    D.contractFileTypes.forEach(t => { if (!D.fileTypeByKey[t.key]) D.fileTypeByKey[t.key] = t; });
  }
})();
