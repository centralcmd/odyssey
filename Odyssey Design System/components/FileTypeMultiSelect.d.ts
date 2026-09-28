import { FileType, FileTypeKind } from './FileTypeSelect';

export interface FileTypeMultiSelectProps {
  /** Vocabulary. Default 'account'. */
  kind?: FileTypeKind;
  /** Selected enum keys. */
  value?: string[];
  onChange?: (values: string[]) => void;
  /** Trigger label. Defaults to "Any type". */
  label?: string;
  /** Trigger glyph. Defaults per kind. */
  icon?: string;
  align?: 'left' | 'right';
  /** Override / subset the registry (e.g. only the types present in the list). */
  types?: FileType[];
}

/**
 * Checkbox-list filter for document type — each row carries its Material icon
 * in its category color, count badge on the trigger. Delegates to RegistryMultiSelect.
 */
export declare function FileTypeMultiSelect(props: FileTypeMultiSelectProps): JSX.Element;
