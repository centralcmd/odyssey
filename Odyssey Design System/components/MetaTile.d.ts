import * as React from 'react';

export interface MetaTileProps {
  /** Uppercase field label. */
  label: React.ReactNode;
  /** Field value — text or any node (e.g. a <Chip> for a status). Long values wrap to multiple lines inside the tile. */
  value: React.ReactNode;
  /** Render the value in Roboto Mono (IDs, codes, timestamps). */
  mono?: boolean;
  /** Extra class on the value element. */
  valueClass?: string;
}

/**
 * A labelled read-only field well for the expanded-record detail grid.
 * Tiles always sit two per row (the `.meta-grid` columns) — there is no
 * spanning variant; multiline values wrap inside their tile.
 */
export declare function MetaTile(props: MetaTileProps): JSX.Element;
