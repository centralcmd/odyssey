/* Seed data for the Odyssey UI kit click-thru.
   Shapes mirror Odyssey.Finance.Dtos enums + records. No API calls. */

window.OdysseyData = {
  user: { name: 'Jane Sato', email: 'jane@odyssey.app', avatar: 'JS' },

  // Mirrors the Account entity: name, description, opened, accountNumber,
  // accountType, closed, archived, currencyCode (+ derived balance/delta for UI).
  accounts: [
    { id: '1', name: 'Chase Checking', number: '·1234', accountNumber: '1100 2233 0044', custodianId: 'c14', description: 'Primary everyday spending account', type: 'CheckingAccount', currency: 'USD', opened: '2021-03-14', closed: null, archived: null, balance:  4182.50, deltaLabel: '+$ 312.40 this week', deltaDir: 'up',   icon: 'account_balance', tone: 'tide' },
    { id: '2', name: 'Ally Savings', number: '·8821', accountNumber: '8842 5510 8821', custodianId: 'c15', description: 'High-yield emergency fund', type: 'SavingsAccount', currency: 'USD', opened: '2020-08-02', closed: null, archived: null, balance: 18902.10, deltaLabel: '+$ 41.05 interest', deltaDir: 'up',   icon: 'savings', tone: 'sea' },
    { id: '3', name: 'Amex Platinum', number: '·5512', accountNumber: '3782 822463 55121', custodianId: 'c16', description: 'Travel & dining rewards card', type: 'CreditCard', currency: 'USD', opened: '2022-11-20', closed: null, archived: null, balance: -1128.40, deltaLabel: 'Statement due Nov 28', deltaDir: 'down', icon: 'credit_card', tone: 'violet' },
    { id: '4', name: 'Vanguard Brokerage', number: '·VBR9', accountNumber: 'VBR9 0042 1188', custodianId: 'c17', description: 'Long-term index fund portfolio', type: 'InvestmentAccount', currency: 'USD', opened: '2019-01-09', closed: null, archived: null, balance: 62412.88, deltaLabel: '+1.2% MTD', deltaDir: 'up',   icon: 'trending_up', tone: 'mint' },
    { id: '5', name: 'Citi Auto Loan', number: '·LN03', accountNumber: 'LN03 7788 2210', custodianId: 'c13', description: 'Fixed-rate 60-month auto loan', type: 'CarLoan', currency: 'USD', opened: '2023-06-01', closed: null, archived: null, balance: -14820.00, deltaLabel: '$285 due Dec 1', deltaDir: 'down', icon: 'directions_car', tone: 'coral' },
    { id: '7', name: 'Maple St Residence', number: '·PROP', accountNumber: 'PROP 0451 2290', description: 'Primary home — appraised value', type: 'Property', currency: 'USD', opened: '2018-09-05', closed: null, archived: null, balance: 685000.00, deltaLabel: '+2.1% YoY est.', deltaDir: 'up', icon: 'home', tone: 'sea' },
    { id: '6', name: 'Old Wells Checking', number: '·0098', accountNumber: '0098 4421 7700', custodianId: 'c19', description: 'Closed — migrated to Chase Checking', type: 'CheckingAccount', currency: 'USD', opened: '2016-04-22', closed: '2021-03-10', archived: null, balance: 0.00, deltaLabel: 'No activity', deltaDir: 'flat', icon: 'account_balance', tone: 'tide' },
  ],

  /* Canonical account-type registry — single source of truth for label, group
     (asset|liability), Material icon, and the fixed icon color (oklch foreground
     + soft tinted background). Ordered assets-first, then liabilities. */
  accountTypes: [
    // ---- Assets ----
    { key: 'Cash',              label: 'Cash',               group: 'asset',     icon: 'payments',                color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
    { key: 'CheckingAccount',   label: 'Checking',           group: 'asset',     icon: 'account_balance',         color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
    { key: 'SavingsAccount',    label: 'Savings',            group: 'asset',     icon: 'savings',                 color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
    { key: 'InvestmentAccount', label: 'Investment',         group: 'asset',     icon: 'trending_up',             color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
    { key: 'PensionAccount',    label: 'Pension',            group: 'asset',     icon: 'elderly',                 color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
    { key: 'Property',          label: 'Property',           group: 'asset',     icon: 'home',                    color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)' },
    { key: 'Vehicle',           label: 'Vehicle',            group: 'asset',     icon: 'directions_car',          color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)' },
    { key: 'OtherAsset',        label: 'Other asset',        group: 'asset',     icon: 'category',                color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
    // ---- Liabilities ----
    { key: 'CreditCard',        label: 'Credit card',        group: 'liability', icon: 'credit_card',             color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
    { key: 'Mortgage',          label: 'Mortgage',           group: 'liability', icon: 'home_work',               color: 'oklch(0.77 0.14 55)',  soft: 'oklch(0.77 0.14 55 / 0.16)' },
    { key: 'StudentLoan',       label: 'Student loan',       group: 'liability', icon: 'school',                  color: 'oklch(0.79 0.14 78)',  soft: 'oklch(0.79 0.14 78 / 0.16)' },
    { key: 'PersonalLoan',      label: 'Personal loan',      group: 'liability', icon: 'account_balance_wallet',  color: 'oklch(0.72 0.16 8)',   soft: 'oklch(0.72 0.16 8 / 0.16)' },
    { key: 'CarLoan',           label: 'Car loan',           group: 'liability', icon: 'directions_car',          color: 'oklch(0.75 0.15 38)',  soft: 'oklch(0.75 0.15 38 / 0.16)' },
    { key: 'TaxDebt',           label: 'Tax debt',           group: 'liability', icon: 'receipt_long',            color: 'oklch(0.71 0.17 352)', soft: 'oklch(0.71 0.17 352 / 0.16)' },
    { key: 'OtherLiability',    label: 'Other liability',    group: 'liability', icon: 'category',                color: 'oklch(0.66 0.03 30)',  soft: 'oklch(0.66 0.03 30 / 0.16)' },
  ],

  /* Canonical file-type registries — single source of truth for the file-type
     enums' label, Material icon, and fixed icon color (oklch foreground + soft tint).
     Files attach in THREE contexts, each with its own enum:
       • accountFileTypes  — Odyssey.Finance.Dtos/AccountFileType (field FileType on
         ExistingAccountFile): Other(0) · Message(1) · Statement(2) · Contract(3) ·
         Tax(4) · Documentation(5) · InsurancePolicy(6) · LoanAgreement(7) ·
         RepaymentSchedule(8) · PurchaseAgreement(9) · Valuation(10) · Warranty(11) ·
         Registration(12) · Prospectus(13). The 6–13 block was added to cover the
         documents that property / vehicle / loan / investment accounts need.
       • transactionFileTypes — Odyssey.Finance.Dtos/TransactionFileType (field Type on
         ExistingTransactionFile): Receipt(0) · Invoice(1) · Other(2) · CreditNote(3) ·
         Quote(4) · PaymentConfirmation(5) · Documentation(6).
       • taxStatementFileTypes — Odyssey.Finance.Dtos/TaxStatementFileType (field FileType
         on TaxStatementFile, newly added): TaxReturn(0) · TaxAssessment(1) ·
         SupportingDocument(2) · Other(3).
     Each list is in enum order with `Other` (the default in each) pulled last. The
     enums carry no icon/color — those are a design-system decision, defined here so
     every surface renders a kind identically. Hues share the categorical band with
     accountTypes & contactTypes so all the registries read as one family. */
  accountFileTypes: [
    { key: 'Message',           label: 'Message',            enumValue: 1,  icon: 'mail',              color: 'oklch(0.76 0.13 225)',  soft: 'oklch(0.76 0.13 225 / 0.16)',  desc: 'Saved correspondence — an emailed notice or letter from the institution.' },
    { key: 'Statement',         label: 'Statement',          enumValue: 2,  icon: 'description',       color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)', desc: 'A periodic account statement. The only type eligible for analysis.' },
    { key: 'Contract',          label: 'Contract',           enumValue: 3,  icon: 'history_edu',       color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.16)',  desc: 'A signed agreement — loan terms, an account-opening or deposit form.' },
    { key: 'Tax',               label: 'Tax',                enumValue: 4,  icon: 'request_quote',     color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.16)',  desc: 'A tax document — a 1099, 1098, or year-end summary.' },
    { key: 'Documentation',     label: 'Documentation',      enumValue: 5,  icon: 'menu_book',         color: 'oklch(0.77 0.14 110)',  soft: 'oklch(0.77 0.14 110 / 0.16)',  desc: 'Reference material — a manual, guide, policy booklet, or product documentation.' },
    { key: 'InsurancePolicy',   label: 'Insurance policy',   enumValue: 6,  icon: 'shield',            color: 'oklch(0.74 0.15 30)',   soft: 'oklch(0.74 0.15 30 / 0.16)',   desc: 'Insurance coverage — home, contents, auto. Carries a policy period (Valid from / to).' },
    { key: 'LoanAgreement',     label: 'Loan agreement',     enumValue: 7,  icon: 'gavel',             color: 'oklch(0.72 0.15 265)',  soft: 'oklch(0.72 0.15 265 / 0.16)',  desc: 'The original loan or credit agreement for a mortgage, student, personal, or car loan.' },
    { key: 'RepaymentSchedule', label: 'Repayment schedule', enumValue: 8,  icon: 'event_repeat',      color: 'oklch(0.78 0.14 160)',  soft: 'oklch(0.78 0.14 160 / 0.16)',  desc: 'An amortization plan — the schedule of instalments over the life of a loan.' },
    { key: 'PurchaseAgreement', label: 'Purchase agreement', enumValue: 9,  icon: 'sell',              color: 'oklch(0.79 0.14 60)',   soft: 'oklch(0.79 0.14 60 / 0.16)',   desc: 'The purchase & sale contract for a property, vehicle, or other asset.' },
    { key: 'Valuation',         label: 'Valuation',          enumValue: 10, icon: 'price_check',       color: 'oklch(0.80 0.15 140)',  soft: 'oklch(0.80 0.15 140 / 0.16)',  desc: 'A professional valuation or appraisal report of an asset.' },
    { key: 'Warranty',          label: 'Warranty',           enumValue: 11, icon: 'verified',          color: 'oklch(0.77 0.13 205)',  soft: 'oklch(0.77 0.13 205 / 0.16)',  desc: 'A manufacturer or extended warranty. Usually carries an expiry (Valid to).' },
    { key: 'Registration',      label: 'Registration',       enumValue: 12, icon: 'app_registration',  color: 'oklch(0.74 0.15 310)',  soft: 'oklch(0.74 0.15 310 / 0.16)',  desc: 'A registration certificate — vehicle registration, deed, or title document.' },
    { key: 'Prospectus',        label: 'Prospectus',         enumValue: 13, icon: 'auto_stories',      color: 'oklch(0.78 0.14 95)',   soft: 'oklch(0.78 0.14 95 / 0.16)',   desc: 'A fund prospectus or KID for an investment or pension account.' },
    { key: 'Other',             label: 'Other',              enumValue: 0,  icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)',  desc: 'The enum default — anything that does not fit the categories above.' },
  ],
  transactionFileTypes: [
    { key: 'Receipt',             label: 'Receipt',              enumValue: 0, icon: 'receipt_long',      color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)', desc: 'A purchase receipt — the proof-of-payment attached to a transaction.' },
    { key: 'Invoice',             label: 'Invoice',              enumValue: 1, icon: 'receipt',           color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)',  desc: 'A bill or invoice the transaction settles.' },
    { key: 'CreditNote',          label: 'Credit note',          enumValue: 3, icon: 'assignment_return', color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)',  desc: 'A refund or credit memo issued against an earlier charge.' },
    { key: 'Quote',               label: 'Quote',                enumValue: 4, icon: 'format_quote',      color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'A pre-invoice quotation or estimate.' },
    { key: 'PaymentConfirmation', label: 'Payment confirmation', enumValue: 5, icon: 'price_check',       color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)', desc: 'A bank transfer or payment confirmation slip.' },
    { key: 'Documentation',       label: 'Documentation',        enumValue: 6, icon: 'menu_book',         color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)', desc: 'General supporting documentation for the transaction.' },
    { key: 'Other',               label: 'Other',                enumValue: 2, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The enum default — any other supporting document.' },
  ],
  taxStatementFileTypes: [
    { key: 'TaxReturn',          label: 'Tax return',          enumValue: 0, icon: 'assignment',        color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)', desc: 'The filed tax return for the fiscal year.' },
    { key: 'TaxAssessment',      label: 'Tax assessment',      enumValue: 1, icon: 'fact_check',        color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'The authority’s assessment / notice of the final settled figures.' },
    { key: 'SupportingDocument', label: 'Supporting document', enumValue: 2, icon: 'attach_file',       color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)', desc: 'Backing material — receipts, deduction evidence, schedules.' },
    { key: 'Other',              label: 'Other',               enumValue: 3, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The enum default — anything that does not fit the categories above.' },
  ],

  /* Canonical insurance-policy-type registry — single source of truth for the
     InsurancePolicyType enum's label, Material icon, and fixed icon color (oklch
     foreground + soft tint). Ordered to match the enum declaration
     (Odyssey.Finance.Context/InsurancePolicyType); `Other` is the entity default
     and always sorts last. The enum carries no icon/color — those are a
     design-system decision, defined here so every surface (policy avatar, type
     chip, picker) renders a type identically. Hues sit in the shared categorical
     band with accountTypes / contactTypes / fileTypes so all the registries
     read as one family; brand tide stays out of it. */
  insurancePolicyTypes: [
    { key: 'Home',      label: 'Home',           enumValue: 0,  icon: 'house',              color: 'oklch(0.72 0.14 255)',  soft: 'oklch(0.72 0.14 255 / 0.16)', desc: 'Buildings + contents cover for a primary residence.' },
    { key: 'Contents',  label: 'Contents',       enumValue: 1,  icon: 'chair',              color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'Belongings and household contents, often a rider on a home policy.' },
    { key: 'Building',  label: 'Building',       enumValue: 2,  icon: 'apartment',          color: 'oklch(0.76 0.13 225)',  soft: 'oklch(0.76 0.13 225 / 0.16)', desc: 'Structure-only cover for a building or dwelling.' },
    { key: 'Vehicle',   label: 'Vehicle',        enumValue: 3,  icon: 'directions_car',     color: 'oklch(0.78 0.14 170)',  soft: 'oklch(0.78 0.14 170 / 0.16)', desc: 'Motor cover — car, motorcycle, or other vehicle.' },
    { key: 'Travel',    label: 'Travel',         enumValue: 4,  icon: 'flight',             color: 'oklch(0.77 0.13 205)',  soft: 'oklch(0.77 0.13 205 / 0.16)', desc: 'Single-trip or annual multi-trip travel cover.' },
    { key: 'Life',      label: 'Life',           enumValue: 5,  icon: 'favorite',           color: 'oklch(0.72 0.16 8)',    soft: 'oklch(0.72 0.16 8 / 0.16)',   desc: 'Term or whole-of-life assurance paying a death benefit.' },
    { key: 'Health',    label: 'Health',         enumValue: 6,  icon: 'health_and_safety',  color: 'oklch(0.80 0.15 150)',  soft: 'oklch(0.80 0.15 150 / 0.16)', desc: 'Private medical / health cover.' },
    { key: 'Accident',  label: 'Accident',       enumValue: 7,  icon: 'personal_injury',    color: 'oklch(0.79 0.14 60)',   soft: 'oklch(0.79 0.14 60 / 0.16)',  desc: 'Personal-accident / income-protection cover.' },
    { key: 'Liability', label: 'Liability',      enumValue: 8,  icon: 'gavel',              color: 'oklch(0.72 0.15 265)',  soft: 'oklch(0.72 0.15 265 / 0.16)', desc: 'Third-party / public / professional liability cover.' },
    { key: 'Pet',       label: 'Pet',            enumValue: 9,  icon: 'pets',               color: 'oklch(0.79 0.14 78)',   soft: 'oklch(0.79 0.14 78 / 0.16)',  desc: 'Veterinary and pet-health cover.' },
    { key: 'Property',  label: 'Property',       enumValue: 10, icon: 'home_work',          color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.16)', desc: 'Cover for a secondary property — cabin, rental, or plot.' },
    { key: 'Other',     label: 'Other',          enumValue: 11, icon: 'shield',             color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The entity default — anything outside the categories above.' },
  ],

  /* Canonical policy-file-type registry — the PolicyFileType enum's label, icon
     and color. Files attach to a policy AND to an individual renewal; both use
     this one vocabulary. Enum order with `Other` (the default) pulled last. */
  policyFileTypes: [
    { key: 'Contract',           label: 'Contract',             enumValue: 0, icon: 'history_edu',       color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'The signed insurance contract / schedule of cover.' },
    { key: 'Invoice',            label: 'Invoice',              enumValue: 1, icon: 'receipt',           color: 'oklch(0.80 0.13 85)',   soft: 'oklch(0.80 0.13 85 / 0.16)',  desc: 'A premium invoice or payment confirmation.' },
    { key: 'TermsAndConditions', label: 'Terms & conditions',   enumValue: 2, icon: 'menu_book',         color: 'oklch(0.77 0.14 110)',  soft: 'oklch(0.77 0.14 110 / 0.16)', desc: 'The policy wording — terms, conditions, and exclusions.' },
    { key: 'PolicyDocument',     label: 'Policy document',      enumValue: 3, icon: 'shield',            color: 'oklch(0.72 0.16 282)',  soft: 'oklch(0.72 0.16 282 / 0.16)', desc: 'The headline policy document / certificate of insurance.' },
    { key: 'ClaimDocument',      label: 'Claim document',       enumValue: 4, icon: 'assignment_late',   color: 'oklch(0.72 0.16 22)',   soft: 'oklch(0.72 0.16 22 / 0.16)',  desc: 'Documents relating to a claim filed against the policy.' },
    { key: 'Other',              label: 'Other',                enumValue: 5, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)', desc: 'The enum default — anything outside the categories above.' },
  ],

  // AccountFile collection — keyed by accountId. Each file mirrors ExistingAccountFile:
  //   { id, name, kind, size, uploaded }  plus the optional validity metadata added
  //   on the join entity — validFrom / validTo (e.g. a policy period or warranty
  //   window), issuedAt (when the document was signed/issued) and issuedBy (a
  //   Contact id — the issuing institution). All four are nullable, so older
  //   attachments simply omit them.
  accountFiles: {
    '1': [
      { id: 'f1', name: 'bank_statement_january_2026.pdf', kind: 'Statement', size: '4.4 KB', uploaded: '2026-05-22' },
      { id: 'f2', name: 'direct_deposit_form.pdf',         kind: 'Contract',  size: '88 KB',  uploaded: '2026-04-03' },
      { id: 'f3', name: 'costco_receipt_1123.jpg',         kind: 'Other',     size: '1.2 MB', uploaded: '2026-05-18' },
    ],
    '3': [
      { id: 'f4', name: 'amex_statement_october.pdf', kind: 'Statement', size: '512 KB', uploaded: '2026-05-01' },
    ],
    '4': [
      { id: 'f5', name: 'q1_brokerage_summary.pdf', kind: 'Statement', size: '320 KB', uploaded: '2026-04-12' },
      { id: 'f6', name: '1099_div_2025.pdf',        kind: 'Tax',       size: '96 KB',  uploaded: '2026-02-09' },
      { id: 'f7', name: 'investor_handbook.pdf',     kind: 'Documentation', size: '1.8 MB', uploaded: '2026-03-15' },
      { id: 'f8', name: 'core_index_fund_prospectus.pdf', kind: 'Prospectus', size: '2.1 MB', uploaded: '2026-01-30', issuedAt: '2026-01-01', issuedBy: null },
    ],
    // Citi Auto Loan (liability) — the loan paperwork, with the agreement's term as a validity window.
    '5': [
      { id: 'f9',  name: 'auto_loan_agreement.pdf',   kind: 'LoanAgreement',     size: '264 KB', uploaded: '2023-06-02', validFrom: '2023-06-01', validTo: '2028-06-01', issuedAt: '2023-06-01', issuedBy: 'c13' },
      { id: 'f10', name: 'repayment_schedule.pdf',    kind: 'RepaymentSchedule', size: '142 KB', uploaded: '2023-06-02', validFrom: '2023-06-01', validTo: '2028-06-01', issuedAt: '2023-06-01', issuedBy: 'c13' },
    ],
    // Maple St Residence (Property) — the document set a home needs: insurance, the
    // purchase contract, a recent valuation, and the title/deed registration.
    '7': [
      { id: 'f11', name: 'home_insurance_policy_2026.pdf', kind: 'InsurancePolicy',   size: '410 KB', uploaded: '2025-12-18', validFrom: '2026-01-01', validTo: '2026-12-31', issuedAt: '2025-12-15', issuedBy: 'c12' },
      { id: 'f12', name: 'purchase_agreement.pdf',          kind: 'PurchaseAgreement', size: '1.1 MB', uploaded: '2018-09-06', issuedAt: '2018-09-05', issuedBy: null },
      { id: 'f13', name: 'appraisal_report_2025.pdf',       kind: 'Valuation',         size: '780 KB', uploaded: '2025-11-22', issuedAt: '2025-11-20', issuedBy: null },
      { id: 'f14', name: 'property_deed.pdf',               kind: 'Registration',      size: '320 KB', uploaded: '2018-09-06', issuedAt: '2018-09-05', issuedBy: null },
    ],
  },

  // TransactionTag — Name (≤64), Description (≤256), Archived (datetime?, null = active).
  // A transaction now carries a *set* of these (many-to-many), so a purchase can be
  // both a category (Groceries) and a cross-cutting tag (Reimbursable / Business).
  tags: [
    { id: 't1', name: 'Groceries',     description: 'Supermarkets, food shops, and weekly stock-ups', archived: null },
    { id: 't2', name: 'Subscriptions', description: 'Recurring streaming, software, and memberships',  archived: null },
    { id: 't3', name: 'Transit',       description: 'Public transit, rideshare, and fuel',             archived: null },
    { id: 't4', name: 'Rent',          description: 'Monthly housing payments',                        archived: null },
    { id: 't5', name: 'Income',        description: 'Salary, refunds, interest, and inbound payments',  archived: null },
    { id: 't6', name: 'Dining',        description: 'Restaurants, cafés, and bars',                     archived: null },
    { id: 't7', name: 'Utilities',     description: 'Electricity, water, gas, and internet',            archived: null },
    { id: 't9', name: 'Reimbursable',  description: 'Expensable — to be claimed back from work or a peer', archived: null },
    { id: 't10', name: 'Business',     description: 'Work-related spending, tracked for the books',     archived: null },
    { id: 't8', name: 'Vacation 2024', description: 'One-off travel spending from the 2024 trips',      archived: '2025-01-08T09:00:00Z' },
  ],

  /* Canonical contact-type registry — single source of truth for the
     ContactType enum's label, Material icon, and fixed icon color (oklch
     foreground + soft tinted background). Ordered to match the enum declaration
     (Odyssey.Finance.Dtos/ContactType.cs); `Other` is the DTO default and
     always sorts last. The enum carries no icon/color — those are a design-system
     decision, defined here so every surface (table avatar, type chip, picker)
     renders a type identically. Hues share the categorical chroma/lightness band
     with accountTypes so the two registries read as one visual family. */
  contactTypes: [
    { key: 'Person',       label: 'Person',       icon: 'person',         color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)', desc: 'An individual — a friend, landlord, employer contact, or contractor money moves to or from.' },
    { key: 'Organization', label: 'Organization', icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)', desc: 'Any entity that is not a person — a merchant, company, bank, utility, insurer, charity, or institution (v5: the old Merchant/Company/Institution/Other values collapse here).' },
  ],

  // Contact — Name (≤128), NormalizedName (≤128, server-derived UPPER+trim),
  // Type (Merchant|Person|Organization|Company|Institution|Other),
  // Description (≤1024), Archived (datetime?, null = active).
  contacts: [
    { id: 'c1',  name: 'Whole Foods Market',        normalizedName: 'WHOLE FOODS MARKET',        type: 'Organization',     description: 'Grocery chain — Mission & SoMa locations.',    archived: null },
    { id: 'c2',  name: 'ACME Co Payroll',           normalizedName: 'ACME CO PAYROLL',           type: 'Organization',      orgNumber: '98-7654321',  description: 'Employer payroll direct deposit.',             archived: null },
    { id: 'c3',  name: 'Spotify',                   normalizedName: 'SPOTIFY',                   type: 'Organization',      description: 'Music streaming subscription.',                archived: null },
    { id: 'c4',  name: 'Uber',                      normalizedName: 'UBER',                      type: 'Organization',      description: 'Rideshare trips and Uber Eats.',               archived: null },
    { id: 'c5',  name: 'PG&E',                      normalizedName: 'PG&E',                      type: 'Organization',  orgNumber: '94-0742640',  description: 'Pacific Gas & Electric — utilities.',          archived: null },
    { id: 'c6',  name: 'Delta Air Lines',           normalizedName: 'DELTA AIR LINES',           type: 'Organization',      description: 'Air travel, fares, and refunds.',              archived: null },
    { id: 'c7',  name: 'Lakeside Property Mgmt',    normalizedName: 'LAKESIDE PROPERTY MGMT',    type: 'Organization', orgNumber: '81-2233445',  description: 'Apartment landlord — monthly rent.',           archived: null },
    { id: 'c8',  name: 'Costco Wholesale',          normalizedName: 'COSTCO WHOLESALE',          type: 'Organization',     description: 'Warehouse club — bulk groceries.',             archived: null },
    { id: 'c9',  name: 'Michael Chen',              normalizedName: 'MICHAEL CHEN',              type: 'Person',       description: 'Shared expenses and reimbursements.',          archived: null },
    { id: 'c10', name: 'Internal Revenue Service',  normalizedName: 'INTERNAL REVENUE SERVICE',  type: 'Organization',  description: 'Federal tax payments and refunds.',            archived: null },
    { id: 'c11', name: 'FitZone Gym',               normalizedName: 'FITZONE GYM',               type: 'Organization',     description: 'Cancelled membership — kept for history.',      archived: '2025-03-02T09:00:00Z' },
    { id: 'c12', name: 'Pacific Home Insurance',    normalizedName: 'PACIFIC HOME INSURANCE',    type: 'Organization',      orgNumber: '45-6677889',  description: 'Home & contents insurer — issues the property policy.', archived: null },
    { id: 'c13', name: 'Citi Lending',              normalizedName: 'CITI LENDING',             type: 'Organization',  description: 'Lender on the fixed-rate auto loan.',          archived: null },
    // ---- Insurers: contacts that issue insurance policies. Any
    //      contact type is eligible as an insurer; these are Companies /
    //      Institutions. Referenced by InsurancePolicy.InsurerId.
    { id: 'c20', name: 'Meridian Auto & Casualty', normalizedName: 'MERIDIAN AUTO & CASUALTY', type: 'Organization',     orgNumber: '52-1190034', description: 'Motor & casualty insurer — issues the vehicle policy.',          archived: null },
    { id: 'c21', name: 'Anchor Life Assurance',    normalizedName: 'ANCHOR LIFE ASSURANCE',    type: 'Organization',     orgNumber: '38-7741200', description: 'Life & term-assurance insurer.',                            archived: null },
    { id: 'c22', name: 'Polaris Travel Cover',     normalizedName: 'POLARIS TRAVEL COVER',     type: 'Organization',     orgNumber: '61-3398201', description: 'Travel & annual multi-trip insurer.',                       archived: null },
    { id: 'c23', name: 'Nordic Forsikring',        normalizedName: 'NORDIC FORSIKRING',        type: 'Organization', orgNumber: '99-1002345', description: 'Norwegian property & contents insurer (NOK / CHF policies).', archived: null },
    { id: 'c24', name: 'Evergreen Health Plan',    normalizedName: 'EVERGREEN HEALTH PLAN',    type: 'Organization',     orgNumber: '47-5567012', description: 'Private health & accident insurer.',                        archived: null },
    // ---- Custodians: the institutions that hold accounts (banks / brokers /
    //      card issuers). Any contact type is eligible as a custodian; these
    //      happen to be Institutions. Wells Fargo is archived to exercise the
    //      CustodianArchived chip state.
    { id: 'c14', name: 'JPMorgan Chase',            normalizedName: 'JPMORGAN CHASE',            type: 'Organization',  orgNumber: '13-2624428',  description: 'Retail bank — holds the everyday checking account.', archived: null },
    { id: 'c15', name: 'Ally Bank',                 normalizedName: 'ALLY BANK',                 type: 'Organization',  orgNumber: '57-0001234',  description: 'Online bank — holds the high-yield savings.',  archived: null },
    { id: 'c16', name: 'American Express',          normalizedName: 'AMERICAN EXPRESS',          type: 'Organization',  orgNumber: '13-4922250',  description: 'Card issuer — holds the Platinum card.',       archived: null },
    { id: 'c17', name: 'Vanguard',                  normalizedName: 'VANGUARD',                  type: 'Organization',  orgNumber: '23-2868925',  description: 'Brokerage — custodies the index-fund portfolio.', archived: null },
    { id: 'c19', name: 'Wells Fargo',               normalizedName: 'WELLS FARGO',               type: 'Organization',  orgNumber: '94-1347393',  description: 'Former bank — held the closed checking account.', archived: '2021-03-12T00:00:00Z' },
  ],

  // Currency — CurrencyCode (3, PK/ISO-4217), Name (≤64), MinorUnits (0–12),
  // Symbol (≤8), Archived (datetime?, null = active). USD is the workspace base.
  currencies: [
    { code: 'USD', name: 'US Dollar',       symbol: '$',  minorUnits: 2, base: true,  archived: null },
    { code: 'EUR', name: 'Euro',            symbol: '€',  minorUnits: 2, base: false, archived: null },
    { code: 'GBP', name: 'British Pound',   symbol: '£',  minorUnits: 2, base: false, archived: null },
    { code: 'NOK', name: 'Norwegian Krone', symbol: 'kr', minorUnits: 2, base: false, archived: null },
    { code: 'SEK', name: 'Swedish Krona',   symbol: 'kr', minorUnits: 2, base: false, archived: null },
    { code: 'JPY', name: 'Japanese Yen',    symbol: '¥',  minorUnits: 0, base: false, archived: null },
    { code: 'CAD', name: 'Canadian Dollar', symbol: '$',  minorUnits: 2, base: false, archived: null },
    { code: 'CHF', name: 'Swiss Franc',     symbol: 'Fr', minorUnits: 2, base: false, archived: '2025-02-19T09:00:00Z' },
  ],

  // ExchangeRate — append-only & timestamped. A new rate never overwrites an old
  // one; conversions use the latest AsOf for a (From, To) pair. Rate = units of To
  // per 1 unit of From. CreatedAt is the server-set audit insertion time.
  exchangeRates: [
    { id: 'r1',  from: 'USD', to: 'EUR', rate: 0.9218, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r2',  from: 'USD', to: 'GBP', rate: 0.7891, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r3',  from: 'USD', to: 'NOK', rate: 10.612, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r4',  from: 'USD', to: 'SEK', rate: 10.498, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r5',  from: 'USD', to: 'JPY', rate: 157.32, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r6',  from: 'USD', to: 'CAD', rate: 1.3718, asOf: '2026-06-05T09:00:00Z', createdAt: '2026-06-05T09:00:12Z' },
    { id: 'r7',  from: 'USD', to: 'EUR', rate: 0.9203, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r8',  from: 'USD', to: 'GBP', rate: 0.7902, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r9',  from: 'USD', to: 'NOK', rate: 10.588, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r10', from: 'USD', to: 'SEK', rate: 10.471, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r11', from: 'USD', to: 'JPY', rate: 157.04, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r12', from: 'USD', to: 'CAD', rate: 1.3702, asOf: '2026-06-03T09:00:00Z', createdAt: '2026-06-03T09:00:09Z' },
    { id: 'r13', from: 'USD', to: 'EUR', rate: 0.9187, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
    { id: 'r14', from: 'USD', to: 'GBP', rate: 0.7885, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
    { id: 'r15', from: 'USD', to: 'NOK', rate: 10.640, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
    { id: 'r16', from: 'USD', to: 'SEK', rate: 10.512, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
    { id: 'r17', from: 'USD', to: 'JPY', rate: 156.61, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
    { id: 'r18', from: 'USD', to: 'CAD', rate: 1.3735, asOf: '2026-06-01T09:00:00Z', createdAt: '2026-06-01T09:00:07Z' },
  ],

  // Transactions — each carries a SET of tags (`tags: string[]`, many-to-many with
  // TransactionTag), replacing the old single `tag`. Most are single-tagged (one
  // category); several carry a category + a cross-cutting tag (e.g. Groceries +
  // Reimbursable) to exercise the multi-tag UI. An empty array means "no tags".
  transactions: [
    { id: 'x1',  date: '2024-11-23', desc: 'Whole Foods Market · Mission',  account: '1', tags: ['t1', 't9'], contact: 'c1', currency: 'USD', externalId: 'WF-10472-1123', amount:  -128.40, status: 'Approved', icon: 'shopping_cart', dir: 'expense', files: [
      { id: 'tf-x1a', name: 'wholefoods_receipt_1123.jpg', kind: 'Receipt', size: '1.2 MB', uploaded: '2024-11-23' },
    ] },
    { id: 'x2',  date: '2024-11-22', desc: 'ACME Co · Payroll',             account: '1', tags: ['t5'], contact: 'c2', currency: 'USD', amount:  3250.00, status: 'Approved', icon: 'arrow_downward', dir: 'income' },
    { id: 'x3',  date: '2024-11-22', desc: 'Spotify · Monthly',             account: '3', tags: ['t2'], contact: 'c3', currency: 'USD', amount:    -9.99, status: 'Approved', icon: 'subscriptions',  dir: 'expense' },
    { id: 'x4',  date: '2024-11-21', desc: 'Uber · Trip to airport',        account: '3', tags: ['t3', 't9', 't10'], contact: 'c4', currency: 'USD', amount:   -24.18, status: 'New',      icon: 'local_taxi',     dir: 'expense' },
    { id: 'x5',  date: '2024-11-20', desc: 'PG&E · Electric',               account: '1', tags: ['t7'], contact: 'c5', currency: 'USD', statusComment: 'Verified against November statement.', amount:  -112.75, status: 'Approved', icon: 'bolt',           dir: 'expense', files: [
      { id: 'tf-x5a', name: 'pge_bill_november.pdf', kind: 'Invoice', size: '212 KB', uploaded: '2024-11-20' },
      { id: 'tf-x5b', name: 'autopay_confirmation.pdf', kind: 'Other', size: '64 KB', uploaded: '2024-11-21' },
    ] },
    { id: 'x6',  date: '2024-11-18', desc: 'Delta Air · Refund',            account: '3', tags: ['t5'], amount:   118.40, status: 'Approved', icon: 'flight',         dir: 'income' },
    { id: 'x7',  date: '2024-11-17', desc: 'Tartine Bakery',                account: '3', tags: ['t6', 't9'], amount:   -18.50, status: 'Flagged',  icon: 'restaurant',     dir: 'expense' },
    { id: 'x8',  date: '2024-11-15', desc: 'Lakeside Property Mgmt · Rent', account: '1', tags: ['t4'], amount: -2400.00, status: 'Approved', icon: 'home',           dir: 'expense' },
    { id: 'x9',  date: '2024-11-14', desc: 'Interest · Ally',               account: '2', tags: ['t5'], amount:     4.12, status: 'Approved', icon: 'savings',        dir: 'income' },
    { id: 'x10', date: '2024-11-13', desc: 'Costco Wholesale',              account: '3', tags: ['t1'], amount:   -84.20, status: 'New',      icon: 'shopping_cart',  dir: 'expense' },
    // ---- December 2024 (current month, partially spent) ----
    { id: 'x11', date: '2024-12-01', desc: 'ACME Co · Payroll',             account: '1', tags: ['t5'], amount:  3250.00, status: 'Approved', icon: 'arrow_downward', dir: 'income'  },
    { id: 'x12', date: '2024-12-02', desc: 'Lakeside Property Mgmt · Rent', account: '1', tags: ['t4'], amount: -2400.00, status: 'Approved', icon: 'home',           dir: 'expense' },
    { id: 'x13', date: '2024-12-03', desc: 'Whole Foods Market · Mission',  account: '1', tags: ['t1'], amount:   -96.30, status: 'Approved', icon: 'shopping_cart',  dir: 'expense' },
    { id: 'x14', date: '2024-12-05', desc: 'Spotify · Monthly',             account: '3', tags: ['t2'], amount:    -9.99, status: 'Approved', icon: 'subscriptions',  dir: 'expense' },
    { id: 'x15', date: '2024-12-06', desc: 'PG&E · Electric',               account: '1', tags: ['t7'], amount:  -104.20, status: 'New',      icon: 'bolt',           dir: 'expense' },
    // ---- October 2024 (closed period — feeds the archived budget) ----
    { id: 'x16', date: '2024-10-25', desc: 'ACME Co · Payroll',             account: '1', tags: ['t5'], amount:  3250.00, status: 'Approved', icon: 'arrow_downward', dir: 'income'  },
    { id: 'x17', date: '2024-10-15', desc: 'Lakeside Property Mgmt · Rent', account: '1', tags: ['t4'], amount: -2400.00, status: 'Approved', icon: 'home',           dir: 'expense' },
    { id: 'x18', date: '2024-10-10', desc: 'Costco · Monthly stock-up',     account: '1', tags: ['t1'], amount:  -540.10, status: 'Approved', icon: 'shopping_cart',  dir: 'expense' },
    { id: 'x19', date: '2024-10-20', desc: 'PG&E · Electric',               account: '1', tags: ['t7'], amount:  -168.00, status: 'Approved', icon: 'bolt',           dir: 'expense' },
    { id: 'x20', date: '2024-10-12', desc: 'Nopa · Dinner party',           account: '3', tags: ['t6', 't9'], amount:  -210.40, status: 'Approved', icon: 'restaurant',     dir: 'expense' },
    { id: 'x21', date: '2024-10-08', desc: 'BART · Monthly pass',           account: '3', tags: ['t3'], amount:   -88.00, status: 'Approved', icon: 'local_taxi',     dir: 'expense' },
  ],

  // Budgets — ExistingBudget[] (BudgetsCard list) + each budget's ExistingBudgetItem[]
  // (BudgetCard detail). An item's `actual` is NOT stored: it's derived from the
  // transactions whose tag matches the item's `tagId` within the budget's date range
  // — exactly how the server's BudgetReport computes per-tag sums. Items with no
  // tagId are plan-only (no matched actual), which the real TransactionTagId allows.
  budgets: [
    {
      id: 'b1', name: 'November 2024', description: 'Primary monthly household budget.',
      currency: 'USD', startDate: '2024-11-01', endDate: '2024-11-30', archived: null,
      icon: 'pie_chart', tone: 'tide',
      items: [
        { id: 'bi1', name: 'Salary',        description: 'Base monthly pay',        categoryType: 'Income',  tagId: 't5', planned: 3250 },
        { id: 'bi2', name: 'Side projects', description: 'Freelance & consulting',   categoryType: 'Income',  tagId: null, planned:  400 },
        { id: 'bi3', name: 'Rent',          description: 'Lakeside apartment',       categoryType: 'Expense', tagId: 't4', planned: 2400 },
        { id: 'bi4', name: 'Groceries',     description: 'Weekly food shop',         categoryType: 'Expense', tagId: 't1', planned:  600 },
        { id: 'bi5', name: 'Utilities',     description: 'Electric, water, internet', categoryType: 'Expense', tagId: 't7', planned:  180 },
        { id: 'bi6', name: 'Subscriptions', description: 'Streaming & software',     categoryType: 'Expense', tagId: 't2', planned:   40 },
        { id: 'bi7', name: 'Transit',       description: 'Transit & rideshare',      categoryType: 'Expense', tagId: 't3', planned:  120 },
        { id: 'bi8', name: 'Dining out',    description: 'Restaurants & cafés',      categoryType: 'Expense', tagId: 't6', planned:  150 },
      ],
    },
    {
      id: 'b2', name: 'December 2024', description: 'Holiday season plan — extra room for gifts and travel.',
      currency: 'USD', startDate: '2024-12-01', endDate: '2024-12-31', archived: null,
      icon: 'pie_chart', tone: 'violet',
      items: [
        { id: 'bi9',  name: 'Salary',         description: 'Base monthly pay',     categoryType: 'Income',  tagId: 't5', planned: 3250 },
        { id: 'bi10', name: 'Year-end bonus',  description: 'Expected Q4 bonus',    categoryType: 'Income',  tagId: null, planned: 1500 },
        { id: 'bi11', name: 'Rent',            description: 'Lakeside apartment',   categoryType: 'Expense', tagId: 't4', planned: 2400 },
        { id: 'bi12', name: 'Groceries',       description: 'Holiday hosting',      categoryType: 'Expense', tagId: 't1', planned:  650 },
        { id: 'bi13', name: 'Utilities',       description: 'Higher winter usage',  categoryType: 'Expense', tagId: 't7', planned:  200 },
        { id: 'bi14', name: 'Subscriptions',   description: 'Streaming & software', categoryType: 'Expense', tagId: 't2', planned:   40 },
        { id: 'bi15', name: 'Holiday gifts',   description: 'Family & friends',     categoryType: 'Expense', tagId: null, planned:  800 },
        { id: 'bi16', name: 'Travel',          description: 'Flights home',         categoryType: 'Expense', tagId: null, planned:  600 },
      ],
    },
    {
      id: 'b3', name: 'October 2024', description: 'Pre-move budget — closed at month end.',
      currency: 'USD', startDate: '2024-10-01', endDate: '2024-10-31', archived: '2024-11-02T09:00:00Z',
      icon: 'pie_chart', tone: 'sea',
      items: [
        { id: 'bi17', name: 'Salary',     description: 'Base monthly pay',     categoryType: 'Income',  tagId: 't5', planned: 3250 },
        { id: 'bi18', name: 'Rent',       description: 'Lakeside apartment',   categoryType: 'Expense', tagId: 't4', planned: 2400 },
        { id: 'bi19', name: 'Groceries',  description: 'Monthly stock-up',     categoryType: 'Expense', tagId: 't1', planned:  500 },
        { id: 'bi20', name: 'Utilities',  description: 'Electric & water',     categoryType: 'Expense', tagId: 't7', planned:  180 },
        { id: 'bi21', name: 'Dining out', description: 'Restaurants & cafés',  categoryType: 'Expense', tagId: 't6', planned:  150 },
        { id: 'bi22', name: 'Transit',    description: 'Transit & rideshare',  categoryType: 'Expense', tagId: 't3', planned:  100 },
      ],
    },
  ],

};

/* ---------------------------------------------------------------------------
   File-analysis jobs — the "Analyze" feature on a statement file row.

   Shapes mirror Odyssey.Finance.Dtos exactly:
     • ExistingFileAnalysisJob  — Id, AccountFileId, Status, FileTypeDetected,
       StartedAt, CompletedAt, FailureCode/Message, AnalyzerProvider,
       AnalyzerModel, PromptVersion, Candidates[]
     • ExistingFileAnalysisCandidateTransaction — Id, TransactionDate,
       BookingDate?, Description, Merchant?, CategoryHint?, Amount, Currency,
       ExternalId?, ReferenceNumber?, LlmConfidence? (0–1), LlmModel?,
       ReviewStatus (Pending|Accepted|Rejected), ReviewedAt?

   Status enum: New|Queued|Running|Completed|Failed|Cancelled.
   The live provider config is Claude · claude-opus-4-7 (FileAnalysisOptions).
   Only files of type Statement can be analyzed (server throws otherwise). --- */

window.OdysseyData.analyzer = { provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0' };

// Hand-authored candidate sets keyed by the statement file id, so each statement
// returns a believable, distinct extraction. Amounts are signed (debits negative).
//
// AI matching (feature: merchant + category matching). After extraction, a SECOND
// LLM step compares each candidate's free-text merchant/category against the user's
// contact + tag NAMES and returns the best existing record per field with a
// confidence. Those raw returns live on the candidate as `match*` fields below; the
// review dialog applies FileAnalysis:Match:AutoLinkThreshold (0.60) to decide what
// is auto-linked vs. shown as a sub-threshold "suggested-but-not-linked" chip:
//   • matchContactId / matchContactConfidence  → Contact (0 or 1)
//   • matchTagIds / matchTagConfidence                   → TransactionTag (0..N)
// A null id / empty array = the model found no fit (returns "no match" rather than
// guess). The free-text merchant/categoryHint is KEPT either way (audit + display).
window.OdysseyData.analysisCandidates = {
  // Amex Platinum · October statement (credit card → mostly debits + one autopay credit)
  f4: [
    { id: 'ca1', transactionDate: '2024-10-02', bookingDate: '2024-10-03', description: 'WHOLEFDS MKT #10472 MISSION SF', merchant: 'Whole Foods Market', categoryHint: 'Groceries',     amount: -128.40, currency: 'USD', externalId: 'TXN-558201', referenceNumber: 'REF 8841-2207', llmConfidence: 0.98, matchContactId: 'c1', matchContactConfidence: 0.98, matchTagIds: ['t1'], matchTagConfidence: 0.96 },
    { id: 'ca2', transactionDate: '2024-10-04', bookingDate: null,         description: 'SPOTIFY P0F3A21 NEW YORK NY',     merchant: 'Spotify',            categoryHint: 'Subscriptions', amount:   -9.99, currency: 'USD', externalId: 'TXN-558244', referenceNumber: null,            llmConfidence: 0.99, matchContactId: 'c3', matchContactConfidence: 0.99, matchTagIds: ['t2'], matchTagConfidence: 0.95 },
    { id: 'ca3', transactionDate: '2024-10-07', bookingDate: '2024-10-08', description: 'DELTA AIR 0062371882 ATL',        merchant: 'Delta Air Lines',    categoryHint: 'Travel',        amount: -412.30, currency: 'USD', externalId: 'TXN-558310', referenceNumber: 'CONF JX42PQ',   llmConfidence: 0.95, matchContactId: 'c6', matchContactConfidence: 0.97, matchTagIds: ['t3'], matchTagConfidence: 0.46 },
    { id: 'ca4', transactionDate: '2024-10-09', bookingDate: '2024-10-10', description: 'UBER *TRIP HELP.UBER.COM',        merchant: 'Uber',               categoryHint: 'Transport',     amount:  -24.18, currency: 'USD', externalId: 'TXN-558377', referenceNumber: null,            llmConfidence: 0.92, matchContactId: 'c4', matchContactConfidence: 0.96, matchTagIds: ['t3'], matchTagConfidence: 0.82 },
    { id: 'ca5', transactionDate: '2024-10-12', bookingDate: '2024-10-14', description: 'NOPA RESTAURANT SAN FRANCISCO',   merchant: 'Nopa',               categoryHint: 'Dining',        amount: -210.40, currency: 'USD', externalId: 'TXN-558401', referenceNumber: 'REF 1190-3345', llmConfidence: 0.88, matchContactId: null, matchContactConfidence: null, matchTagIds: ['t6'], matchTagConfidence: 0.93 },
    { id: 'ca6', transactionDate: '2024-10-15', bookingDate: '2024-10-15', description: 'AUTOPAY PAYMENT - THANK YOU',     merchant: null,                 categoryHint: 'Payment',       amount: 1500.00, currency: 'USD', externalId: 'TXN-558455', referenceNumber: null,            llmConfidence: 0.97, matchContactId: null, matchContactConfidence: null, matchTagIds: [], matchTagConfidence: null },
    { id: 'ca7', transactionDate: '2024-10-19', bookingDate: null,         description: 'POS PURCHASE 0023421 TERM 04',    merchant: 'Unknown',            categoryHint: null,            amount:  -42.00, currency: 'USD', externalId: null,          referenceNumber: '0023421',       llmConfidence: 0.41, matchContactId: null, matchContactConfidence: null, matchTagIds: [], matchTagConfidence: null },
    { id: 'ca8', transactionDate: '2024-10-23', bookingDate: '2024-10-24', description: 'APPLE STORE R052 SAN FRANCISCO',  merchant: 'Apple',              categoryHint: 'Electronics',   amount:-1299.00, currency: 'USD', externalId: 'TXN-558529', referenceNumber: 'REF 7782-9910', llmConfidence: 0.90, matchContactId: null, matchContactConfidence: null, matchTagIds: [], matchTagConfidence: null },
    { id: 'ca9', transactionDate: '2024-10-28', bookingDate: '2024-10-29', description: 'DELTA AIR REFUND 0062371882',     merchant: 'Delta Air Lines',    categoryHint: 'Travel',        amount:  118.40, currency: 'USD', externalId: 'TXN-558588', referenceNumber: 'CONF JX42PQ',   llmConfidence: 0.86, matchContactId: 'c6', matchContactConfidence: 0.93, matchTagIds: ['t3'], matchTagConfidence: 0.43 },
  ],
  // Chase Checking · January statement (everyday account → payroll, rent, utilities)
  f1: [
    { id: 'cb1', transactionDate: '2026-01-02', bookingDate: '2026-01-02', description: 'ACME CO PAYROLL DIR DEP',         merchant: 'ACME Co',            categoryHint: 'Income',        amount: 3250.00, currency: 'USD', externalId: 'ACH-220114', referenceNumber: 'PPD 5521',      llmConfidence: 0.99, matchContactId: 'c2', matchContactConfidence: 0.88, matchTagIds: ['t5'], matchTagConfidence: 0.97 },
    { id: 'cb2', transactionDate: '2026-01-03', bookingDate: '2026-01-03', description: 'LAKESIDE PROPERTY MGMT RENT',      merchant: 'Lakeside Property',  categoryHint: 'Rent',          amount:-2400.00, currency: 'USD', externalId: 'ACH-220140', referenceNumber: 'WEB 88123',     llmConfidence: 0.96, matchContactId: 'c7', matchContactConfidence: 0.94, matchTagIds: ['t4'], matchTagConfidence: 0.95 },
    { id: 'cb3', transactionDate: '2026-01-06', bookingDate: null,         description: 'PG&E WEB PMT 9921',                merchant: 'PG&E',               categoryHint: 'Utilities',     amount: -112.75, currency: 'USD', externalId: null,          referenceNumber: '9921',          llmConfidence: 0.93, matchContactId: 'c5', matchContactConfidence: 0.97, matchTagIds: ['t7'], matchTagConfidence: 0.96 },
    { id: 'cb4', transactionDate: '2026-01-09', bookingDate: '2026-01-10', description: 'WHOLEFDS MKT #10472',              merchant: 'Whole Foods Market', categoryHint: 'Groceries',     amount:  -96.30, currency: 'USD', externalId: 'TXN-771204', referenceNumber: null,            llmConfidence: 0.91, matchContactId: 'c1', matchContactConfidence: 0.98, matchTagIds: ['t1'], matchTagConfidence: 0.95 },
    { id: 'cb5', transactionDate: '2026-01-14', bookingDate: '2026-01-14', description: 'CHECK 1043',                       merchant: null,                 categoryHint: null,            amount: -325.00, currency: 'USD', externalId: null,          referenceNumber: 'CHK 1043',      llmConfidence: 0.52, matchContactId: null, matchContactConfidence: null, matchTagIds: [], matchTagConfidence: null },
    { id: 'cb6', transactionDate: '2026-01-21', bookingDate: '2026-01-22', description: 'ALLY BANK TRANSFER',               merchant: 'Ally Bank',          categoryHint: 'Transfer',      amount: -500.00, currency: 'USD', externalId: 'ACH-220512', referenceNumber: null,            llmConfidence: 0.84, matchContactId: 'c15', matchContactConfidence: 0.90, matchTagIds: [], matchTagConfidence: null },
  ],
};

/* ---------------------------------------------------------------------------
   AI-match configuration (feature: merchant + category matching).
   Mirrors the FileAnalysis:Match:* config keys. Thresholds/caps are read by the
   review dialog when it turns raw match returns into linked values vs. chips. */
window.OdysseyData.matchConfig = {
  autoLinkThreshold: 0.60,  // ≥ → auto-linked (MatchMethod=Llm); < → suggestion chip only
  maxVocabulary: 500,       // per list (contacts, tags); over cap ⇒ match Skipped
  timeoutSeconds: 60,
};

/* Authorization claims the signed-in reviewer holds. The match/import flow is open
   to the `User` role, but `contacts.create` is NOT — so the merchant cell's
   inline "Create …" affordance is conditionally rendered on this claim (a User-role
   reviewer never meets a 403 on a happy-path control; server-side [Authorize] stays
   the real gate). Flip `role` to demo the gated vs. ungated combobox. */
window.OdysseyData.permissions = {
  role: 'Owner',  // Owner | Admin | User
  claims: { 'file-analysis.create': true, 'file-analysis.read': true, 'contacts.create': true },
};
// Role → which claims are present. User keeps analysis + read but loses create.
window.OdysseyData.setReviewerRole = (role) => {
  const create = role !== 'User';
  window.OdysseyData.permissions = {
    role,
    claims: { 'file-analysis.create': true, 'file-analysis.read': true, 'contacts.create': create },
  };
  return window.OdysseyData.permissions;
};
window.OdysseyData.can = (claim) => !!(window.OdysseyData.permissions.claims || {})[claim];

/* ---------------------------------------------------------------------------
   File-analysis privacy / consent (issue: third-party transfer of personal +
   financial documents). The "Analyze" flow opens on a consent gate before any
   bytes leave Odyssey. These constants drive the gate's disclosure copy AND the
   admin audit log, so the wording the user agreed to is exactly what's recorded.

   The whole file is sent (the feature needs the full statement to read it), so
   the design's job is informed, logged, per-document consent — not redaction. */
window.OdysseyData.analysisTransfer = {
  processor: 'Anthropic',
  processorRegion: 'United States',
  // The single line of consent the user must affirm. Recorded verbatim per call.
  // CORRECTED wording (v2): the line now states the contact/tag NAMES ride
  // along for matching, so the recorded ConsentText is factually accurate. The
  // version stamps which disclosure each job was consented under (GDPR Art. 5(2)
  // accountability) — old jobs keep their old text, never back-dated.
  consentText: 'I\u2019m authorized to share this document and consent to sending the complete file \u2014 plus my contact and tag names, for matching \u2014 to Anthropic\u2019s Claude API for analysis.',
  consentVersion: '2.0',
  // The precise one-line note about the matching vocabulary (names only).
  matchDisclosure: 'Your contact and tag names \u2014 names only, for matching. No notes, organization numbers, or other fields are sent.',
  // Lawful basis surfaced in the gate + log (GDPR Art. 6). Consent is the basis
  // captured here; the privacy notice documents the processor + transfer.
  lawfulBasis: 'Consent \u00b7 GDPR Art. 6(1)(a)',
  privacyNoticeUrl: 'https://www.anthropic.com/legal/privacy',
  dpaInPlace: true,
};

/* ---------------------------------------------------------------------------
   Runtime file-analysis settings (issue #439). The kill switch, the model and
   the provider base URL are admin-editable rows in the SystemSettings store,
   not deploy-time configuration — so every surface that depends on them reads
   THIS object rather than a compiled constant. System settings writes it on
   save (the kit's stand-in for evicting the settings cache + invalidating the
   client's disclosure cache).

   `enabled` is read LIVE on every call in the real service — never from the
   30-second snapshot — because "I turned it off" has to mean the next request
   is refused, not the next request after a TTL. The migration seeds it FALSE;
   this demo deployment has an administrator who turned it on.

   `apiKey` stays deploy-time configuration and is deliberately absent: the
   consequence an admin has to be told is that the configured key travels to
   whatever host is set here, which is what the base-URL row's advisory says. */
window.OdysseyData.fileAnalysisRuntime = {
  enabled: true,
  model: 'claude-opus-4-7',
  baseUrl: 'https://api.anthropic.com',
};

// The shipped defaults the migration seeds. Held separately so an advisory can
// say "this differs from the shipped default" without restating a literal.
window.OdysseyData.fileAnalysisDefaults = {
  enabled: false,
  model: 'claude-sonnet-5',
  baseUrl: 'https://api.anthropic.com',
};

// HOST only — never the path, query or userinfo. A gateway URL such as
// https://key:secret@gateway.internal/v1 is the expected shape here, so every
// surface that echoes the destination (advisory, job stamp, audit row) parses
// the host once and cannot reach the rest.
window.OdysseyData.hostOf = (url) => {
  try { const u = new URL(String(url || '').trim()); return u.host || null; } catch (e) { return null; }
};

/* The disclosure the consent gate renders, and the version that binds a user's
   affirmation to it. Stands in for GET /api/file-analysis/disclosure:

     disclosureVersion = Base64Url(SHA256(processor ␟ processorRegion ␟
       lawfulBasis ␟ privacyNoticeUrl ␟ model ␟ host(baseUrl)))[..16]

   (a short non-cryptographic digest here — the property being demonstrated is
   that the token changes with the tuple, not the hash function). `enabled` is
   deliberately NOT in the tuple: it is not a disclosure fact, and including it
   would invalidate every open consent gate on an unrelated toggle. host(baseUrl)
   — never the whole URL — so the version moves when the destination moves
   without the input carrying a path or a credential. */
window.OdysseyData.analysisDisclosure = () => {
  const t = window.OdysseyData.analysisTransfer;
  const r = window.OdysseyData.fileAnalysisRuntime;
  const host = window.OdysseyData.hostOf(r.baseUrl);
  const tuple = [t.processor, t.processorRegion, t.lawfulBasis, t.privacyNoticeUrl, r.model, host].join('\u241F');
  let h1 = 0x811c9dc5, h2 = 0x01000193;
  for (let i = 0; i < tuple.length; i++) {
    h1 = ((h1 ^ tuple.charCodeAt(i)) * 16777619) >>> 0;
    h2 = ((h2 + tuple.charCodeAt(i) * 31) * 2654435761) >>> 0;
  }
  const version = (h1.toString(36) + h2.toString(36) + '000000000000000').slice(0, 16);
  return {
    enabled: !!r.enabled,
    // A stored value that cannot be used resolves to null, never to the shipped
    // default: analysis refuses (503 configuration_unavailable) rather than
    // stamping a model that did not run or transferring to a processor nobody
    // chose. `baseUrlUsable` is the same fact for the destination, kept separate
    // so the host is not echoed here.
    baseUrlUsable: !!host,
    processor: t.processor,
    processorRegion: t.processorRegion,
    lawfulBasis: t.lawfulBasis,
    privacyNoticeUrl: t.privacyNoticeUrl,
    consentText: t.consentText,
    model: r.model,
    // The destination is NOT in this response — it is deployment infrastructure,
    // it can name an internal host, and it stays on the admin-gated settings DTO.
    disclosureVersion: version,
  };
};

// The matching vocabulary that would be sent: contact + tag NAMES only,
// archived excluded, capped per list. Returns counts (never the names) so the
// gate + audit log can show how many names a transfer carried. Over-cap ⇒ the
// match step is Skipped (manual fallback), never truncate-and-send-a-subset.
window.OdysseyData.analysisVocabulary = () => {
  const cap = window.OdysseyData.matchConfig.maxVocabulary;
  const cpNames = window.OdysseyData.contacts.filter((c) => !c.archived).length;
  const tagNames = window.OdysseyData.tags.filter((t) => !t.archived).length;
  return {
    contacts: cpNames,
    tags: tagNames,
    total: cpNames + tagNames,
    overCap: cpNames > cap || tagNames > cap,
    cap,
  };
};

/* External-analysis audit trail — one row per statement sent to Claude, for ISO
   27001 accountability + breach traceability (who · which file · when · result).
   Shapes mirror a would-be Odyssey.Finance FileAnalysisAuditEntry. Newest first. */
// Demo padding: synthesize additional accounts so the Accounts list exceeds one
// batch (25) and exercises the card-list infinite scroll (sentinel auto-load +
// skeletons + "X of N loaded" counter). Illustrative seed volume only — safe to
// remove. All USD so they add no FX "no rate" attention alerts.
(() => {
  const T = [
    { type: 'CheckingAccount',   icon: 'account_balance', tone: 'tide',   kind: 'Checking' },
    { type: 'SavingsAccount',    icon: 'savings',         tone: 'sea',    kind: 'Savings' },
    { type: 'CreditCard',        icon: 'credit_card',     tone: 'violet', kind: 'Rewards Card' },
    { type: 'InvestmentAccount', icon: 'trending_up',     tone: 'mint',   kind: 'Brokerage' },
    { type: 'CarLoan',           icon: 'directions_car',  tone: 'coral',  kind: 'Auto Loan' },
  ];
  const banks = ['Chase', 'Ally', 'Amex', 'Vanguard', 'Citi', 'Wells Fargo', 'Fidelity', 'SoFi',
    'Capital One', 'Discover', 'US Bank', 'PNC', 'Marcus', 'Schwab', 'TD', 'HSBC', 'Barclays', 'Truist'];
  const custodians = ['c14', 'c15', 'c16', 'c17', null];
  for (let i = 0; i < 24; i++) {
    const t = T[i % T.length];
    const isLiab = t.type === 'CreditCard' || t.type === 'CarLoan';
    const bal = isLiab ? -(200 + (i * 337) % 9000) : (500 + (i * 911) % 40000);
    const n = 1000 + (i * 7) % 8900;
    const yr = 2016 + (i % 9), mo = 1 + (i % 9), dy = 10 + (i % 18);
    window.OdysseyData.accounts.push({
      id: 'acc-demo-' + (i + 1),
      name: banks[i % banks.length] + ' ' + t.kind,
      number: '\u00b7' + n,
      accountNumber: n + ' 00' + (10 + i % 80) + ' ' + (2000 + i),
      custodianId: custodians[i % custodians.length],
      description: t.kind + ' account',
      type: t.type, currency: 'USD',
      opened: yr + '-' + String(mo).padStart(2, '0') + '-' + String(dy).padStart(2, '0'),
      closed: null, archived: null,
      balance: Math.round(bal * 100) / 100,
      deltaLabel: isLiab ? 'Statement due' : '+$ ' + (10 + (i * 13) % 400) + ' this week',
      deltaDir: isLiab ? 'down' : 'up',
      icon: t.icon, tone: t.tone,
    });
  }
})();

window.OdysseyData.analysisAuditLog = [
  { id: 'aud-1041', at: '2026-06-30T08:14:22Z', user: { name: 'Mara Lindqvist', email: 'mara@odyssey.app' },
    file: { id: 'f1', name: 'chase_checking_january.pdf', kind: 'Statement' }, account: { name: 'Everyday Checking', number: '••4471' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 4, size: '318 KB',
    status: 'Completed', candidates: 6, imported: 5, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    matchStatus: 'Completed', vocabularyCount: 31,
    // The conditions the transfer ran under (#439) — stamped at job creation from
    // one settings snapshot, immutable afterwards.
    analyzerBaseUrlHost: 'api.anthropic.com', processorInForce: 'Anthropic', processorRegionInForce: 'United States',
    requestId: 'fa_req_9f2a71c4', durationMs: 5120 },
  { id: 'aud-1040', at: '2026-06-29T16:42:09Z', user: { name: 'Tom Bekele', email: 'tom@odyssey.app' },
    file: { id: 'f4', name: 'amex_platinum_october.pdf', kind: 'Statement' }, account: { name: 'Travel Card', number: '••1008' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 7, size: '512 KB',
    status: 'Completed', candidates: 9, imported: 8, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    matchStatus: 'Completed', vocabularyCount: 30,
    analyzerBaseUrlHost: 'api.anthropic.com', processorInForce: 'Anthropic', processorRegionInForce: 'United States',
    requestId: 'fa_req_8b13de52', durationMs: 6730 },
  { id: 'aud-1039', at: '2026-06-27T11:05:55Z', user: { name: 'Mara Lindqvist', email: 'mara@odyssey.app' },
    file: { id: 'f7', name: 'wells_savings_q2.pdf', kind: 'Statement' }, account: { name: 'Rainy-day Savings', number: '••9920' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 3, size: '241 KB',
    status: 'Failed', candidates: 0, imported: 0, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    matchStatus: 'NotRun', vocabularyCount: null,
    analyzerBaseUrlHost: 'api.anthropic.com', processorInForce: 'Anthropic', processorRegionInForce: 'United States',
    requestId: 'fa_req_77a0c1f9', durationMs: 1980, failure: 'Scanned image — no extractable text layer.' },
  { id: 'aud-1038', at: '2026-06-24T09:31:40Z', user: { name: 'Priya Anand', email: 'priya@odyssey.app' },
    file: { id: 'f9', name: 'boa_business_may.pdf', kind: 'Statement' }, account: { name: 'Studio Operating', number: '••3312' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 11, size: '884 KB',
    status: 'Completed', candidates: 14, imported: 12, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    matchStatus: 'Failed', matchFailure: 'The matching provider returned an error.', vocabularyCount: 58,
    // This transfer left for an internal gateway — the destination was repointed
    // for a week while the disclosure kept naming Anthropic / United States.
    // Reconstructable only because the host is stamped on the row.
    analyzerBaseUrlHost: 'gateway.corp.internal', processorInForce: 'Anthropic', processorRegionInForce: 'United States',
    requestId: 'fa_req_5c44ab20', durationMs: 9410 },
  { id: 'aud-1037', at: '2026-06-20T14:18:03Z', user: { name: 'Tom Bekele', email: 'tom@odyssey.app' },
    file: { id: 'f2', name: 'chase_checking_december.pdf', kind: 'Statement' }, account: { name: 'Everyday Checking', number: '••4471' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 4, size: '301 KB',
    status: 'Completed', candidates: 7, imported: 7, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    // Recorded before the provenance columns existed: host, processor and region
    // are absent and render as "Not recorded", never back-filled with today's
    // values — a fabricated region would be a fabricated answer to “was this a
    // third-country transfer?”.
    matchStatus: 'Completed', vocabularyCount: 27,
    requestId: 'fa_req_41fb9e08', durationMs: 5560 },
  { id: 'aud-1036', at: '2026-06-15T10:02:51Z', user: { name: 'Priya Anand', email: 'priya@odyssey.app' },
    file: { id: 'f5', name: 'amex_platinum_september.pdf', kind: 'Statement' }, account: { name: 'Travel Card', number: '••1008' },
    provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0', pages: 6, size: '498 KB',
    status: 'Completed', candidates: 8, imported: 6, lawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
    matchStatus: 'Skipped', vocabularyCount: 512,
    requestId: 'fa_req_2ad7610b', durationMs: 6010 },
];

// Demo padding: synthesize additional historical entries so the Analysis log
// clearly exceeds one page (25) and exercises the card-list infinite scroll
// (sentinel auto-load + skeletons + "X of N loaded" counter). Purely
// illustrative seed volume — safe to remove once real audit volume exists.
(() => {
  const users = [
    { name: 'Mara Lindqvist', email: 'mara@odyssey.app' },
    { name: 'Tom Bekele', email: 'tom@odyssey.app' },
    { name: 'Priya Anand', email: 'priya@odyssey.app' },
    { name: 'Sofia Ruiz', email: 'sofia@odyssey.app' },
  ];
  const files = [
    { name: 'chase_checking_february.pdf', kind: 'Statement' },
    { name: 'amex_platinum_august.pdf', kind: 'Statement' },
    { name: 'ally_savings_q1.pdf', kind: 'Statement' },
    { name: 'vanguard_brokerage_2025.pdf', kind: 'Statement' },
    { name: 'citi_auto_loan_april.pdf', kind: 'Statement' },
    { name: 'boa_business_april.pdf', kind: 'Statement' },
  ];
  const accts = [
    { name: 'Everyday Checking', number: '\u2022\u20224471' },
    { name: 'Travel Card', number: '\u2022\u20221008' },
    { name: 'Rainy-day Savings', number: '\u2022\u20229920' },
    { name: 'Brokerage', number: '\u2022\u20222210' },
    { name: 'Studio Operating', number: '\u2022\u20223312' },
  ];
  const outcomes = [
    { status: 'Completed', matchStatus: 'Completed' },
    { status: 'Completed', matchStatus: 'Skipped' },
    { status: 'Completed', matchStatus: 'Completed' },
    { status: 'Failed', matchStatus: 'NotRun' },
    { status: 'Running', matchStatus: 'NotRun' },
  ];
  for (let i = 0; i < 32; i++) {
    const o = outcomes[i % outcomes.length];
    const cand = o.status === 'Completed' ? 4 + (i % 12) : 0;
    const mon = Math.max(1, 6 - Math.floor(i / 6));
    const day = 1 + ((i * 5) % 27);
    window.OdysseyData.analysisAuditLog.push({
      id: 'aud-' + (1035 - i),
      at: '2026-' + String(mon).padStart(2, '0') + '-' + String(day).padStart(2, '0')
        + 'T' + String(7 + (i % 12)).padStart(2, '0') + ':' + String((i * 7) % 60).padStart(2, '0') + ':00Z',
      user: users[i % users.length],
      file: Object.assign({ id: 'f-demo-' + i }, files[i % files.length]),
      account: accts[i % accts.length],
      provider: 'Claude', model: 'claude-opus-4-7', promptVersion: '1.0',
      pages: 3 + (i % 9), size: (180 + (i * 13) % 800) + ' KB',
      status: o.status, candidates: cand,
      imported: o.status === 'Completed' ? Math.max(0, cand - (i % 3)) : 0,
      lawfulBasis: 'Consent \u00b7 GDPR Art. 6(1)(a)',
      matchStatus: o.matchStatus, vocabularyCount: o.status === 'Failed' ? null : 20 + (i % 40),
      // Only the newer half of the padded history carries the #439 provenance
      // stamps; the older rows predate the columns and render "Not recorded".
      ...(i < 14 ? { analyzerBaseUrlHost: 'api.anthropic.com', processorInForce: 'Anthropic', processorRegionInForce: 'United States' } : {}),
      requestId: 'fa_req_' + i.toString(16).padStart(2, '0') + 'de' + (i * 3).toString(16).padStart(2, '0'),
      durationMs: 1800 + (i * 137) % 9000,
      ...(o.status === 'Failed' ? { failure: 'Scanned image \u2014 no extractable text layer.' } : {}),
    });
  }
})();

// Prepend a new audit row when a user consents + runs analysis from the dialog,
// so the live kit's log reflects the action the gate just authorized.
window.OdysseyData.recordAnalysisConsent = (entry) => {
  const row = Object.assign({
    id: 'aud-' + Math.floor(1042 + Math.random() * 100),
    at: new Date().toISOString().replace(/\.\d+Z$/, 'Z'),
    provider: window.OdysseyData.analyzer.provider,
    model: window.OdysseyData.fileAnalysisRuntime.model,
    promptVersion: window.OdysseyData.analyzer.promptVersion,
    lawfulBasis: window.OdysseyData.analysisTransfer.lawfulBasis,
    // The conditions this transfer actually ran under, stamped once at job
    // creation from ONE settings snapshot (issue #439): where the document went,
    // and the processor + region the deployment asserted at that instant. All
    // three are editable now, so none of them can be reconstructed later.
    analyzerBaseUrlHost: window.OdysseyData.hostOf(window.OdysseyData.fileAnalysisRuntime.baseUrl),
    processorInForce: window.OdysseyData.analysisTransfer.processor,
    processorRegionInForce: window.OdysseyData.analysisTransfer.processorRegion,
    status: 'Running', candidates: 0, imported: 0,
    // Matching runs after extraction; the row starts NotRun and records how many
    // names the transfer carried (the audit signal an admin reviews).
    matchStatus: 'NotRun', vocabularyCount: window.OdysseyData.analysisVocabulary().total,
    requestId: 'fa_req_' + Math.random().toString(16).slice(2, 10),
  }, entry);
  window.OdysseyData.analysisAuditLog.unshift(row);
  return row;
};

/* ---------------------------------------------------------------------------
   Resumable file-analysis reviews (feature: "Resume an open review").
   ----------------------------------------------------------------------------
   A persisted analysis job is RESUMABLE when extraction Completed, candidates
   are still Pending, and it isn't failed/superseded. The real app discovers
   these via ONE account-scoped read — GET /api/accounts/{id}/files/analysis/
   resumable — returning a fileId → minimal-summary map (no candidate detail).
   Here that map is seeded statically and read by the Files surfaces' host.

   The summary mirrors the response DTO: fileId, analysisJobId, status,
   startedAt, candidateCount, pendingCount — counts only, never candidate
   free-text (data-minimisation). Files with no resumable job are simply ABSENT
   from the map (the same uniform representation for never-analysed, failed-only
   and all-reviewed — so it can't be used as an existence oracle). */
window.OdysseyData.resumableJobs = {
  // Amex · October — review opened, dialog closed before importing (9 pending).
  f4: { fileId: 'f4', analysisJobId: 'fa-f4', status: 'Completed',
        startedAt: '2026-06-30T09:12:04Z', candidateCount: 9, pendingCount: 9 },
  // Chase · January — partially triaged, then left (6 candidates, 4 still pending).
  f1: { fileId: 'f1', analysisJobId: 'fa-f1', status: 'Completed',
        startedAt: '2026-06-29T17:40:51Z', candidateCount: 6, pendingCount: 4 },
};
// The account-scoped read: the per-file resumable summaries for one account's
// files. Single round-trip, no per-file call. Returns [] when none resumable.
window.OdysseyData.resumableJobsForAccount = (accountId) => {
  const files = window.OdysseyData.accountFiles[accountId] || [];
  return files
    .map(f => window.OdysseyData.resumableJobs[f.id])
    .filter(Boolean);
};
// Convenience for a single file (host already holds the map; this just reads it).
window.OdysseyData.resumableSummaryForFile = (file) =>
  (file && window.OdysseyData.resumableJobs[file.id]) || null;
// Clearing a file's resumable hint after its review is imported/finished, so the
// host can refresh the map on dialog close (mirrors re-fetching the read).
window.OdysseyData.clearResumableJob = (fileId) => {
  delete window.OdysseyData.resumableJobs[fileId];
};

window.OdysseyData.tagById = Object.fromEntries(window.OdysseyData.tags.map(t => [t.id, t]));
/* A transaction's tag-id set, tolerant of every shape it travels in: the raw
   data (`tags: string[]` ids, or the legacy single `tag`) AND the denormalized
   table row (`tags: [{id,label}]` objects the bridge builds for display).
   Always returns a flat array of id strings. */
window.OdysseyData.txnTagIds = (t) => {
  const src = (t && t.tags && t.tags.length) ? t.tags : (t && t.tag ? [t.tag] : []);
  return src.map(x => (x && typeof x === 'object') ? x.id : x).filter(Boolean);
};
/* The same set resolved to TransactionTag records (skips unknown / archived-away ids). */
window.OdysseyData.txnTags = (t) => window.OdysseyData.txnTagIds(t)
  .map(id => window.OdysseyData.tagById[id])
  .filter(Boolean);
window.OdysseyData.accountById = Object.fromEntries(window.OdysseyData.accounts.map(a => [a.id, a]));
window.OdysseyData.accountTypeById = Object.fromEntries(window.OdysseyData.accountTypes.map(t => [t.key, t]));
window.OdysseyData.contactTypeByKey = Object.fromEntries(window.OdysseyData.contactTypes.map(t => [t.key, t]));
window.OdysseyData.accountFileTypeByKey = Object.fromEntries(window.OdysseyData.accountFileTypes.map(t => [t.key, t]));
window.OdysseyData.transactionFileTypeByKey = Object.fromEntries(window.OdysseyData.transactionFileTypes.map(t => [t.key, t]));
window.OdysseyData.taxStatementFileTypeByKey = Object.fromEntries(window.OdysseyData.taxStatementFileTypes.map(t => [t.key, t]));
window.OdysseyData.insurancePolicyTypeByKey = Object.fromEntries(window.OdysseyData.insurancePolicyTypes.map(t => [t.key, t]));
window.OdysseyData.policyFileTypeByKey = Object.fromEntries(window.OdysseyData.policyFileTypes.map(t => [t.key, t]));
/* Merged kind→icon/color lookup for rendering a file's avatar/chip on any surface,
   regardless of which enum it came from. Account types win the shared `Other`
   (identical icon/color anyway). Pickers use the per-context lists above. */
window.OdysseyData.fileTypeByKey = Object.assign(
  {},
  window.OdysseyData.taxStatementFileTypeByKey,
  window.OdysseyData.transactionFileTypeByKey,
  window.OdysseyData.accountFileTypeByKey,
);
window.OdysseyData.contactById = Object.fromEntries(window.OdysseyData.contacts.map(c => [c.id, c]));
/* Resolve an account's custodian to the slim Custodian projection the read DTO
   carries (identifying fields only — no free-text description). Returns null for
   no link, and also for a dangling link whose contact was deleted
   (CustodianMissing → reads back as no custodian). The archived flag rides along
   so the chip can show its archived state. */
window.OdysseyData.custodianForAccount = (a) => {
  const id = a && a.custodianId;
  if (!id) return null;
  const c = window.OdysseyData.contactById[id];
  if (!c) return null;
  return {
    contactId: c.id,
    name: c.name,
    normalizedName: c.normalizedName,
    type: c.type,
    organizationNumber: c.orgNumber || null,
    archived: c.archived || null,
  };
};
/* Active (non-archived) contacts — the selectable custodian options. */
window.OdysseyData.activeContacts = () => window.OdysseyData.contacts.filter(c => !c.archived);
window.OdysseyData.currencyByCode = Object.fromEntries(window.OdysseyData.currencies.map(c => [c.code, c]));

window.OdysseyHelpers = {
  money(n, currency = 'USD') {
    const sign = n < 0 ? '−' : '';
    const abs = Math.abs(n);
    return `${sign}$ ${abs.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  },
  signedMoney(n, currency = 'USD') {
    const sign = n < 0 ? '−' : '+';
    const abs = Math.abs(n);
    return `${sign}$ ${abs.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  },
  dateShort(iso) {
    const d = new Date(iso);
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
  },
  dateLong(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-US', { month: 'short', day: '2-digit', year: 'numeric' });
  },
  txnsForAccount(accountId) {
    return window.OdysseyData.transactions.filter(t => t.account === accountId);
  },
  filesForAccount(accountId) {
    return window.OdysseyData.accountFiles[accountId] || [];
  },
  // Attachments stored directly on a transaction (AccountFile shape). Created
  // transactions carry these via the Add-transaction modal's Files[]; a few
  // seed rows include them too.
  filesForTransaction(t) {
    return (t && t.files) || [];
  },
  // Prototype files have no real bytes — synthesize a small placeholder blob and
  // download it under the file's real name so the Download action visibly works.
  downloadFile(f) {
    const body =
      `Odyssey — placeholder export\n` +
      `============================\n\n` +
      `File name : ${f.name}\n` +
      `Kind      : ${f.kind}\n` +
      `Size      : ${f.size}\n` +
      `Uploaded  : ${f.uploaded}\n` +
      `Exported  : ${new Date().toISOString()}\n\n` +
      `This is a stand-in generated by the Odyssey UI kit prototype.\n` +
      `In the production app this downloads the original stored file.\n`;
    const url = URL.createObjectURL(new Blob([body], { type: 'text/plain' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = f.name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
  accountStatus(a) {
    if (a.archived) return { label: 'Archived', tone: 'outline' };
    if (a.closed)   return { label: 'Closed',   tone: 'expense' };
    return { label: 'Open', tone: 'income' };
  },

  // ---- Reference-data helpers (tags / contacts / currencies / rates) ----
  // Active / Archived chip for any record carrying a nullable `archived` field
  // (TransactionTag, Contact, Currency) — mirrors the accountStatus pattern.
  archivedStatus(rec) {
    return rec && rec.archived
      ? { label: 'Archived', tone: 'outline' }
      : { label: 'Active', tone: 'income' };
  },
  // Server-side NormalizedName rule: ToUpperInvariant + whitespace-collapse + trim.
  normalizeName(s) { return (s || '').toUpperCase().replace(/\s+/g, ' ').trim(); },
  // 'Jun 05, 2026, 09:00' — used for AsOf / CreatedAt / Archived timestamps.
  dateTime(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-US', { month: 'short', day: '2-digit', year: 'numeric' }) +
      ', ' + d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hour12: false });
  },
  // The ExchangeRate ids that are the *latest* AsOf for their (from,to) pair —
  // i.e. the rate a conversion would actually use. Everything else is history.
  currentRateIds(rates) {
    const best = {};
    for (const r of rates) {
      const k = `${r.from}>${r.to}`;
      if (!best[k] || r.asOf > best[k].asOf) best[k] = r;
    }
    return new Set(Object.values(best).map(r => r.id));
  },

  // ---- Budget helpers -------------------------------------------------
  // Active / Archived chip, mirroring ViewBudgetCard's Archived?-is-null check.
  budgetStatus(b) {
    return b.archived
      ? { label: 'Archived', tone: 'outline' }
      : { label: 'Active', tone: 'income' };
  },
  // The budget's date range (ISO 'YYYY-MM-DD' strings sort lexically).
  budgetItems(b) { return b.items || []; },
  // Transactions matched to the budget: in [startDate, endDate] AND tagged with
  // ANY of the budget's item tags (the txn carries a tag SET now). De-duplicated
  // by transaction — a txn matching two of the budget's tags still appears once.
  // Client-side stand-in for GET /api/budgets/{id}/transactions (BudgetReport).
  budgetMatchedTxns(b) {
    const tagIds = new Set(b.items.filter(i => i.tagId).map(i => i.tagId));
    if (tagIds.size === 0) return [];
    return window.OdysseyData.transactions
      .filter(t => window.OdysseyData.txnTagIds(t).some(id => tagIds.has(id)) && t.date >= b.startDate && t.date <= b.endDate)
      .sort((a, c) => c.date.localeCompare(a.date));
  },
  // Per-item actual = sum of |amount| of transactions carrying that item's tag,
  // in range. (BudgetReport.ExistingTransactionReport.Sum, grouped by tag.) With
  // multi-tag, a txn tagged with two of the budget's item tags counts under EACH
  // — so the per-tag buckets can sum to more than the de-duplicated transaction
  // total (no amount splitting in v1, per spec §9). Untagged items stay plan-only.
  budgetItemActual(item, b) {
    if (!item.tagId) return 0;
    return window.OdysseyData.transactions
      .filter(t => window.OdysseyData.txnTagIds(t).includes(item.tagId) && t.date >= b.startDate && t.date <= b.endDate)
      .reduce((s, t) => s + Math.abs(t.amount), 0);
  },
  // Roll-up: planned + actual income/expense and their differences.
  budgetTotals(b) {
    const H = window.OdysseyHelpers;
    let plannedIncome = 0, plannedExpense = 0, actualIncome = 0, actualExpense = 0;
    for (const it of b.items) {
      const actual = H.budgetItemActual(it, b);
      if (it.categoryType === 'Income') { plannedIncome += it.planned; actualIncome += actual; }
      else { plannedExpense += it.planned; actualExpense += actual; }
    }
    return {
      plannedIncome, plannedExpense, actualIncome, actualExpense,
      expectedDiff: plannedIncome - plannedExpense,
      actualDiff: actualIncome - actualExpense,
    };
  },
};

// Back-compat alias for any consumer of the old single-budget shape.
window.OdysseyData.budget = window.OdysseyData.budgets[0];
window.OdysseyData.budgetById = Object.fromEntries(window.OdysseyData.budgets.map(b => [b.id, b]));

// ---- File-analysis helpers -------------------------------------------------
Object.assign(window.OdysseyHelpers, {
  // ISO 'YYYY-MM-DD' for a Date — used when seeding StartedAt/CompletedAt.
  isoNow() { return new Date().toISOString(); },

  // Whether a stored file is eligible for analysis. The server only accepts
  // AccountFileType.Statement; everything else throws "Only files of type
  // Statement can be analyzed."
  canAnalyze(file) { return file && file.kind === 'Statement'; },

  // Build an ExistingFileAnalysisJob-shaped object for a statement file. Falls
  // back to the Amex set so any statement demoes with real-looking candidates.
  analysisJobForFile(file) {
    const A = window.OdysseyData.analyzer;
    const set = window.OdysseyData.analysisCandidates[file && file.id]
      || window.OdysseyData.analysisCandidates.f4;
    return {
      id: `fa-${(file && file.id) || 'demo'}`,
      accountFileId: (file && file.id) || 'f4',
      status: 'Completed',
      fileTypeDetected: 'application/pdf',
      startedAt: null,
      completedAt: null,
      failureCode: null,
      failureMessage: null,
      analyzerProvider: A.provider,
      // The model comes from the settings snapshot read for THIS run (#439), not
      // from configuration — the value a request is built with is the value
      // stamped on the job.
      analyzerModel: window.OdysseyData.fileAnalysisRuntime.model || A.model,
      promptVersion: A.promptVersion,
      // Where this run's requests went, and what the deployment disclosed at that
      // moment. Host only — never the path, query or userinfo.
      analyzerBaseUrlHost: window.OdysseyData.hostOf(window.OdysseyData.fileAnalysisRuntime.baseUrl),
      processorInForce: window.OdysseyData.analysisTransfer.processor,
      processorRegionInForce: window.OdysseyData.analysisTransfer.processorRegion,
      // Match step state, orthogonal to the extraction `status` above. Extraction
      // `status` still governs importability; matchStatus only governs whether
      // suggestions exist — so a match failure never blocks the import.
      matchStatus: 'Completed',          // NotRun | Running | Completed | Skipped | Failed
      matchFailureMessage: null,         // curated reason when Failed (never the raw body)
      vocabularyCount: window.OdysseyData.analysisVocabulary().total,
      // Deep-clone so per-dialog edits never mutate the seed.
      candidates: set.map(c => ({ ...c, reviewStatus: 'Pending', reviewedAt: null })),
    };
  },

  // Confidence → { label, tone } band. Sea (info) high, amber (pending) medium,
  // coral (expense) low — reusing the chip tone vocabulary.
  confidenceBand(v) {
    if (v == null) return { pct: null, label: '—', tone: 'outline' };
    const pct = Math.round(v * 100);
    if (v >= 0.85) return { pct, label: 'High', tone: 'info' };
    if (v >= 0.6)  return { pct, label: 'Medium', tone: 'pending' };
    return { pct, label: 'Low', tone: 'expense' };
  },

  // Match confidence → band, phrased for the merchant/category match indicator
  // ("High match · 91%"). `linked` is whether it cleared the auto-link threshold —
  // the band label is the same scale as confidenceBand but the word is "match".
  matchBand(v) {
    const T = window.OdysseyData.matchConfig.autoLinkThreshold;
    if (v == null) return { pct: null, label: 'No match', tone: 'outline', linked: false };
    const pct = Math.round(v * 100);
    const linked = v >= T;
    const label = v >= 0.85 ? 'High match' : v >= 0.6 ? 'Good match' : 'Low match';
    const tone = v >= 0.85 ? 'info' : v >= 0.6 ? 'info' : 'pending';
    return { pct, label, tone, linked };
  },
});

/* ============================================================================
   Account terms — interest-rate & fee history (AccountTerm feature)
   ----------------------------------------------------------------------------
   A time-versioned history of an account's TERMS: its interest rate, an optional
   expected return, and the prices of its bank services (fees). Each entry records
   a value (a Percentage fraction in [-1,1], or a money Amount) of a given TermKind,
   effective from a date; the latest entry on/before a date is the value in force
   (implicit supersession — no EffectiveTo). Mirrors the backend spec:
   AccountTerm / TermKind / TermValueUnit / BillingPeriod + the eligibility matrix.

   Canonical registry, sibling of accountTypes / contactTypes / fileTypes —
   single source of truth for a kind's label / group / icon / color / default unit,
   so a term reads identically in the summary, chart, history table and picker.
   Hues sit in the shared categorical band (L~0.74–0.80, C~0.13–0.16); brand tide
   stays out of it. Interest rate leads (group 'rate'); fees are the second group. */
window.OdysseyData.termKinds = [
  // ---- Rates ----
  { key: 'InterestRate',   label: 'Interest rate',   group: 'rate', enumValue: 1,  defaultUnit: 'Percentage', icon: 'percent',       color: 'oklch(0.78 0.13 200)',  soft: 'oklch(0.78 0.13 200 / 0.15)',  desc: 'Contractual interest the account earns or is charged.' },
  { key: 'ExpectedReturn', label: 'Expected return', group: 'rate', enumValue: 2,  defaultUnit: 'Percentage', icon: 'trending_up',   color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.15)',  desc: 'Optional target / expected annual return for a variable-return holding.' },
  // ---- Fees ----
  { key: 'ManagementFee',  label: 'Management fee',   group: 'fee',  enumValue: 10, defaultUnit: 'Percentage', icon: 'pie_chart',     color: 'oklch(0.77 0.14 55)',   soft: 'oklch(0.77 0.14 55 / 0.15)',   desc: 'Fund / platform / management fee — usually a percentage of assets.' },
  { key: 'ServiceFee',     label: 'Service fee',      group: 'fee',  enumValue: 11, defaultUnit: 'Amount',     icon: 'event_repeat',  color: 'oklch(0.76 0.13 225)',  soft: 'oklch(0.76 0.13 225 / 0.15)',  desc: 'Periodic account / service fee — usually a flat amount.' },
  { key: 'TransactionFee', label: 'Transaction fee',  group: 'fee',  enumValue: 12, defaultUnit: 'Amount',     icon: 'swap_horiz',    color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.15)',  desc: 'Per-transaction fee — an amount or a percentage.' },
  { key: 'OtherFee',       label: 'Other fee',        group: 'fee',  enumValue: 99, defaultUnit: 'Amount',     icon: 'receipt_long',  color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.15)',  desc: 'Any other fee outside the categories above.' },
];
window.OdysseyData.termKindByKey = Object.fromEntries(window.OdysseyData.termKinds.map(t => [t.key, t]));

/* BillingPeriod enum — optional context for fees; null for rates. */
window.OdysseyData.billingPeriods = [
  { key: 'OneTime',        label: 'One-time',        chip: 'One-time', enumValue: 0, suffix: '' },
  { key: 'PerTransaction', label: 'Per transaction', chip: 'Per txn',  enumValue: 1, suffix: '/txn' },
  { key: 'Daily',          label: 'Daily',           chip: 'Daily',    enumValue: 2, suffix: '/day' },
  { key: 'Monthly',        label: 'Monthly',         chip: 'Monthly',  enumValue: 3, suffix: '/mo' },
  { key: 'Quarterly',      label: 'Quarterly',       chip: 'Quarterly',enumValue: 4, suffix: '/qtr' },
  { key: 'Annually',       label: 'Annually',        chip: 'Annually', enumValue: 5, suffix: '/yr' },
];
window.OdysseyData.billingPeriodByKey = Object.fromEntries(window.OdysseyData.billingPeriods.map(b => [b.key, b]));

/* Eligibility matrix (TermKind → permitted AccountTypes). Lives in code, not the
   DB, so it can evolve without a migration. 'ALL' = every account type. */
window.OdysseyData.termKindEligibility = {
  InterestRate:   ['CheckingAccount', 'SavingsAccount', 'PensionAccount', 'CreditCard', 'Mortgage', 'StudentLoan', 'PersonalLoan', 'CarLoan', 'TaxDebt'],
  ExpectedReturn: ['InvestmentAccount', 'PensionAccount'],
  ManagementFee:  'ALL',
  ServiceFee:     'ALL',
  TransactionFee: 'ALL',
  OtherFee:       'ALL',
};

/* Seed AccountTerm history, keyed by accountId. EffectiveFrom ascending here for
   readability; the helpers sort as needed. Percentages stored as fractions. */
window.OdysseyData.accountTerms = {
  // Ally Savings — a high-yield rate stepped DOWN over two years (the headline story).
  '2': [
    { id: 'tm-2-1', accountId: '2', kind: 'InterestRate',   unit: 'Percentage', value: 0.0425, currency: null,  billingPeriod: null,             effectiveFrom: '2024-02-01', note: 'Promotional intro APY',                  createdAtUtc: '2024-02-01T09:00:00Z' },
    { id: 'tm-2-2', accountId: '2', kind: 'InterestRate',   unit: 'Percentage', value: 0.0410, currency: null,  billingPeriod: null,             effectiveFrom: '2024-09-01', note: null,                                     createdAtUtc: '2024-09-01T09:00:00Z' },
    { id: 'tm-2-3', accountId: '2', kind: 'InterestRate',   unit: 'Percentage', value: 0.0385, currency: null,  billingPeriod: null,             effectiveFrom: '2025-01-15', note: 'Fed cut pass-through',                   createdAtUtc: '2025-01-15T09:00:00Z' },
    { id: 'tm-2-4', accountId: '2', kind: 'InterestRate',   unit: 'Percentage', value: 0.0360, currency: null,  billingPeriod: null,             effectiveFrom: '2025-07-01', note: null,                                     createdAtUtc: '2025-07-01T09:00:00Z' },
    { id: 'tm-2-5', accountId: '2', kind: 'InterestRate',   unit: 'Percentage', value: 0.0340, currency: null,  billingPeriod: null,             effectiveFrom: '2026-02-10', note: 'Fed cut pass-through',                   createdAtUtc: '2026-02-10T09:00:00Z' },
    { id: 'tm-2-6', accountId: '2', kind: 'TransactionFee', unit: 'Amount',     value: 10.00,  currency: 'USD', billingPeriod: 'PerTransaction', effectiveFrom: '2024-02-01', note: 'Excess withdrawal fee (over 6 / month)', createdAtUtc: '2024-02-01T09:00:00Z' },
  ],
  // Amex Platinum — purchase APR stepped UP, plus an annual fee and a cash-advance fee.
  '3': [
    { id: 'tm-3-1', accountId: '3', kind: 'InterestRate',   unit: 'Percentage', value: 0.2249, currency: null,  billingPeriod: null,             effectiveFrom: '2023-01-01', note: 'Variable purchase APR (Prime + 16.99%)', createdAtUtc: '2023-01-01T09:00:00Z' },
    { id: 'tm-3-2', accountId: '3', kind: 'InterestRate',   unit: 'Percentage', value: 0.2624, currency: null,  billingPeriod: null,             effectiveFrom: '2023-09-01', note: null,                                     createdAtUtc: '2023-09-01T09:00:00Z' },
    { id: 'tm-3-3', accountId: '3', kind: 'InterestRate',   unit: 'Percentage', value: 0.2899, currency: null,  billingPeriod: null,             effectiveFrom: '2024-06-01', note: 'Prime-rate increase',                    createdAtUtc: '2024-06-01T09:00:00Z' },
    { id: 'tm-3-4', accountId: '3', kind: 'ServiceFee',     unit: 'Amount',     value: 695.00, currency: 'USD', billingPeriod: 'Annually',       effectiveFrom: '2023-01-01', note: 'Annual membership fee',                  createdAtUtc: '2023-01-01T09:00:00Z' },
    { id: 'tm-3-5', accountId: '3', kind: 'TransactionFee', unit: 'Percentage', value: 0.0500, currency: null,  billingPeriod: 'PerTransaction', effectiveFrom: '2023-01-01', note: 'Cash-advance fee',                       createdAtUtc: '2023-01-01T09:00:00Z' },
  ],
  // Vanguard Brokerage — an expected-return target (lowered once) + an expense ratio.
  '4': [
    { id: 'tm-4-1', accountId: '4', kind: 'ExpectedReturn', unit: 'Percentage', value: 0.0700, currency: null,  billingPeriod: null,       effectiveFrom: '2024-01-01', note: 'Long-run target · 80/20 blend', createdAtUtc: '2024-01-01T09:00:00Z' },
    { id: 'tm-4-2', accountId: '4', kind: 'ExpectedReturn', unit: 'Percentage', value: 0.0650, currency: null,  billingPeriod: null,       effectiveFrom: '2025-06-01', note: 'Trimmed on valuation outlook', createdAtUtc: '2025-06-01T09:00:00Z' },
    { id: 'tm-4-3', accountId: '4', kind: 'ManagementFee',  unit: 'Percentage', value: 0.0004, currency: null,  billingPeriod: 'Annually', effectiveFrom: '2023-01-01', note: 'Blended expense ratio',        createdAtUtc: '2023-01-01T09:00:00Z' },
    { id: 'tm-4-4', accountId: '4', kind: 'ManagementFee',  unit: 'Percentage', value: 0.0003, currency: null,  billingPeriod: 'Annually', effectiveFrom: '2025-01-01', note: 'Expense ratio reduction',      createdAtUtc: '2025-01-01T09:00:00Z' },
  ],
  // Citi Auto Loan — a single fixed APR (chart shows one flat hold) + a late fee.
  '5': [
    { id: 'tm-5-1', accountId: '5', kind: 'InterestRate', unit: 'Percentage', value: 0.0649, currency: null,  billingPeriod: null,             effectiveFrom: '2023-06-01', note: 'Fixed APR · 60-month term', createdAtUtc: '2023-06-01T09:00:00Z' },
    { id: 'tm-5-2', accountId: '5', kind: 'OtherFee',     unit: 'Amount',     value: 15.00,  currency: 'USD', billingPeriod: 'PerTransaction', effectiveFrom: '2023-06-01', note: 'Late-payment fee',          createdAtUtc: '2023-06-01T09:00:00Z' },
  ],
  // Chase Checking ('1') intentionally has no terms — drives the empty state.
};

Object.assign(window.OdysseyHelpers, {
  termKindInfo(kind) {
    return window.OdysseyData.termKindByKey[kind]
      || { key: kind, label: kind || 'Term', group: 'fee', defaultUnit: 'Amount', icon: 'sell', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };
  },
  billingInfo(key) {
    return key ? (window.OdysseyData.billingPeriodByKey[key] || { key, label: key, suffix: '' }) : null;
  },
  // All terms for an account, EffectiveFrom DESC (history listing, newest first).
  termsForAccount(accountId) {
    return (window.OdysseyData.accountTerms[accountId] || [])
      .slice()
      .sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0));
  },
  // TermKinds permitted for an account type, in registry order.
  eligibleTermKinds(accountType) {
    const E = window.OdysseyData.termKindEligibility;
    return window.OdysseyData.termKinds
      .filter(k => { const a = E[k.key]; return a === 'ALL' || (a && a.includes(accountType)); })
      .map(k => k.key);
  },
  isTermKindEligible(kind, accountType) {
    const a = window.OdysseyData.termKindEligibility[kind];
    return a === 'ALL' || (!!a && a.includes(accountType));
  },
  // The currently-effective entry per kind as of `asOf` (default: today): for each
  // kind with ≥1 entry, the one with the greatest EffectiveFrom ≤ asOf. Returns
  // them in registry order (rates first). This is the GET …/terms/current view.
  currentTerms(accountId, asOf) {
    const cutoff = asOf || new Date().toISOString().slice(0, 10);
    const byKind = {};
    for (const t of (window.OdysseyData.accountTerms[accountId] || [])) {
      if (t.effectiveFrom > cutoff) continue; // future-dated, not yet in force
      const cur = byKind[t.kind];
      if (!cur || t.effectiveFrom > cur.effectiveFrom) byKind[t.kind] = t;
    }
    return window.OdysseyData.termKinds
      .map(k => byKind[k.key])
      .filter(Boolean);
  },
  // Ascending {date,value,note,id} series for one kind — for the step chart.
  termSeries(accountId, kind) {
    return (window.OdysseyData.accountTerms[accountId] || [])
      .filter(t => t.kind === kind)
      .map(t => ({ id: t.id, date: t.effectiveFrom, value: t.value, note: t.note }))
      .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
  },
  // Percentage fraction → trimmed display string. 0.0340 → "3.40%", 0.0003 → "0.03%".
  pctStr(frac) {
    const p = frac * 100;
    const s = Math.abs(p) < 1 ? p.toFixed(2) : p.toFixed(2).replace(/\.?0+$/, '');
    return `${s}%`;
  },
  // A term's value as a display string (percentage or money) with the right unit.
  fmtTermValue(t) {
    if (t.unit === 'Percentage') return window.OdysseyHelpers.pctStr(t.value);
    return window.OdysseyHelpers.money(t.value, t.currency || 'USD');
  },

  // ---- Rate sign: a loan's interest is a COST -------------------------------
  // Interest you're charged on a liability (loan, credit card, …) is money out,
  // so its rate reads negative + expense-colored — mirroring how the account's
  // balance is shown. Interest earned on an asset (savings) and an expected
  // return on an investment stay positive. Fees keep their own (price) framing.
  accountIsLiability(account) {
    const ti = account && window.OdysseyData.accountTypeById[account.type];
    return !!ti && ti.group === 'liability';
  },
  // Does this term read as a cost (negative) for its account? An interest rate
  // on a liability. (Expected return only exists on assets; fees stay positive.)
  termIsNegative(t, account) {
    return t.unit === 'Percentage'
      && t.kind === 'InterestRate'
      && window.OdysseyHelpers.accountIsLiability(account);
  },
  // The value with the cost sign applied (for the chart + deltas).
  signedTermValue(t, account) {
    return window.OdysseyHelpers.termIsNegative(t, account) ? -Math.abs(t.value) : t.value;
  },
  // Expense color for a cost-rate, else null (caller keeps its own color).
  costColor(t, account) {
    return window.OdysseyHelpers.termIsNegative(t, account) ? 'var(--finance-expense)' : null;
  },
  // Display string, signed for cost-rates: "−6.49%" on a loan, "3.40%" on savings.
  fmtTermValueFor(t, account) {
    if (t.unit !== 'Percentage') return window.OdysseyHelpers.money(t.value, t.currency || 'USD');
    const v = window.OdysseyHelpers.signedTermValue(t, account);
    return (v < 0 ? '−' : '') + window.OdysseyHelpers.pctStr(Math.abs(v));
  },
});

/* ============================================================================
   Account value estimates — user-supplied worth of non-transactional assets
   (AccountEstimate feature; sibling of AccountTerm)
   ----------------------------------------------------------------------------
   A time-versioned history of an account's ESTIMATED VALUE — a single money
   amount in the account's own currency, effective from a date. The latest entry
   on/before a date is the value in force (implicit supersession — no EffectiveTo,
   step function, identical resolution to AccountTerm). Mirrors the backend spec:
   AccountEstimate { Value · CurrencyCode (= account currency) · EffectiveFrom ·
   Note · CreatedAtUtc }, the …/estimates endpoints, and the account-type matrix.

   Unlike a term, an estimate has NO kind / unit / billing dimension — it's always
   one amount. The current estimate REPLACES the transaction balance in net worth
   when present (the §9 "replace" policy); the section surfaces the estimate as the
   headline value and the transaction balance as a quiet secondary. All account
   types may carry estimates; a recommended practical subset (asset accounts whose
   worth isn't transaction-derived) is highlighted in the UI but never enforced. */

window.OdysseyData.estimateRecommendedTypes = ['Property', 'Vehicle', 'OtherAsset', 'InvestmentAccount', 'PensionAccount'];

/* Seed AccountEstimate history, keyed by accountId. EffectiveFrom ascending here
   for readability; the helpers sort as needed. Currency always = account currency. */
window.OdysseyData.accountEstimates = {
  // Maple St Residence (Property '7') — successive appraisals, purchase → today.
  '7': [
    { id: 'es-7-1', accountId: '7', value: 540000, currencyCode: 'USD', effectiveFrom: '2018-09-05', note: 'Purchase price at closing',          createdAtUtc: '2018-09-05T09:00:00Z' },
    { id: 'es-7-2', accountId: '7', value: 588000, currencyCode: 'USD', effectiveFrom: '2020-06-01', note: 'County reassessment',                 createdAtUtc: '2020-06-01T09:00:00Z' },
    { id: 'es-7-3', accountId: '7', value: 642000, currencyCode: 'USD', effectiveFrom: '2022-04-01', note: 'Refinance appraisal',                 createdAtUtc: '2022-04-01T09:00:00Z' },
    { id: 'es-7-4', accountId: '7', value: 668000, currencyCode: 'USD', effectiveFrom: '2024-03-15', note: 'Online valuation estimate',           createdAtUtc: '2024-03-15T09:00:00Z' },
    { id: 'es-7-5', accountId: '7', value: 685000, currencyCode: 'USD', effectiveFrom: '2026-04-10', note: 'Annual estimate · comparable sales',  createdAtUtc: '2026-04-10T09:00:00Z' },
  ],
  // Chase Checking ('1') intentionally has no estimates — drives the empty state.
};

Object.assign(window.OdysseyHelpers, {
  // Is this account type in the recommended practical subset for estimates?
  // (UI hint only — every type is eligible.)
  isEstimateRecommended(accountType) {
    return window.OdysseyData.estimateRecommendedTypes.includes(accountType);
  },
  // All estimates for an account, EffectiveFrom DESC (history listing, newest first).
  estimatesForAccount(accountId) {
    return (window.OdysseyData.accountEstimates[accountId] || [])
      .slice()
      .sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0));
  },
  // Ascending {id,date,value,note} series — for the value chart + change deltas.
  estimateSeries(accountId) {
    return (window.OdysseyData.accountEstimates[accountId] || [])
      .map(e => ({ id: e.id, date: e.effectiveFrom, value: e.value, note: e.note }))
      .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
  },
  // The currently-effective estimate as of `asOf` (default today): the entry with
  // the greatest EffectiveFrom on/before the cutoff. This is GET …/estimates/current.
  currentEstimate(accountId, asOf) {
    const cutoff = asOf || new Date().toISOString().slice(0, 10);
    let cur = null;
    for (const e of (window.OdysseyData.accountEstimates[accountId] || [])) {
      if (e.effectiveFrom > cutoff) continue;
      if (!cur || e.effectiveFrom > cur.effectiveFrom) cur = e;
    }
    return cur;
  },
  // Compact money for chart axes: 540000 → "$ 540k", 1250000 → "$ 1.25M".
  moneyCompact(n, currency = 'USD') {
    const sign = n < 0 ? '−' : '';
    const abs = Math.abs(n);
    let s;
    if (abs >= 1e9) s = (abs / 1e9).toFixed(abs % 1e9 ? 2 : 0).replace(/\.?0+$/, '') + 'B';
    else if (abs >= 1e6) s = (abs / 1e6).toFixed(abs % 1e6 ? 2 : 0).replace(/\.?0+$/, '') + 'M';
    else if (abs >= 1e3) s = (abs / 1e3).toFixed(abs % 1e3 ? 1 : 0).replace(/\.?0+$/, '') + 'k';
    else s = String(Math.round(abs));
    return `${sign}$ ${s}`;
  },
});
