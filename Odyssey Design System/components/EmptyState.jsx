/**
 * Odyssey DS — EmptyState
 * The centered "nothing here yet" content: icon, one-sentence title,
 * optional description, optional single action (pass a <Button>). Voice:
 * state the absence, offer one CTA. Bare by design — place it inside a Card
 * for a surfaced panel. `mutedIcon` dims the icon tile (for search-no-match).
 * Styled by .odc-empty.
 */
export function EmptyState({ icon = 'inbox', title, desc, action, mutedIcon = false, className = '' }) {
  return (
    <div className={`odc-empty${mutedIcon ? ' muted-ic' : ''}${className ? ' ' + className : ''}`}>
      <div className="odc-empty-ic"><span className="material-icons" aria-hidden="true">{icon}</span></div>
      {title ? <div className="odc-empty-ttl">{title}</div> : null}
      {desc ? <div className="odc-empty-desc">{desc}</div> : null}
      {action ? <div className="odc-empty-actions">{action}</div> : null}
    </div>
  );
}
