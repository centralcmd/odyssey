// Geometry + pointer-capture helpers for OdsCalTimeGrid. The drag/resize math lives in C#
// (Blazor WASM, so pointermove handling is in-process and cheap); JS only supplies the one thing
// C# can't compute — the live bounding rect of the grid body — and routes pointer events to it via
// setPointerCapture so a drag keeps tracking when the cursor leaves the element.

export function bodyRect(el) {
  if (!el) return null;
  const r = el.getBoundingClientRect();
  return { top: r.top, left: r.left, width: r.width, height: r.height };
}

export function capture(el, pointerId) {
  try { el.setPointerCapture(pointerId); } catch { /* pointer already released */ }
}

export function release(el, pointerId) {
  try { el.releasePointerCapture(pointerId); } catch { /* already released */ }
}

// Auto-scroll the hourly body so a given pixel offset (≈ current time) opens near the top.
export function scrollTo(el, top) {
  if (el) el.scrollTop = top;
}
