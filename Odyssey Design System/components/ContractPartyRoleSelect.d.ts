export interface ContractPartyRoleEntry {
  key: string;
  label: string;
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
  desc?: string;
  /** True for Object / Property / Collateral — the thing the agreement is about, not a side of it. */
  object?: boolean;
  /** Set by contractPartyRolesFor: which matrix group the row fell in. */
  group?: 'suggested' | 'allowed';
}

/** Canonical ContractPartyRole registry — the twenty live members. Ordinals 0 and 5 are retired holes. */
export declare const CONTRACT_PARTY_ROLES: ContractPartyRoleEntry[];

export interface ContractPartyRoleMatrixCell {
  suggested: string[];
  allowed: string[];
}

/** Canonical contract type × party role legality matrix; 78 of 200 cells are legal. */
export declare const CONTRACT_PARTY_ROLE_MATRIX: Record<string, ContractPartyRoleMatrixCell>;

/** Legality of one cell. An unknown contract type reports 'allowed' rather than refusing. */
export declare function contractPartyRoleLegality(
  contractType: string,
  roleKey: string,
): 'suggested' | 'allowed' | 'rejected';

/** The legal roles for a contract type, suggested first, each tagged with its group. */
export declare function contractPartyRolesFor(
  contractType: string,
  roles?: ContractPartyRoleEntry[],
): ContractPartyRoleEntry[];

export interface ContractPartyRoleSelectProps {
  /** Selected ContractPartyRole enum key. No default — a role is required. */
  value?: string;
  onChange?: (key: string, event: React.MouseEvent) => void;
  label?: string;
  /** ContractType key. Given, the list narrows to the legal roles, suggested first. */
  contractType?: string;
  /** Subset / reorder the registry; defaults to CONTRACT_PARTY_ROLES. */
  roles?: ContractPartyRoleEntry[];
  placeholder?: string;
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/** Matrix-aware single-select for the ContractPartyRole vocabulary. */
export declare function ContractPartyRoleSelect(props: ContractPartyRoleSelectProps): JSX.Element;
