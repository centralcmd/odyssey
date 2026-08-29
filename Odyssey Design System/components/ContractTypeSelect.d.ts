export interface ContractTypeEntry {
  key: string;
  label: string;
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
}

/** Canonical ContractType registry — mirrors OdysseyData.contractTypes and the C# ContractType enum. */
export declare const CONTRACT_TYPES: ContractTypeEntry[];

export interface ContractTypeSelectProps {
  /** Selected ContractType enum key. */
  value?: string;
  /** Fires with the picked key first, the native event second. */
  onChange?: (key: string, event: React.MouseEvent) => void;
  label?: string;
  placeholder?: string;
  /** Subset / reorder the registry; defaults to CONTRACT_TYPES. */
  types?: ContractTypeEntry[];
  help?: string;
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/** Single-select pre-wired to the ContractType vocabulary; delegates to the shared TypeSelect. */
export declare function ContractTypeSelect(props: ContractTypeSelectProps): JSX.Element;
