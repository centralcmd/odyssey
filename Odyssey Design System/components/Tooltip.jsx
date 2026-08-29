/**
 * Odyssey DS — Tooltip
 * A hover/focus label on an inverted bubble (both themes). Wrap any single
 * focusable trigger; the bubble shows on hover and on keyboard focus, so
 * keyboard users get it too.
 *
 * The bubble is portaled to <body> and positioned against the trigger, so it
 * escapes any overflow:hidden/auto ancestor (a toolbar, table cell, card) and
 * never clips. It sits above by default and flips below when there isn't room,
 * clamping to the viewport so it stays on-screen at the edges. Repositions on
 * scroll / resize while shown.
 *
 * a11y: the bubble carries role="tooltip" + a stable id, and the trigger is
 * cloned to reference it via aria-describedby, so screen readers announce the
 * label when the trigger gets focus. Esc dismisses the bubble without moving
 * focus (WCAG 1.4.13) and without closing an enclosing Modal. Keep `label`
 * terse (one line). Touch is not a tooltip surface — put essential info in a
 * visible label instead.
 */
export function Tooltip({ label, children }) {
  const { useState, useRef, useLayoutEffect, useCallback, useId } = React;
  const id = useId();
  const tipId = `tip-${id}`;
  const [open, setOpen] = useState(false);
  const [box, setBox] = useState(null);
  const anchorRef = useRef(null);
  const bubbleRef = useRef(null);

  const place = useCallback(() => {
    const a = anchorRef.current;
    if (!a) return;
    const r = a.getBoundingClientRect();
    const b = bubbleRef.current;
    const bw = b ? b.offsetWidth : 0;
    const bh = b ? b.offsetHeight : 0;
    const vw = window.innerWidth;
    const gap = 8;
    let top;
    let side;
    if (r.top - gap - bh >= 0) {
      top = r.top - gap - bh;
      side = 'top';
    } else {
      top = r.bottom + gap;
      side = 'bottom';
    }
    let left = r.left + r.width / 2 - bw / 2;
    left = Math.min(Math.max(6, left), Math.max(6, vw - bw - 6));
    setBox({ top, left, side });
  }, []);

  useLayoutEffect(() => {
    if (!open) { setBox(null); return undefined; }
    place();
    const onScroll = () => place();
    // Esc dismisses the tooltip (WCAG 1.4.13) without disturbing anything
    // else — capture + stopPropagation so a Modal underneath stays open.
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', place);
    document.addEventListener('keydown', onKey, true);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', place);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open, place]);

  const onlyChild = React.isValidElement(children) ? children : null;
  const trigger = onlyChild
    ? React.cloneElement(onlyChild, {
        'aria-describedby': [onlyChild.props['aria-describedby'], tipId].filter(Boolean).join(' '),
      })
    : children;

  const floatStyle = box
    ? {
        position: 'fixed', top: box.top, left: box.left,
        right: 'auto', bottom: 'auto', transform: 'none', margin: 0,
        opacity: 1, visibility: 'visible', pointerEvents: 'none', zIndex: 80,
      }
    : { position: 'fixed', top: 0, left: 0, visibility: 'hidden', pointerEvents: 'none' };

  return (
    <span
      className="odc-tip"
      ref={anchorRef}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocusCapture={() => setOpen(true)}
      onBlurCapture={() => setOpen(false)}
    >
      {trigger}
      {open
        ? ReactDOM.createPortal(
          <span ref={bubbleRef} role="tooltip" id={tipId} className="odc-tip-bubble floating" style={floatStyle}>
            {label}
          </span>,
          document.body,
        )
        : null}
    </span>
  );
}
