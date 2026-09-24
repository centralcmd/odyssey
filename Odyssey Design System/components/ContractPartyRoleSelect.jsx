/**
 * Odyssey DS — ContractPartyRoleSelect
 * A single-select pre-wired to the ContractPartyRole vocabulary, and the only
 * picker in the system whose option list depends on ANOTHER field: a role is
 * legal only on certain contract types. Pass `contractType` and the control
 * offers exactly the legal roles for that type, the SUGGESTED ones first under
 * their own group heading; omit it and the full vocabulary is offered.
 *
 * There is no default and no empty member. `Unspecified` was retired with the
 * matrix — a role is now required on every party write — so the control starts
 * on the placeholder and the form's save stays disabled until one is picked.
 *
 * `CONTRACT_PARTY_ROLES` is the canonical registry (key · label · enumValue ·
 * icon · color · soft · desc) and `CONTRACT_PARTY_ROLE_MATRIX` the canonical
 * type × role legality declaration — the client half of the server's shared
 * `ContractPartyRoleMatrix`. Both mirror `OdysseyData.contractPartyRoles` /
 * `OdysseyData.contractPartyRoleMatrix`; keep all of them in lockstep. Ordinals
 * are a wire and persistence contract: 0 (`Unspecified`) and 5
 * (`ServiceProvider`) are retired holes and must never be reused.
 */

export const CONTRACT_PARTY_ROLES = [
  { key: 'Employee',     label: 'Employee',     enumValue: 1,  icon: 'badge',              color: 'oklch(0.76 0.13 265)', soft: 'oklch(0.76 0.13 265 / 0.16)', desc: 'The person employed under this agreement.' },
  { key: 'Employer',     label: 'Employer',     enumValue: 2,  icon: 'corporate_fare',     color: 'oklch(0.75 0.14 300)', soft: 'oklch(0.75 0.14 300 / 0.16)', desc: 'The party that employs.' },
  { key: 'Buyer',        label: 'Buyer',        enumValue: 3,  icon: 'shopping_bag',       color: 'oklch(0.79 0.14 145)', soft: 'oklch(0.79 0.14 145 / 0.16)', desc: 'The party acquiring under this agreement.' },
  { key: 'Seller',       label: 'Seller',       enumValue: 4,  icon: 'sell',               color: 'oklch(0.80 0.13 90)',  soft: 'oklch(0.80 0.13 90 / 0.16)',  desc: 'The party disposing under this agreement — including supplying a service.' },
  { key: 'Other',        label: 'Other',        enumValue: 6,  icon: 'more_horiz',         color: 'oklch(0.77 0.10 25)',  soft: 'oklch(0.77 0.10 25 / 0.16)',  desc: 'A deliberate role that is none of the others.' },
  { key: 'Landlord',     label: 'Landlord',     enumValue: 7,  icon: 'vpn_key',            color: 'oklch(0.79 0.13 55)',  soft: 'oklch(0.79 0.13 55 / 0.16)',  desc: 'The party letting the property under this tenancy.' },
  { key: 'Tenant',       label: 'Tenant',       enumValue: 8,  icon: 'home',               color: 'oklch(0.78 0.13 35)',  soft: 'oklch(0.78 0.13 35 / 0.16)',  desc: 'The party occupying under this tenancy.' },
  { key: 'Insurer',      label: 'Insurer',      enumValue: 9,  icon: 'shield',             color: 'oklch(0.75 0.14 285)', soft: 'oklch(0.75 0.14 285 / 0.16)', desc: 'The party carrying the risk.' },
  { key: 'Policyholder', label: 'Policyholder', enumValue: 10, icon: 'assignment_ind',     color: 'oklch(0.76 0.13 255)', soft: 'oklch(0.76 0.13 255 / 0.16)', desc: 'The party that holds the policy and owes the premium.' },
  { key: 'Insured',      label: 'Insured',      enumValue: 11, icon: 'health_and_safety',  color: 'oklch(0.77 0.13 215)', soft: 'oklch(0.77 0.13 215 / 0.16)', desc: 'The person, account or thing covered. One member for both party kinds.' },
  { key: 'Beneficiary',  label: 'Beneficiary',  enumValue: 12, icon: 'volunteer_activism', color: 'oklch(0.78 0.13 185)', soft: 'oklch(0.78 0.13 185 / 0.16)', desc: 'The party that receives on the policy. Blocks deletion of the linked contact.' },
  { key: 'Lender',       label: 'Lender',       enumValue: 13, icon: 'savings',            color: 'oklch(0.78 0.13 120)', soft: 'oklch(0.78 0.13 120 / 0.16)', desc: 'The party advancing the money.' },
  { key: 'Borrower',     label: 'Borrower',     enumValue: 14, icon: 'request_quote',      color: 'oklch(0.78 0.13 165)', soft: 'oklch(0.78 0.13 165 / 0.16)', desc: 'The party that owes the money back.' },
  { key: 'Guarantor',    label: 'Guarantor',    enumValue: 15, icon: 'verified_user',      color: 'oklch(0.76 0.07 330)', soft: 'oklch(0.76 0.07 330 / 0.16)', desc: 'A party standing behind another’s obligation. Legal on every type.' },
  { key: 'Broker',       label: 'Broker',       enumValue: 16, icon: 'handshake',          color: 'oklch(0.76 0.07 245)', soft: 'oklch(0.76 0.07 245 / 0.16)', desc: 'An intermediary that arranged the agreement. Legal on every type.' },
  // Object roles — the THING the agreement is about, not a side of it. Role is
  // orthogonal to kind: expected on an Account, equally legal on a Contact.
  { key: 'Object',       label: 'Object',       enumValue: 17, icon: 'category',           color: 'oklch(0.78 0.11 75)',  soft: 'oklch(0.78 0.11 75 / 0.16)',  object: true, desc: 'The thing the agreement concerns — the record it is about, not a side of it.' },
  { key: 'Property',     label: 'Property',     enumValue: 18, icon: 'holiday_village',    color: 'oklch(0.78 0.11 45)',  soft: 'oklch(0.78 0.11 45 / 0.16)',  object: true, desc: 'Real property or goods — the let premises, the purchased asset.' },
  { key: 'Collateral',   label: 'Collateral',   enumValue: 19, icon: 'lock',               color: 'oklch(0.78 0.11 105)', soft: 'oklch(0.78 0.11 105 / 0.16)', object: true, desc: 'Security pledged against an obligation — a loan’s security, or a deposit blocked to secure a lease.' },
  // The Deposit pair — the mirror of Lender/Borrower, separate members, not aliases.
  { key: 'Depositor',    label: 'Depositor',    enumValue: 20, icon: 'account_balance_wallet', color: 'oklch(0.77 0.13 235)', soft: 'oklch(0.77 0.13 235 / 0.16)', desc: 'The party placing the money and entitled to have it returned.' },
  { key: 'Custodian',    label: 'Custodian',    enumValue: 21, icon: 'account_balance',    color: 'oklch(0.76 0.13 350)', soft: 'oklch(0.76 0.13 350 / 0.16)', desc: 'The party holding the deposited money and owing it back — typically the bank, or a landlord holding a rental deposit. Independent of an account\u2019s custodian.' },
];

