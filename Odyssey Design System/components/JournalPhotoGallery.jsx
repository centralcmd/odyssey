/**
 * Odyssey DS — JournalPhotoGallery
 * A responsive thumbnail grid over a journal entry's photos. Each tile is a
 * keyboard-focusable control with an accessible name (the photo title/filename)
 * that opens the full-res file via the host's handler. v4: a journal entry
 * links a library Photo (by PhotoId); the read enriches each link with the
 * library Photo's FileId, from which the tile builds the Files content URL
 * (in production, `src`). v1 has no EXIF / caption / lightbox / thumbnail pipeline —
 * a tile renders the image at native fidelity with `loading="lazy"` and a
 * capped display size, or, when no renderable `src` is supplied (mock / not yet
 * loaded), a striped placeholder carrying a monospace `photo` label + filename.
 *
 * The count is announced as text in the section heading (never colour/icon
 * alone). Tiles are a real `<button>` so tab-order, Enter/Space activation, and
 * the focus ring come for free.
 *
 * Props: `photos` [{ id, name, src? }], `onOpen(photo)`, `title` (heading, set
 * '' to hide), `minTile` (px min tile width, default 120), `emptyText`.
 * Styled by `.odc-photogrid`.
 */

export function JournalPhotoGallery({
  photos = [],
  onOpen,
  title = 'Photos',
  minTile = 120,
  emptyText = 'No photos.',
  className = '',
  style,
}) {
  const count = photos.length;
  return (
    <section
      className={`odc-photogrid${className ? ' ' + className : ''}`}
      aria-label={title ? `${title} (${count})` : undefined}
      style={style}>
      {title ? (
        <div className="odc-photogrid-head">
          <span className="material-icons" aria-hidden="true">photo_library</span>
          <span className="odc-photogrid-title">{title}</span>
          <span className="odc-photogrid-count">{count}</span>
        </div>
      ) : null}

      {count === 0 ? (
        <div className="odc-photogrid-empty">{emptyText}</div>
      ) : (
        <ul
          className="odc-photogrid-list"
          style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${minTile}px, 1fr))` }}>
          {photos.map((p) => (
            <li key={p.id} className="odc-photogrid-item">
              <button
                type="button"
                className="odc-photogrid-tile"
                aria-label={`Open photo ${p.name || p.id}`}
                onClick={() => onOpen && onOpen(p)}>
                {p.src ? (
                  <img className="odc-photogrid-img" src={p.src} alt={p.name || ''} loading="lazy" />
                ) : (
                  <span className="odc-photogrid-ph" aria-hidden="true">
                    <span className="odc-photogrid-ph-label mono">photo</span>
                  </span>
                )}
                <span className="odc-photogrid-name mono" title={p.name || p.id}>{p.name || p.id}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
