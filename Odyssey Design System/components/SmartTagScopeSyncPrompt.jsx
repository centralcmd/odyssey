/**
 * SmartTagScopeSyncPrompt — the "also update the scope?" band in the Edit
 * contract dialog. Renders only when the term dates CHANGED in this edit and
 * the scope differs from what the new term would give. Unchecked by default:
 * the scope is independent, and the motivating case — setting an end date on
 * an ongoing contract — must leave it open.
 */
export function SmartTagScopeSyncPrompt({ before = {}, after = {}, scope = {}, checked = false, onChange, className = '' }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Checkbox = NS.Checkbox;
  const R = NS.SMART_TAG_SCOPE_RULES;
  if (!R) return null;
  const termChanged = !R.equals(R.fromTerm(before), R.fromTerm(after)) || !!before.completionDate !== !!after.completionDate;
  const proposed = R.fromTerm(after);
  if (!termChanged || R.equals(scope, proposed)) return null;
  const label = `Also set the smart-tag scope to ${R.formatRange(proposed)}`;
  return (
    <div className={`odc-stsync${className ? ' ' + className : ''}`}>
      <span className="material-icons odc-stsync-ic" aria-hidden="true">sell</span>
      <div className="odc-stsync-main">
        <div className="odc-stsync-title">The smart-tag scope does not follow the term</div>
        <div className="odc-stsync-desc">
          It stays <b>{R.formatRange(scope)}</b> unless you change it here or from the Smart tags section.
        </div>
        {Checkbox ? <Checkbox checked={checked} onChange={(v) => onChange && onChange(v)} label={label} />
          : <label><input type="checkbox" checked={checked} onChange={(e) => onChange && onChange(e.target.checked)} /> {label}</label>}
      </div>
    </div>
  );
}
