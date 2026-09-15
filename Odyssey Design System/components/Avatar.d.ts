export interface AvatarProps {
  /** Image source. Takes precedence over initials/icon. Rendered as a plain lazy, async-decoding <img>. */
  src?: string;
  /** Alt text for the image, or accessible label for a monogram avatar. Empty string where the adjacent text already names the subject. */
  alt?: string;
  /** Text monogram (e.g. "JS") when there's no image. */
  initials?: string;
  /** Material Icons ligature name, as a fallback identity glyph. */
  icon?: string;
  /** sm = 28 · md = 40 (default) · lg = 56. */
  size?: 'sm' | 'md' | 'lg';
  /** Rounded-rect (8px) instead of a circle — for account / file / record tiles. */
  square?: boolean;
  /** How an image meets the frame. `cover` (default) square-crops — a photograph. `contain` letterboxes it on a neutral ground with a 3:1 boundary — a logo, which carries transparency and must never be cut. */
  fit?: 'cover' | 'contain';
  /** Fires when the image fails to load. Swap to the identity glyph here; the failure is never surfaced to the user. */
  onError?: (event: React.SyntheticEvent<HTMLImageElement>) => void;
  /** A named categorical hue — neutral (default) · tide · sea · violet · mint ·
   *  coral — or a custom `{ bg, fg }` color pair for an arbitrary tint. */
  tone?: 'neutral' | 'tide' | 'sea' | 'violet' | 'mint' | 'coral' | { bg: string; fg: string };
  className?: string;
  children?: React.ReactNode;
}

/** A circular identity token — image, monogram, or icon. */
export declare function Avatar(props: AvatarProps): JSX.Element;
