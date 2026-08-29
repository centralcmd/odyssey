/**
 * Odyssey DS — Modal
 * The dialog shell: scrim + centered surface (12px radius) with header
 * (title/sub + close), scrollable body, and a right-aligned footer for
 * actions. `wide` switches to the 1240px variant (file-analysis grid).
 * Esc and scrim-click call onClose. Styled by .odc-modal / .odc-scrim.
 *
 * a11y: on open it stores the previously-focused element, locks body scroll,
 * and moves focus into the dialog; Tab is trapped within the surface; on close
 * focus is restored to the opener. The dialog is named via aria-labelledby
 * pointing at the rendered title (so a JSX title still labels it); with no
 * title, pass `ariaLabel`. An optional `icon` renders a tinted lead tile left
 * of the title — a Material Icons ligature, or any non-ligature character
 * (e.g. "§") rendered as a typographic glyph; `iconTone` ('brand' | 'warning' | 'error',
 * default 'brand') tints it — use 'warning'/'error' for destructive/confirm dialogs.
 */
const ODC_FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function Modal({ open = true, title, subtitle, icon, iconTone = 'brand', onClose, footer, wide = false, ariaLabel, className = '', bodyClassName = '', children }) {
  const dialogRef = React.useRef(null);
  const prevFocus = React.useRef(null);
  const titleId = React.useId();
  const subId = React.useId();

  React.useEffect(() => {
    if (!open) return undefined;
    prevFocus.current = document.activeElement;
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const node = dialogRef.current;
    const focusables = () =>
      node ? Array.from(node.querySelectorAll(ODC_FOCUSABLE)).filter((el) => el.offsetParent !== null) : [];

    const first = focusables();
    // Respect an autoFocus field that already took focus; otherwise prefer the
    // first focusable in the BODY (skip the header close button) so a form
    // dialog opens on its first input.
    if (!(node && node.contains(document.activeElement))) {
      const bodyEl = node ? node.querySelector('.odc-modal-body') : null;
      const bodyFocusables = bodyEl
        ? Array.from(bodyEl.querySelectorAll(ODC_FOCUSABLE)).filter((el) => el.offsetParent !== null)
        : [];
      if (bodyFocusables.length) bodyFocusables[0].focus();
      else if (first.length) first[0].focus();
      else if (node) node.focus();
    }

    const onKey = (e) => {
      if (e.key === 'Escape') {
        if (onClose) onClose();
        return;
      }
      if (e.key !== 'Tab') return;
      const els = focusables();
      if (!els.length) {
        e.preventDefault();
        if (node) node.focus();
        return;
      }
      const top = els[0];
      const bottom = els[els.length - 1];
      const active = document.activeElement;
      if (e.shiftKey) {
        if (active === top || !node.contains(active)) {
          e.preventDefault();
          bottom.focus();
        }
      } else if (active === bottom || !node.contains(active)) {
        e.preventDefault();
        top.focus();
      }
    };

    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
      if (prevFocus.current && prevFocus.current.focus) prevFocus.current.focus();
    };
  }, [open, onClose]);

  if (!open) return null;

  const onScrim = (e) => { if (e.target === e.currentTarget && onClose) onClose(); };

  const dialog = (
    <div className="odc-scrim" onMouseDown={onScrim}>
      <div
        ref={dialogRef}
        tabIndex={-1}
        className={`odc-modal${wide ? ' wide' : ''}${className ? ' ' + className : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={title ? titleId : undefined}
        aria-label={title ? undefined : ariaLabel}
        aria-describedby={subtitle ? subId : undefined}
      >
        {(title || onClose) ? (
          <div className="odc-modal-head">
            {icon ? (
              <div className={`odc-modal-lead${iconTone && iconTone !== 'brand' ? ' ' + iconTone : ''}`}>
                {/^[a-z0-9_]+$/.test(icon)
                  ? <span className="material-icons" aria-hidden="true">{icon}</span>
                  : <span className="odc-modal-lead-glyph" aria-hidden="true">{icon}</span>}
              </div>
            ) : null}
            <div className="odc-modal-titles">
              {title ? <div className="odc-modal-title" id={titleId}>{title}</div> : null}
              {subtitle ? <div className="odc-modal-sub" id={subId}>{subtitle}</div> : null}
            </div>
            {onClose ? (
              <button type="button" className="odc-iconbtn" aria-label="Close" onClick={onClose}>
                <span className="material-icons" aria-hidden="true">close</span>
              </button>
            ) : null}
          </div>
        ) : null}
        <div className={`odc-modal-body${bodyClassName ? ' ' + bodyClassName : ''}`}>{children}</div>
        {footer ? <div className="odc-modal-foot">{footer}</div> : null}
      </div>
    </div>
  );

  // Portal to <body> so the dialog escapes any overflow/transform ancestor
  // (cards, collapsibles, scaled stages). Falls back to in-place rendering
  // when ReactDOM isn't global (it always is in DS consumers).
  if (typeof document !== 'undefined' && typeof ReactDOM !== 'undefined' && ReactDOM.createPortal) {
    return ReactDOM.createPortal(dialog, document.body);
  }
  return dialog;
}
