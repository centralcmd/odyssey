/**
 * Odyssey DS — SectionDivider
 * The divider that introduces a band or a section inside a record card body:
 * an uppercase letter-spaced label, a hairline rule that takes the remaining
 * width, and an optional mono meta note on the right. Promoted from the
 * Insurance page's .ins-sub treatment, which is now the one section divider.
 *
 * No icon by design — the label carries it, and the record's sections are
 * already named by the header's counts. Styled by .odc-sectiondivider.
 */
export function SectionDivider({ label, meta, className = '', id }) {
  return (
    <div className={`odc-sectiondivider ${className}`.trim()} id={id}>
      <span className="odc-sectiondivider-l">{label}</span>
      <span className="odc-sectiondivider-rule" aria-hidden="true" />
      {meta != null ? <span className="odc-sectiondivider-meta">{meta}</span> : null}
    </div>
  );
}
