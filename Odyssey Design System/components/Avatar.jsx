/**
 * Odyssey DS — Avatar
 * A circular identity token. Pass `src` for an image, or `initials` / a child
 * for a text monogram, or `icon` for a Material glyph. size = sm (28) /
 * md (40, default) / lg (56). `square` gives a rounded-rect (8px) instead of a
 * circle — for account / file / record tiles.
 *
 * `fit` decides how an image meets the frame. `cover` (default) square-crops —
 * a person's photograph. `contain` LETTERBOXES the image on a neutral ground
 * with a 3:1 boundary — a company logo, which carries transparency, is rarely
 * square and must never be cut. Choose it from what the image IS, not from the
 * shape: a wordmark cropped to a circle is unrecognisable.
 *
 * The image is a plain <img> with `loading="lazy"` and `decoding="async"`: a
 * 50-row list must not decode fifty pictures to show ten. `onError` fires when
 * the image cannot load (deleted file, revoked access, offline) — the consumer
 * uses it to swap back to its identity glyph, silently. Without it a broken
 * image renders the browser's broken-image icon inside the frame.
 *
 * `tone` is either a named categorical hue — neutral (default) · tide · sea ·
 * violet · mint · coral — or a custom `{ bg, fg }` object for an arbitrary
 * pair (e.g. a file-kind color). Named tones are theme-aware soft tints.
 * Styled by .odc-avatar.
 */
export function Avatar({ src, alt = '', initials, icon, size = 'md', tone = 'neutral', square = false, fit = 'cover', onError, className = '', style, children }) {
  const namedTone = typeof tone === 'string';
  const contain = !!src && fit === 'contain';
  const cls = `odc-avatar${size !== 'md' ? ' ' + size : ''}${square ? ' sq' : ''}${contain ? ' contain' : ''}${namedTone && tone !== 'neutral' ? ' ' + tone : ''}${className ? ' ' + className : ''}`;
  const toneStyle = !namedTone && tone ? { background: tone.bg, color: tone.fg } : null;
  let inner;
  if (src) inner = <img src={src} alt={alt} loading="lazy" decoding="async" onError={onError} />;
  else if (icon) inner = <span className="material-icons" aria-hidden="true">{icon}</span>;
  else inner = initials || children;
  return (
    <span
      className={cls}
      style={{ ...toneStyle, ...style }}
      role={src ? undefined : 'img'}
      aria-label={src ? undefined : (alt || undefined)}
    >
      {inner}
    </span>
  );
}
