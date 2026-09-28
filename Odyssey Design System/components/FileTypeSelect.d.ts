import * as React from 'react';

/** Which entity the file is attached to — selects the vocabulary. */
export type FileTypeKind = 'account' | 'transaction' | 'taxStatement' | 'property';

export interface FileType {
  /** Enum key — matches the C# enum member and the stored value. */
  key: string;
  label: string;
  /** Numeric enum value (wire + persistence contract). */
  enumValue: number;
  /** Material Icons ligature. */
  icon: string;
  /** Category color (oklch). */
  color: string;
  /** Soft 16% tint of `color`, for avatar backgrounds. */
  soft: string;
}

export declare const ACCOUNT_FILE_TYPES: FileType[];
export declare const TRANSACTION_FILE_TYPES: FileType[];
export declare const TAX_STATEMENT_FILE_TYPES: FileType[];
export declare const PROPERTY_FILE_TYPES: FileType[];
export declare const FILE_TYPE_REGISTRIES: Record<FileTypeKind, { types: FileType[]; filterIcon: string }>;

export interface FileTypeSelectProps {
  /** Vocabulary. Default 'account'. */
  kind?: FileTypeKind;
  /** Selected enum key (e.g. "Statement"). */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Type". */
  label?: string;
  placeholder?: string;
  /** Override / subset the registry. */
  types?: FileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select for a file's document type. `kind` picks the vocabulary
 * (account · transaction · taxStatement · property); each option carries its
 * Material icon in its category color. Delegates to RegistrySelect.
 */
export declare function FileTypeSelect(props: FileTypeSelectProps): JSX.Element;
