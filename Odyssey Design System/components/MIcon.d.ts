export interface MIconProps {
  /** Material Icons ligature name, e.g. "home", "receipt_long". */
  name: string;
  /** Pixel font-size. Defaults to 24 (dense rows use 20, chips 14/18). */
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

/** A single Material Icons glyph from the Odyssey icon font. */
export declare function MIcon(props: MIconProps): JSX.Element;
