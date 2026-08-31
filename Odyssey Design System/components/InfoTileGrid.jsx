/**
 * Odyssey DS — InfoTileGrid
 * The auto-fitting grid the record-card `details` slot is made of. Codifies the
 * rule rather than leaving each page to re-declare a grid: tiles fit at
 * minmax(200px, 1fr), so a full-width card body gets three or four per row
 * instead of two, and an `InfoTile wide` spans the row for prose.
 *
 * The grid holds the record's FULL field set, including fields the collapsed
 * header already shows — at tile scale each value arrives with its own label, so
 * a repeat reads as a field rather than an echo. There is no tile ceiling.
 *
 * A field with no value renders NO tile. The exception is a field whose absence
 * is itself the fact (a subscription with no end date is open-ended) — decided
 * per field, with the reason in a comment, never as the default.
 *
 * Tiles never condition on each other: each renders on its own field, never
 * because a sibling "already shows that". A derived tile (Status, coverage,
 * review state) summarises the record and carries the date its state began; the
 * fields it is computed from keep their own tiles, so a fact cannot vanish when
 * one state takes precedence over another.
 *
 * `dense` drops the icon chips and fits at minmax(152px) — an option for record
 * types whose facts are many and short (tax statements). Decided per record
 * type, never mixed inside one card.
 *
 * The grid reads --rec / --rec-soft from the card, so every tile's icon chip
 * carries the record's type colour with nothing passed per tile. A tile may
 * still override with iconColor/iconSoft, but inside a record card it should
 * not need to. Styled by .odc-tilegrid.
 */
export function InfoTileGrid({ dense = false, className = '', style, children }) {
  const cls = ['odc-tilegrid', dense ? 'dense' : '', className].filter(Boolean).join(' ');
  return <div className={cls} style={style}>{children}</div>;
}
