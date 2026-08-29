import * as React from 'react';

export interface TypeOption {
  /** Enum key emitted by onChange. `value` is accepted as an alias. */
  key?: string;
  value?: string;
  label: string;
  /** Material Icons glyph name. */
  icon?: string;
  /** Glyph color. `iconColor` is accepted as an alias. */
  color?: string;
  iconColor?: string;
  /** Group key — used to section the list when `groups` is set. */
  group?: string;
}

export interface TypeGroup {
  key: string;
  label: string;
}

export interface TypeSelectProps {
  /** Selected enum key. */
  value?: string;
  /** Fires with the picked key first, the native event second. */
  onChange?: (key: string, event: React.MouseEvent) => void;
  /** The registry to render. */
  types: TypeOption[];
  /** Optional ordered sections; options are grouped by their `group` field with headings + separators. */
  groups?: TypeGroup[];
  label?: string;
  placeholder?: string;
  help?: string;
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/** The shared registry-backed type picker (themed popover, colored glyph, far-right check, optional groups). Domain wrappers delegate to it. */
export declare function TypeSelect(props: TypeSelectProps): JSX.Element;
