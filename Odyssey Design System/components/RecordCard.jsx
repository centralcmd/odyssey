/**
 * Odyssey DS — RecordCard
 * The expandable record card behind every record list: Accounts, Contracts,
 * Tax statements, Budgets, Journal entries,
 * Exchange rates. A dense, scannable header that expands a body in place.
 *
 * ROW HEIGHT IS FIXED ACROSS EVERY RECORD LIST — at least 88px, owned by the
 * component. Insurance is the reference: its meta line carries linked-record
 * pill chips, and a list of plainer records keeps the same rhythm rather than
 * collapsing to a tighter row, so every list scans as one product.
 *
 * The header is the record's IDENTITY — enough to recognise, compare and rule
 * out a record without expanding it. One meta line, ellipsised, never wrapped.
 *
 * The body order is structural, not editorial: `alert` → `details` → `content`
 * → `children` (the sections). Pass only what the record has; you cannot
 * reorder them, which is the point.
 *
 * ONE ACCENT PER RECORD — for what belongs to THIS record. A reference to
 * another object (a linked account, a contact, a policy, a file type) is drawn
 * in that object's own type icon and colour instead, so it stays recognisable as
 * the record it points at.
 *
 * `image` makes the mark the record's own picture instead of its type glyph —
 * a contact's profile photo or company logo. `fit: 'contain'` letterboxes it on
 * a neutral ground (a logo is rarely square and must never be cut); `round`
 * gives the circular mark a photograph wants. The image is decorative here
 * (`alt=""`): the name sits in the very next cell. A load failure falls back to
 * `icon` with nothing surfaced to the user, so pass `icon` as well. The accent
 * does NOT move with it — `--rec` / `--rec-soft` still drive every icon chip
 * and chart in the card; only the mark's content and its own ground change.
 *
 * `accent` / `accentSoft` come from the record's TYPE
 * colour — or its type-equivalent: a categorical registry the record always has
 * exactly one of (a contract's type, a budget item's category).
 * Never derived state (paused/ended/overdue), which stays with the status chip and are set as --rec / --rec-soft on the card, so the header mark, the
 * InfoTile icon chips inside `details`, and any single-series chart inherit the
 * same hue. Omit them and the card falls back to the brand accent.
 *
 * ONE CARD OPEN AT A TIME. A record list holds a single openId and drives every
 * card controlled: open={openId === r.id} onToggle={(o) => setOpenId(o ? r.id : null)}.
 * Bodies are unbounded in height (charts, paged tables, their own dialogs — the
 * card is the product's only record-detail surface), which is only tolerable
 * because opening one record closes its siblings.
 *
 * Works controlled (`open` + `onToggle`) or uncontrolled (`defaultOpen`). The
 * trigger is a real <button aria-expanded> tied to the body via aria-controls;
 * `actions` (an ActionMenu, usually) sits OUTSIDE the trigger so it stays
 * independently clickable. Styled by .odc-record in components.css.
 */
