/**
 * Odyssey DS — AddRow
 * The closing affordance for a record list: a full-width dashed "add" row that
 * sits after the last item (Accounts, Budgets, …). Pass a `title` (the verb)
 * and an optional one-line `sub`; `icon` defaults to `add`. Styled by the kit
 * sheet's .acct-add classes (in the styles.css closure). Reads the Avatar atom
 * off the DS namespace at render time.
 */
export function AddRow({ title, sub, icon = 'add', onClick, className = '' }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Avatar } = NS;
  if (!Avatar) return null;
  return (
    <button type="button" className={`acct-add ${className}`.trim()} onClick={onClick}>
      <Avatar icon={icon} size="lg" square tone={{ bg: 'rgba(255,255,255,0.05)', fg: 'var(--ink-200)' }} />
      <div className="acct-add-text">
        <div className="acct-add-title">{title}</div>
        {sub && <div className="acct-add-sub">{sub}</div>}
      </div>
    </button>
  );
}
