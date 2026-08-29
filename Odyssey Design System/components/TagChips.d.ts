import * as React from 'react';

export interface TagChipsItem {
  /** Stable id, used as the React key and the source for de-duping. */
  id?: string;
  /** Display text. `name` is accepted as an alias (matches the TransactionTag DTO). */
  label?: string;
  name?: string;
}

export interface TagChipsProps {
  /** The tags to render — plain strings or {id?, label|name} objects. */
  tags?: Array<TagChipsItem | string>;
  /** Cap the visible chips; the remainder collapse into a "+N" overflow chip. Omit to show all. */
  max?: number;
  /** Placeholder node shown when `tags` is empty. Default an em-dash. */
  empty?: React.ReactNode;
  className?: string;
}

/** Read-only inline display of a transaction's tag set (zero / one / many). */
export declare function TagChips(props: TagChipsProps): JSX.Element;
