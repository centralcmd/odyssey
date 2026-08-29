/**
 * Odyssey DS — Avatar
 * A circular identity token. Pass `src` for an image, or `initials` / a child
 * for a text monogram, or `icon` for a Material glyph. size = sm (28) /
 * md (40, default) / lg (56). `square` gives a rounded-rect (8px) instead of a
 * circle — for account / file / record tiles.
 *
 * `tone` is either a named categorical hue — neutral (default) · tide · sea ·
 * violet · mint · coral — or a custom `{ bg, fg }` object for an arbitrary
 * pair (e.g. a file-kind color). Named tones are theme-aware soft tints.
 * Styled by .odc-avatar.
 */
export function Avatar({ src, alt = '', initials, icon, size = 'md', tone = 'neutral', square = false, className = '', style, children }) {
  const namedTone = typeof tone === 'string';
  const cls = `odc-avatar${size !== 'md' ? ' ' + size : ''}${square ? ' sq' : ''}${namedTone && tone !== 'neutral' ? ' ' + tone : ''}${className ? ' ' + className : ''}`;
  const toneStyle = !namedTone && tone ? { background: tone.bg, color: tone.fg } : null;
  let inner;
  if (src) inner = <img src={src} alt={alt} />;
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
