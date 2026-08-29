/**
 * Odyssey DS — TagChips
 * Read-only display of a transaction's tags — the multi-tag counterpart to a
 * single `<Chip tone="tag">`. Renders zero, one, or many tag chips inline,
 * collapsing to an em-dash placeholder when empty. With multi-tag transactions
 * (a transaction now carries a list of TransactionTags, not one), this is the
 * canonical way to show that set on a table cell, a detail tile, or anywhere a
 * tag was shown before.
 *
 * `tags` accepts plain strings or {id?, label|name} objects. Pass `max` to cap
 * the visible chips and roll the rest into a "+N" overflow chip (titled with
 * the hidden names) so a dense table column never blows its height; the detail
 * panel omits `max` to show them all. `empty` is the placeholder node when the
 * list is empty (default an em-dash). Styled by `.odc-tagchips`; the chips are
 * the same `.odc-chip.tag` atom used everywhere else.
 */
export function TagChips({ tags = [], max, empty = '—', className = '' }) {
  const norm = (tags || [])
    .map((t) => (typeof t === 'string' ? { label: t } : { id: t.id, label: t.label != null ? t.label : t.name }))
    .filter((t) => t.label != null && t.label !== '');

  if (norm.length === 0) {
    return <span className="odc-tagchips-empty">{empty}</span>;
  }

  const capped = max && norm.length > max;
  const shown = capped ? norm.slice(0, max) : norm;
  const hidden = capped ? norm.slice(max) : [];

  return (
    <span className={`odc-tagchips${className ? ' ' + className : ''}`}>
      {shown.map((t, i) => (
        <span className="odc-chip tag" key={t.id != null ? t.id : `${t.label}-${i}`}>{t.label}</span>
      ))}
      {hidden.length > 0 ? (
        <span className="odc-chip tag odc-tagchips-more" title={hidden.map((t) => t.label).join(', ')}>
          +{hidden.length}
          {/* title= is mouse-only — give AT the hidden names as text. */}
          <span className="sr-only"> more tags: {hidden.map((t) => t.label).join(', ')}</span>
        </span>
      ) : null}
    </span>
  );
}
