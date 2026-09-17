/**
 * Odyssey DS — Timeline / TimelineItem
 * The vertical rail-and-node history list. Both AccountTerms and
 * AccountEstimates had shipped a byte-identical copy of this (`.trm-tl-*` /
 * `.est-tl-*`) as the alternative to their history table; this is that
 * structure promoted once.
 *
 * Each item is: rail node (colored by `color`) · body (label, date, trailing
 * `meta`, `note`) · figures column (`value`, `aside`, `actions`).
 * `current` highlights the value in the income hue.
 */
export function Timeline({ className = '', children, ...rest }) {
  return (
    <div className={`odc-timeline${className ? ' ' + className : ''}`} {...rest}>
      {children}
    </div>
  );
}

export function TimelineItem({
  label,
  date,
  meta,
  note,
  value,
  aside,
  actions,
  color,
  current = false,
  className = '',
  children,
  ...rest
}) {
  const cls = `odc-tl-item odc-rowactions-host${current ? ' current' : ''}${className ? ' ' + className : ''}`;
  const nodeColor = color || (current ? 'var(--finance-income)' : 'var(--mud-palette-text-secondary)');
  return (
    <div className={cls} {...rest}>
      <div className="odc-tl-rail">
        <span className="odc-tl-node" style={{ background: nodeColor, color: nodeColor }} />
      </div>
      <div className="odc-tl-body">
        <div className="odc-tl-top">
          {label != null && <span className="odc-tl-label">{label}</span>}
          {date != null && <span className="odc-tl-date">{date}</span>}
          {meta}
        </div>
        {note ? <div className="odc-tl-note">{note}</div> : null}
        {children}
      </div>
      {(value != null || aside || actions) && (
        <div className="odc-tl-figs">
          {value != null && <span className="odc-tl-value">{value}</span>}
          {aside}
          {actions}
        </div>
      )}
    </div>
  );
}
