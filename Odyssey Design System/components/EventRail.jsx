/**
 * Odyssey DS — EventRail / EventRailItem / EventRailMarker
 *
 * The CONTINUOUS-rail history list: one unbroken line running the full height
 * of the track, with icon nodes sitting on it. Distinct from `Timeline`, and
 * both are kept deliberately:
 *
 *   Timeline   — a rail segment per item, a plain dot node, a figures column.
 *                For effective-dated record tables (terms, estimates), where
 *                each row is a value and the right-hand column carries it.
 *   EventRail  — ONE line behind every row, a 32px circle carrying a glyph,
 *                markers (years, endpoints) sitting on the same line between
 *                rows. For a log of things that happened, where the reader
 *                follows the line rather than comparing figures.
 *
 * A Timeline cannot do this: its rail restarts per item, so a marker between
 * two rows breaks the line. Hence the second component rather than a flag.
 *
 * Composition:
 *   <EventRail capTop capEnd>
 *     <EventRailMarker tone="open">Today · 20 Sept 2026</EventRailMarker>
 *     <EventRailItem icon="campaign" title="…" date="…" desc="…" actions={…}>
 *       …meta, revealed on hover…
 *     </EventRailItem>
 *     <EventRailMarker>2025</EventRailMarker>
 *   </EventRail>
 *
 * `capTop` / `capEnd` say whether THIS page holds the real start/end of the
 * log. An uncapped end fades the line out instead of cutting it — a paged
 * middle genuinely continues, and a hard stop would claim otherwise.
 */
export function EventRail({ capTop = false, capEnd = false, className = '', children, ...rest }) {
  const cls = `odc-er${capTop ? ' capped-top' : ''}${capEnd ? ' capped-end' : ''}${className ? ' ' + className : ''}`;
  return <div className={cls} {...rest}>{children}</div>;
}

/**
 * A point ON the line that is not an event: a year, the present, the day the
 * record was created. `tone` picks the node — a small divider dot (`tick`),
 * a hollow ring (`open`, for "now" / an open end) or a solid dot (`filled`,
 * for a closed end).
 *
 * `meta` is provenance for the marker itself, revealed on the same hover as an
 * item's — use it where the endpoint has an author or a source worth naming.
 */
export function EventRailMarker({ tone = 'tick', meta, className = '', children, ...rest }) {
  return (
    <div className={`odc-er-marker ${tone}${meta ? ' has-meta' : ''}${className ? ' ' + className : ''}`} {...rest}>
      <span>{children}</span>
      {meta ? <div className="odc-er-meta">{meta}</div> : null}
    </div>
  );
}

/**
 * One entry. The node is neutral by default and carries the glyph for the
 * entry's kind; pass `color` only where the kind's hue is genuinely the
 * fastest read, since a column of nine hues competes with status colour.
 */
export function EventRailItem({
  icon,
  iconLabel,
  title,
  date,
  desc,
  actions,
  color,
  selected = false,
  className = '',
  children,
  ...rest
}) {
  const cls = `odc-er-item odc-rowactions-host${selected ? ' sel' : ''}${className ? ' ' + className : ''}`;
  return (
    <div className={cls} {...rest}>
      <span className="odc-er-node" style={color ? { '--odc-er-node-fg': color } : undefined}
        title={iconLabel} aria-label={iconLabel} role={iconLabel ? 'img' : undefined}>
        <span className="material-icons">{icon}</span>
      </span>
      <div className="odc-er-body">
        <div className="odc-er-top">
          {title != null && <span className="odc-er-title">{title}</span>}
          {date != null && <span className="odc-er-date">{date}</span>}
          {actions}
        </div>
        {desc ? <div className="odc-er-desc">{desc}</div> : null}
        {children}
      </div>
    </div>
  );
}