/**
 * The type × role matrix. Per contract type: `suggested` (legal, offered first)
 * and `allowed` (legal, offered after). Anything absent from both is rejected
 * server-side with a 422 — 78 of the 200 cells are legal. Every type carries at
 * least one suggested role, so the picker's first group is never empty.
 * `Guarantor`, `Broker`, `Other` are universal: legal everywhere, suggested
 * nowhere. `Object` is deliberately absent from Employment and Insurance.
 */
export const CONTRACT_PARTY_ROLE_MATRIX = {
  Employment:   { suggested: ['Employee', 'Employer'],                        allowed: ['Guarantor', 'Broker', 'Other'] },
  Service:      { suggested: ['Buyer', 'Seller'],                             allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Rental:       { suggested: ['Landlord', 'Tenant', 'Property'],              allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Insurance:    { suggested: ['Insurer', 'Policyholder', 'Insured', 'Beneficiary'], allowed: ['Guarantor', 'Broker', 'Other'] },
  Subscription: { suggested: ['Buyer', 'Seller'],                             allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Purchase:     { suggested: ['Buyer', 'Seller', 'Property'],                 allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Loan:         { suggested: ['Lender', 'Borrower', 'Collateral'],            allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Deposit:      { suggested: ['Depositor', 'Custodian'],                      allowed: ['Object', 'Collateral', 'Guarantor', 'Broker', 'Other'] },
  Membership:   { suggested: ['Buyer', 'Seller'],                             allowed: ['Object', 'Guarantor', 'Broker', 'Other'] },
  Other:        { suggested: ['Other'],                                       allowed: ['Employee', 'Employer', 'Buyer', 'Seller', 'Landlord', 'Tenant', 'Insurer', 'Policyholder', 'Insured', 'Beneficiary', 'Lender', 'Borrower', 'Object', 'Property', 'Collateral', 'Depositor', 'Custodian', 'Guarantor', 'Broker'] },
};

/** 'suggested' | 'allowed' | 'rejected' for one (contract type, role) cell. */
export function contractPartyRoleLegality(contractType, roleKey) {
  const cell = CONTRACT_PARTY_ROLE_MATRIX[contractType];
  if (!cell) return 'allowed';
  if (cell.suggested.indexOf(roleKey) !== -1) return 'suggested';
  if (cell.allowed.indexOf(roleKey) !== -1) return 'allowed';
  return 'rejected';
}

/** The legal registry rows for a type, suggested first, each tagged `group`. */
export function contractPartyRolesFor(contractType, roles) {
  const all = roles || CONTRACT_PARTY_ROLES;
  const cell = CONTRACT_PARTY_ROLE_MATRIX[contractType];
  if (!cell) return all.map((r) => ({ ...r, group: 'allowed' }));
  const pick = (keys, group) =>
    keys.map((k) => all.find((r) => r.key === k)).filter(Boolean).map((r) => ({ ...r, group }));
  return pick(cell.suggested, 'suggested').concat(pick(cell.allowed, 'allowed'));
}

export function ContractPartyRoleSelect({
  value,
  onChange,
  label = 'Role',
  contractType,
  roles,
  placeholder = 'Choose a role…',
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  const list = contractType ? contractPartyRolesFor(contractType, roles) : (roles || CONTRACT_PARTY_ROLES);
  const groups = contractType
    ? [{ key: 'suggested', label: `Suggested for ${contractType.toLowerCase()}` }, { key: 'allowed', label: 'Also allowed' }]
    : undefined;
  return (
    <RegistrySelect
      value={value}
      onChange={onChange}
      label={label}
      types={list}
      groups={groups}
      placeholder={placeholder}
      {...rest}
    />
  );
}
