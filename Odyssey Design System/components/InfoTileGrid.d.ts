import * as React from 'react';

export interface InfoTileGridProps {
  /** Seven or more facts: drop the icon chips and fit tiles at minmax(152px). Per record type, never mixed inside one card. */
  dense?: boolean;
  className?: string;
  style?: React.CSSProperties;
  /** InfoTiles. Six is the target, eight the ceiling; one may be `wide`. */
  children?: React.ReactNode;
}

/** Auto-fitting grid of InfoTiles — the record card's `details` slot. Inherits the card's accent. */
export declare function InfoTileGrid(props: InfoTileGridProps): JSX.Element;
