/**
 * Odyssey DS — PasswordRules
 * The single, shared password-requirement checklist. This is the one place the
 * five password rules and the minimum length are declared; every surface that
 * shows password requirements (Register, Change password on /account, and the
 * new /reset-password page) renders THIS component, so the displayed rules can
 * never drift from one another.
 *
 * `PASSWORD_POLICY` mirrors the server's IdentityOptions.Password gate
 * (16 chars + all four character classes). It is the authoritative client-side
 * source: hosts drive their submit button's disabled state from
 * `PASSWORD_POLICY.isSatisfied(password)`, the same data that renders the ticks.
 * The server remains the authoritative gate — this is a UX aid.
 *
 * Rule met / unmet is conveyed by icon + text (never colour alone), and each
 * item carries an off-screen "met / not yet met" phrase for assistive tech.
 * Styled by .odc-pw-rules in components.css.
 */
export const PASSWORD_POLICY = {
  minLength: 16,
  rules(candidate) {
    const s = candidate || '';
    const n = PASSWORD_POLICY.minLength;
    return [
      { key: 'len',   label: `At least ${n} characters`, met: s.length >= n },
      { key: 'upper', label: 'An uppercase letter',      met: /[A-Z]/.test(s) },
      { key: 'lower', label: 'A lowercase letter',       met: /[a-z]/.test(s) },
      { key: 'digit', label: 'A number',                 met: /\d/.test(s) },
      { key: 'sym',   label: 'A symbol (!@#$…)',          met: /[^A-Za-z0-9]/.test(s) },
    ];
  },
  isSatisfied(candidate) {
    return PASSWORD_POLICY.rules(candidate).every((r) => r.met);
  },
};

export function PasswordRules({
  password = '',
  columns = 1,
  className = '',
  'aria-label': ariaLabel = 'Password requirements',
}) {
  const rules = PASSWORD_POLICY.rules(password);
  return (
    <ul
      className={`odc-pw-rules${columns === 2 ? ' cols-2' : ''}${className ? ' ' + className : ''}`}
      aria-label={ariaLabel}
    >
      {rules.map((r) => (
        <li key={r.key} className={`odc-pw-rule${r.met ? ' met' : ''}`}>
          <span className="material-icons" aria-hidden="true">
            {r.met ? 'check_circle' : 'radio_button_unchecked'}
          </span>
          <span>{r.label}</span>
          <span className="sr-only">{r.met ? ' — met' : ' — not yet met'}</span>
        </li>
      ))}
    </ul>
  );
}
