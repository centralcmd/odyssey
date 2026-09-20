/**
 * Odyssey DS — EmptyState
 * Two shapes of "nothing here yet", one component:
 *   variant="panel" (default) — centered icon tile, one-sentence title,
 *     optional description, optional single action (pass a <Button>).
 *     Bare by design — place it inside a Card for a surfaced panel.
 *     `mutedIcon` dims the icon tile (for search-no-match).
 *   variant="line" — the thin muted sentence that sits inside a table frame
 *     or a record section where a panel would overpower the row it replaces.
 *     No icon, no action: it states the absence and nothing else. Use the
 *     EmptyLine alias for that case.
 * Styled by .odc-empty.
 */
export function EmptyState({
  icon = 'inbox', title, desc, action, mutedIcon = false,
  variant = 'panel', align = 'start', pad = 'md', className = '',
}) {
  if (variant === 'line') {
    return (
      <div className={`odc-empty line${align === 'center' ? ' center' : ''}${pad !== 'md' ? ` pad-${pad}` : ''}${className ? ' ' + className : ''}`}>
        {title != null ? title : desc}
      </div>
    );
  }
  return (
    <div className={`odc-empty${mutedIcon ? ' muted-ic' : ''}${className ? ' ' + className : ''}`}>
      <div className="odc-empty-ic"><span className="material-icons" aria-hidden="true">{icon}</span></div>
      {title ? <div className="odc-empty-ttl">{title}</div> : null}
      {desc ? <div className="odc-empty-desc">{desc}</div> : null}
      {action ? <div className="odc-empty-actions">{action}</div> : null}
    </div>
  );
}