export function RecordCard({
  icon,
  image,
  accent,
  accentSoft,
  name,
  chips,
  meta = [],
  counts = [],
  figure,
  actions,
  alert,
  details,
  content,
  open,
  defaultOpen = false,
  onToggle,
  dimmed = false,
  highlight = false,
  headingLevel = 2,
  className = '',
  children,
}) {
  const isControlled = open !== undefined;
  const [imageFailed, setImageFailed] = React.useState(false);
  React.useEffect(() => { setImageFailed(false); }, [image && image.src]);
  const [internal, setInternal] = React.useState(defaultOpen);
  const isOpen = isControlled ? open : internal;
  const rid = React.useId();
  const bodyId = `${rid}-body`;
  const toggle = () => {
    const next = !isOpen;
    if (!isControlled) setInternal(next);
    if (onToggle) onToggle(next);
  };

  const items = (Array.isArray(meta) ? meta : [meta]).filter((m) => m !== null && m !== undefined && m !== false && m !== '');
  const style = {};
  if (accent) style['--rec'] = accent;
  if (accentSoft) style['--rec-soft'] = accentSoft;

  const cls = [
    'odc-record',
    isOpen ? 'open' : '',
    dimmed ? 'dimmed' : '',
    highlight ? 'flash' : '',
    className,
  ].filter(Boolean).join(' ');

  // The body is the shared RecordBody component (components/RecordBody.jsx) —
  // the same surface a RecordTable detail row renders, so the card and the
  // table expand into an identical panel. Sibling components resolve through
  // the window namespace (they can't import each other); the inline fallback
  // is the same markup, for a bundle that lags a freshly-added component.
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const body = NS.RecordBody
    ? <NS.RecordBody id={bodyId} alert={alert} details={details} content={content}>{children}</NS.RecordBody>
    : (
      <div className="odc-record-body" id={bodyId}>
        {alert}
        {details}
        {content}
        {children}
      </div>
    );

  const trigger = (
    <button
      type="button"
      className="odc-record-trigger"
      style={dimmed ? { opacity: 0.62 } : undefined}
      aria-expanded={isOpen}
      aria-controls={bodyId}
      onClick={toggle}
    >
      {image && image.src && !imageFailed ? (
        <span className={`odc-record-mark img${image.fit === 'contain' ? ' ground' : ''}${image.round ? ' round' : ''}`}>
          <img src={image.src} alt="" loading="lazy" decoding="async" onError={() => setImageFailed(true)} />
        </span>
      ) : icon ? (
        <span className="odc-record-mark">
          <span className="material-icons" aria-hidden="true">{icon}</span>
        </span>
      ) : null}
      <span className="odc-record-id">
        <span className="odc-record-namerow">
          <span className="odc-record-name">{name}</span>
          {chips}
        </span>
        {(items.length > 0 || counts.length > 0) ? (
          <span className="odc-record-meta">
            {items.map((m, i) => (
              <React.Fragment key={i}>
                {i > 0 ? <span className="odc-record-dot" aria-hidden="true">·</span> : null}
                <span className="odc-record-metaitem">{m}</span>
              </React.Fragment>
            ))}
            {counts.length > 0 ? (
              <React.Fragment>
                {items.length > 0 ? <span className="odc-record-dot" aria-hidden="true">·</span> : null}
                <span className="odc-record-counts">
                  {counts.map((c, i) => (
                    <span key={i} title={c.label}>
                      {/^[a-z0-9_]+$/.test(c.icon || '')
                        ? <span className="material-icons" aria-hidden="true">{c.icon}</span>
                        : <span className="odc-record-glyph" aria-hidden="true">{c.icon}</span>}
                      {c.value}
                      <span className="odc-record-sr">{` ${c.label || ''}`}</span>
                    </span>
                  ))}
                </span>
              </React.Fragment>
            ) : null}
          </span>
        ) : null}
      </span>
      {figure ? (
        <span className="odc-record-figure">
          <span className={`odc-record-value${figure.tone ? ' ' + figure.tone : ''}`}>{figure.value}</span>
          {figure.caption ? <span className="odc-record-caption">{figure.caption}</span> : null}
        </span>
      ) : null}
    </button>
  );

  return (
    <div className={cls} style={Object.keys(style).length ? style : undefined}>
      {/* Dimming is applied inline to the TRIGGER, never to the head: an opacity'd
          ancestor also fades the action-menu popover inside it, and an inline
          style beats any stylesheet rule that might still fade the head. */}
      <div className="odc-record-head" style={{ opacity: 1 }}>
        {headingLevel
          ? <div role="heading" aria-level={headingLevel} style={{ display: 'contents' }}>{trigger}</div>
          : trigger}
        <div className="odc-record-ctl" onClick={(e) => e.stopPropagation()}>
          {actions}
          <button
            type="button"
            className="odc-record-chev"
            aria-hidden="true"
            tabIndex={-1}
            onClick={toggle}
          >
            <span className="material-icons">expand_more</span>
          </button>
        </div>
      </div>
      {isOpen ? body : null}
    </div>
  );
}
