/**
 * Odyssey DS — EmptyLine
 * The thin muted sentence that stands in for an empty table frame or an empty
 * record section — the line shape of <EmptyState variant="line">, rendered
 * directly here (bundle components can't import each other). Plain text, no
 * icon, no action: a section that needs a CTA has outgrown the line and should
 * use the panel EmptyState instead. Styled by .odc-empty.line.
 */
export function EmptyLine({ children, text, align = 'start', pad = 'md', className = '' }) {
  return (
    <div className={`odc-empty line${align === 'center' ? ' center' : ''}${pad !== 'md' ? ` pad-${pad}` : ''}${className ? ' ' + className : ''}`}>
      {text != null ? text : children}
    </div>
  );
}
