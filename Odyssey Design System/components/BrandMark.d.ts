import * as React from 'react';

export interface BrandMarkProps {
  /** Rendered width in px (height follows the aspect). Default 28. */
  size?: number;
  /** Add the spaced-caps ODYSSEY wordmark under the compass. */
  withWordmark?: boolean;
}

/**
 * The Odyssey compass-rose logomark as an inline SVG — exact brand colors,
 * matched to assets/odyssey-logomark.svg. Use it wherever the mark must render
 * at UI scale (drawer lockup, login card, favicicon-adjacent chrome).
 */
export declare function BrandMark(props: BrandMarkProps): JSX.Element;
