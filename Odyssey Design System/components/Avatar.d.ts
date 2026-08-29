export interface AvatarProps {
  /** Image source. Takes precedence over initials/icon. */
  src?: string;
  /** Alt text for the image, or accessible label for a monogram avatar. */
  alt?: string;
  /** Text monogram (e.g. "JS") when there's no image. */
  initials?: string;
  /** Material Icons ligature name, as a fallback identity glyph. */
  icon?: string;
  /** sm = 28 · md = 40 (default) · lg = 56. */
  size?: 'sm' | 'md' | 'lg';
  /** Rounded-rect (8px) instead of a circle — for account / file / record tiles. */
  square?: boolean;
  /** A named categorical hue — neutral (default) · tide · sea · violet · mint ·
   *  coral — or a custom `{ bg, fg }` color pair for an arbitrary tint. */
  tone?: 'neutral' | 'tide' | 'sea' | 'violet' | 'mint' | 'coral' | { bg: string; fg: string };
  className?: string;
  children?: React.ReactNode;
}

/** A circular identity token — image, monogram, or icon. */
export declare function Avatar(props: AvatarProps): JSX.Element;
