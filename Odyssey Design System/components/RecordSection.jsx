/**
 * Odyssey DS — RecordSection
 * One band inside a record card body: the section divider, an optional notice
 * band, and the section's own view. The two behaviours every section repeated
 * by hand now live here — the empty line when there is nothing to show, and
 * the tinted notice that states why writing is refused (archived record, cap
 * reached) at the place the writing would happen.
 *
 * The view stays custom: pass a table, a tile grid, a list — anything. Pass
 * `empty` and the section renders `emptyText` as the muted line instead.
 *
 * Self-contained by design (bundle components can't import each other), so the
 * divider and the empty line are rendered here with the same classes
 * SectionDivider and EmptyLine use. Styled by .odc-recordsection*.
 */
export function RecordSection({
  label, meta, notice, empty = false, emptyText, emptyAlign = 'start', emptyPad = 'md',
  children, className = '', id,
}) {
  const n = notice && (typeof notice === 'string' ? { text: notice } : notice);
  return (
    <section className={`odc-recordsection${className ? ' ' + className : ''}`} id={id}>
      {label != null ? (
        <div className="odc-sectiondivider">
          <span className="odc-sectiondivider-l">{label}</span>
          <span className="odc-sectiondivider-rule" aria-hidden="true" />
          {meta != null ? <span className="odc-sectiondivider-meta">{meta}</span> : null}
        </div>
      ) : null}
      {n ? (
        <div className={`odc-recordsection-notice${n.tone && n.tone !== 'default' ? ' ' + n.tone : ''}`}>
          <span className="material-icons" aria-hidden="true">{n.icon || (n.tone === 'warning' ? 'inventory_2' : 'info')}</span>
          <div className="odc-recordsection-notice-body">{n.text}</div>
        </div>
      ) : null}
      {empty
        ? <div className={`odc-empty line${emptyAlign === 'center' ? ' center' : ''}${emptyPad !== 'md' ? ` pad-${emptyPad}` : ''}`}>{emptyText}</div>
        : children}
    </section>
  );
}
