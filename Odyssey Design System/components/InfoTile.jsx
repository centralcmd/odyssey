/**
 * Odyssey DS — InfoTile
 * A labeled fact/stat tile: an icon chip + uppercase label on top, a headline
 * value, and an optional muted foot caption — on an elevated card. The richer
 * sibling of MetaTile (which is a bare label/value well) and StatTile (a bare
 * figure). Used for policy/account facts and current-state snapshots where each
 * fact reads as its own tile in a responsive grid.
 *
 * The icon chip defaults to the brand accent; pass `iconColor` (+ optional
 * `iconSoft` background) to tint it per-tile — e.g. a category color. To re-tint
 * a whole grid of tiles at once, set `--odc-infotile-accent` /
 * `--odc-infotile-accent-soft` on the grid container instead.
 *
 * `valueVariant`: 'mono' (default — numbers/IDs/dates, tabular) · 'text'
 * (proportional, for names/labels) · 'sm' (smaller mono, for dates). `wide` makes
 * the tile span all grid columns and lets its value wrap (e.g. a notes tile).
 * Styled by .odc-infotile in components.css.
 */
export function InfoTile({
  icon,
  iconColor,
  iconSoft,
  label,
  value,
  foot,
  valueVariant = 'mono',
  wide = false,
  elevated = true,
  className = '',
  style,
}) {
  const cls = ['odc-infotile', elevated ? 'elevated' : '', wide ? 'wide' : '', className].filter(Boolean).join(' ');
  const icStyle = iconColor ? { background: iconSoft || undefined, color: iconColor } : undefined;
  return (
    <div className={cls} style={style}>
      <div className="odc-infotile-top">
        {icon ? (
          <span className="odc-infotile-ic" style={icStyle}>
            <span className="material-icons" aria-hidden="true">{icon}</span>
          </span>
        ) : null}
        {label ? <span className="odc-infotile-k">{label}</span> : null}
      </div>
      <div className={`odc-infotile-v ${valueVariant}`}>{value}</div>
      {foot ? <div className="odc-infotile-foot">{foot}</div> : null}
    </div>
  );
}
