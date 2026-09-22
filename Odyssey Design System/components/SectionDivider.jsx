/**
 * Odyssey DS — SectionDivider
 * The divider that introduces a band or a section inside a record card body:
 * an uppercase letter-spaced label, a hairline rule that takes the remaining
 * width, and an optional mono meta note on the right. The one section divider for a
 * record's zones.
 *
 * No icon by design — the label carries it, and the record's sections are
 * already named by the header's counts. Styled by .odc-sectiondivider.
 *
 * `headingId` makes the label a programmatic focus destination: it emits the
 * id, `tabindex="-1"` (focusable, but out of the tab order) and heading
 * semantics, so a section that deletes the row holding focus has somewhere
 * honest to send it. Without it the label is a plain span and cannot receive
 * focus at all — a `@key`-style identity guarantee on the rows is a different
 * protection and does not substitute for this one.
 */
export function SectionDivider({ label, meta, className = '', id, headingId, headingLevel = 3 }) {
  return (
    <div className={`odc-sectiondivider ${className}`.trim()} id={id}>
      <span className="odc-sectiondivider-l"
        id={headingId}
        tabIndex={headingId ? -1 : undefined}
        role={headingId ? 'heading' : undefined}
        aria-level={headingId ? headingLevel : undefined}>{label}</span>
      <span className="odc-sectiondivider-rule" aria-hidden="true" />
      {meta != null ? <span className="odc-sectiondivider-meta">{meta}</span> : null}
    </div>
  );
}
