/**
 * Odyssey DS — ReferenceNumber
 * Read-only display of a counterparty identifier (a contract's
 * `referenceNumber`): a `tag` glyph and the value in mono, exactly as stored.
 *
 *   • `null` / empty renders NOTHING. A contract with no number on file is an
 *     ordinary record, so there is no "No reference" placeholder, no dash and
 *     no muted tile — absence is the healthy steady state.
 *   • `highlight` marks the case-insensitive substring a list search matched,
 *     so a row found only by its reference number shows why it is in the list.
 *   • `copyable` adds a copy button (the reconciliation use case: the number
 *     goes into a phone call, an email, a support ticket). The value is never
 *     truncated when copied; long values wrap in `size="md"` and ellipsize
 *     with a full-value `title` in `size="sm"`.
 */
export function ReferenceNumber({ value, highlight, copyable = false, size = 'md', showIcon, className = '' }) {
  // The glyph labels a bare meta-line value; a detail tile already carries its own `tag` chip.
  const withIcon = showIcon != null ? showIcon : size === 'sm';
  const [copied, setCopied] = React.useState(false);
  const timer = React.useRef(null);
  React.useEffect(() => () => clearTimeout(timer.current), []);
  if (value == null || String(value).trim() === '') return null;
  const text = String(value);

  let body = text;
  const needle = (highlight || '').trim();
  if (needle) {
    const i = text.toLowerCase().indexOf(needle.toLowerCase());
    if (i >= 0) {
      body = (
        <React.Fragment>
          {text.slice(0, i)}
          <mark className="odc-refnum-mark">{text.slice(i, i + needle.length)}</mark>
          {text.slice(i + needle.length)}
        </React.Fragment>
      );
    }
  }

  const copy = (e) => {
    e.stopPropagation();
    const done = () => {
      setCopied(true);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 1400);
    };
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done, done);
    else done();
  };

  return (
    <span className={`odc-refnum ${size}${className ? ' ' + className : ''}`} title={size === 'sm' ? text : undefined}>
      {withIcon ? <span className="material-icons odc-refnum-icon" aria-hidden="true">tag</span> : null}
      <span className="odc-refnum-text"><span className="odc-sr-only">Reference number </span>{body}</span>
      {copyable ? (
        <button type="button" className={`odc-refnum-copy${copied ? ' done' : ''}`} onClick={copy}
          aria-label={copied ? 'Reference number copied' : 'Copy reference number'}>
          <span className="material-icons" aria-hidden="true">{copied ? 'check' : 'content_copy'}</span>
        </button>
      ) : null}
    </span>
  );
}
