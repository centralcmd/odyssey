/**
 * Odyssey DS — RevealPanel
 * ---------------------------------------------------------------------------
 * A segmented toggle whose selection reveals a **connected** panel below it.
 * Closed, it's just the bare `SegmentedControl`; open, the toggle becomes the
 * header of one bordered surface and the body attaches beneath a divider — so
 * a choice and the fields that choice controls read as a single control rather
 * than two disconnected blocks. Extracted from the calendar's recurrence
 * builder (the "Does not repeat / Repeats" toggle + the rule fields it opens),
 * but general: any conditional option group / "advanced" reveal.
 *
 * Open state is `value === openValue` (or membership when `openValue` is an
 * array), unless the consumer passes an explicit `open` (e.g. open only while
 * the toggle is editable). Pass `locked` + `lockedContent` to render a static,
 * read-only header instead of the interactive toggle.
 *
 * The `SegmentedControl` atom is read off the DS namespace at render time.
 */
export function RevealPanel({
  value,
  onChange,
  options,
  ariaLabel,
  openValue,
  open,
  locked = false,
  lockedContent = null,
  children,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const SegmentedControl = NS.SegmentedControl;
  const isOpen = open != null
    ? open
    : (Array.isArray(openValue) ? openValue.includes(value) : value === openValue);

  return (
    <div className={`odc-reveal${isOpen ? ' open' : ''}${className ? ' ' + className : ''}`}>
      <div className="odc-reveal-head">
        {locked
          ? lockedContent
          : (SegmentedControl
            ? <SegmentedControl full ariaLabel={ariaLabel} value={value} onChange={onChange} options={options} />
            : null)}
      </div>
      {isOpen ? <div className="odc-reveal-body">{children}</div> : null}
    </div>
  );
}
