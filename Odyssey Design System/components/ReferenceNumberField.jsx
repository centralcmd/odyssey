/**
 * Odyssey DS — ReferenceNumberField + REFERENCE_NUMBER_RULES
 * The single-line input for a counterparty's identifier — a contract's
 * `referenceNumber` (policy, agreement, customer or order number).
 *
 * The rules are the client half of the server's, not new ones:
 *   • ≤ 64 characters ([StringLength(64)]). Counted in UTF-16 code units,
 *     which is what both `String.length` and .NET's StringLength count, so the
 *     counter and the 400 can never disagree about a non-BMP character.
 *   • No Unicode Cc / Cf / Co / Cn character — the same four categories the
 *     DTO's [RegularExpression] denies. Cs is deliberately NOT denied, so
 *     emoji and rare CJK above U+FFFF are accepted (the regex runs with the
 *     `u` flag, over code points).
 *   • Trim, blank → null (the service's NormalizeReferenceNumber). The field
 *     trims on blur so what the user sees is what will be stored; interior
 *     spacing, case and separators are kept verbatim.
 *
 * No native `maxLength`: a pasted 70-character number would be silently cut
 * to 64 and saved as a DIFFERENT number. The field takes the whole paste,
 * the counter turns red and the error names the limit; the dialog refuses
 * to save until it is fixed.
 *
 * A hidden character is named by code point and offered a one-click removal
 * ("Remove it"), because the user cannot see what they are being asked to
 * delete — it almost always arrives with a paste from a PDF or an email.
 *
 * Error copy never echoes the value, matching the server's 400 bodies.
 */
const RN_MAX = 64;
const RN_HIDDEN = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}]/u;
const RN_HIDDEN_ALL = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}]/gu;
const RN_NAMES = {
  0x09: 'tab', 0x0a: 'line break', 0x0d: 'carriage return', 0x00: 'null character',
  0x200b: 'zero-width space', 0x200c: 'zero-width non-joiner', 0x200d: 'zero-width joiner',
  0x200e: 'left-to-right mark', 0x200f: 'right-to-left mark', 0x2060: 'word joiner', 0xfeff: 'byte-order mark',
  0x202a: 'left-to-right embedding', 0x202b: 'right-to-left embedding', 0x202c: 'directional formatting end',
  0x202d: 'left-to-right override', 0x202e: 'right-to-left override', 0x00ad: 'soft hyphen',
};
const rnCodePoint = (cp) => 'U+' + cp.toString(16).toUpperCase().padStart(4, '0');

export const REFERENCE_NUMBER_RULES = {
  maxLength: RN_MAX,
  /** Trim; blank → null. The service's NormalizeReferenceNumber. */
  normalize(v) {
    if (v == null) return null;
    const t = String(v).trim();
    return t === '' ? null : t;
  },
  /** Remove every denied character, keeping everything else verbatim. */
  stripHidden(v) { return (v || '').replace(RN_HIDDEN_ALL, ''); },
  /** First denied character, or null. */
  findHidden(v) {
    const m = (v || '').match(RN_HIDDEN);
    if (!m) return null;
    const cp = m[0].codePointAt(0);
    const count = ((v || '').match(RN_HIDDEN_ALL) || []).length;
    return { codePoint: rnCodePoint(cp), name: RN_NAMES[cp] || 'control character', count };
  },
  /** { code, message } for the first rule the (untrimmed) value breaks, else null. */
  validate(v) {
    const s = v || '';
    const hidden = REFERENCE_NUMBER_RULES.findHidden(s);
    if (hidden) {
      return {
        code: 'reference_number_invalid_characters',
        message: `Contains a hidden ${hidden.name} (${hidden.codePoint})${hidden.count > 1 ? ` and ${hidden.count - 1} more` : ''}. Control and formatting characters can’t be stored.`,
        hidden,
      };
    }
    const trimmed = s.trim();
    if (trimmed.length > RN_MAX) {
      return { code: 'reference_number_too_long', message: `Must be ${RN_MAX} characters or fewer — this one is ${trimmed.length}.` };
    }
    return null;
  },
};

export function ReferenceNumberField({
  label = 'Reference number',
  value = '',
  onChange,
  onBlur,
  placeholder = 'e.g. AGR-2026/114-B.2',
  help = 'The number printed on the paperwork — enter it exactly as the other side quotes it.',
  error,
  disabled = false,
  autoFocus = false,
  className = '',
  id,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const v = value || '';
  const live = REFERENCE_NUMBER_RULES.validate(v);
  const len = v.trim().length;
  const over = len > RN_MAX;
  const shownError = error || (live ? live.message : undefined);
  const counter = (
    <span className={`odc-field-count${over ? ' over' : ''}`} aria-hidden="true">{len}/{RN_MAX}</span>
  );
  const handleBlur = (e) => {
    const t = v.trim();
    if (t !== v && onChange) onChange(t, e);
    if (onBlur) onBlur(e);
  };
  const control = (
    <div className="odc-refnum-field">
      <div className="odc-input-wrap">
        <span className="material-icons odc-input-icon" aria-hidden="true">tag</span>
        <input
          id={fieldId}
          className="odc-input has-icon odc-refnum-input"
          type="text"
          value={v}
          placeholder={placeholder}
          disabled={disabled}
          autoFocus={autoFocus}
          spellCheck={false}
          autoComplete="off"
          aria-invalid={shownError ? true : undefined}
          aria-describedby={`${fieldId}-help`}
          onChange={(e) => onChange && onChange(e.target.value, e)}
          onBlur={handleBlur}
        />
      </div>
      {live && live.hidden && !disabled ? (
        <button type="button" className="odc-refnum-strip"
          onClick={() => onChange && onChange(REFERENCE_NUMBER_RULES.stripHidden(v))}>
          <span className="material-icons" aria-hidden="true">cleaning_services</span>
          Remove {live.hidden.count > 1 ? `all ${live.hidden.count}` : 'it'}
        </button>
      ) : null}
    </div>
  );
  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} help={help} error={shownError} aside={counter}
        className={`odc-refnum-shell${className ? ' ' + className : ''}`}>
        {control}
      </FieldShell>
    );
  }
  return (
    <div className={`odc-field${shownError ? ' error' : ''}${className ? ' ' + className : ''}`}>
      <div className="odc-field-head">
        <label className="odc-field-label" htmlFor={fieldId}>{label}</label>
        {counter}
      </div>
      {control}
      <div className="odc-field-help" id={`${fieldId}-help`} role={shownError ? 'alert' : undefined}>{shownError || help}</div>
    </div>
  );
}
