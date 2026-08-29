/**
 * Odyssey DS — Collapsible
 * A disclosure: a header row that expands a body in place — the record-detail
 * "Files" / "Transactions" / "Terms" sections, the Budgets item list, the
 * Users role-permissions reveal, advanced-options reveals. Maps to MudCollapse +
 * a header button.
 *
 * Works controlled (`open` + `onToggle`) or uncontrolled (`defaultOpen`). The
 * trigger is a real <button aria-expanded> tied to the body via aria-controls;
 * the leading chevron rotates on open. `icon` (alias `lead`) is a Material Icons
 * ligature; `count` shows a muted pill (e.g. a file count); `action` is an
 * optional control pinned to the right of the header (a "View all" / "New term"
 * button) — it sits OUTSIDE the trigger so it stays independently clickable.
 * `flush` drops the border/inset for an embedded section.
 */
export function Collapsible({
  title,
  icon,
  lead,
  count,
  action,
  open,
  defaultOpen = false,
  onToggle,
  flush = false,
  headingLevel = 2,
  children,
}) {
  const isControlled = open !== undefined;
  const [internal, setInternal] = React.useState(defaultOpen);
  const isOpen = isControlled ? open : internal;
  const rid = React.useId();
  const bodyId = `${rid}-body`;
  const glyph = icon || lead;

  const toggle = () => {
    const next = !isOpen;
    if (!isControlled) setInternal(next);
    if (onToggle) onToggle(next);
  };

  const trigger = (
    <button
      type="button"
      className="odc-collapsible-trigger"
      aria-expanded={isOpen}
      aria-controls={bodyId}
      onClick={toggle}
    >
      <span className="material-icons odc-collapsible-chev" aria-hidden="true">expand_more</span>
      {glyph ? (
        /^[a-z0-9_]+$/.test(glyph)
          ? <span className="material-icons odc-collapsible-lead" aria-hidden="true">{glyph}</span>
          : <span className="odc-collapsible-lead glyph" aria-hidden="true">{glyph}</span>
      ) : null}
      <span className="odc-collapsible-title">{title}</span>
      {count != null ? <span className="odc-collapsible-count">{count}</span> : null}
    </button>
  );

  return (
    <div className={`odc-collapsible${flush ? ' flush' : ''}`} data-open={isOpen ? '' : undefined}>
      <div className="odc-collapsible-head">
        {headingLevel
          ? (
            // The disclosure heading wraps the trigger button (the WAI-ARIA
            // accordion pattern). `display:contents` keeps the button as the
            // flex child it always was, so layout is untouched.
            <div role="heading" aria-level={headingLevel} style={{ display: 'contents' }}>
              {trigger}
            </div>
          )
          : trigger}
        {action ? <div className="odc-collapsible-action" onClick={(e) => e.stopPropagation()}>{action}</div> : null}
      </div>
      {isOpen ? <div className="odc-collapsible-body" id={bodyId}>{children}</div> : null}
    </div>
  );
}
// Marks the action/icon-capable revision (also renders a literal symbol lead
// such as "§" when the value isn't a Material Icons ligature) so a consumer can
// detect whether the compiled bundle already carries it (else fall back).
Collapsible.supportsAction = true;
