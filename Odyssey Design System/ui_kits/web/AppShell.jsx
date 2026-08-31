/* App shell — v2: module rail + switcher + command palette.
   ------------------------------------------------------------------
   The old flat drawer grew to ~20 flat destinations. This shell groups
   every page under a MODULE (Dashboard · Finance ·
   Journal · User · System). The left icon rail shows the ACTIVE module's pages only;
   the switcher at the top changes module; ⌘K (or the search icon) opens a
   command palette that jumps to any page across ALL modules.

   The public contract is unchanged, so no page needs to change:
     <AppShell current={key} onNavigate={fn} onLogout={fn}
               darkMode={bool} onToggleDark={fn} isAdmin={bool}>…</AppShell>
   `current` / `onNavigate` still use the exact page keys the router uses
   (home, tax-statements, exchange-rates, analysis-log, user-account, …). */

// Every destination, grouped by module. `key` is the router key (unchanged).
// `admin: true` gates a page behind the users.read claim (isAdmin), matching
// the previous NAV_ADMIN fence.
const MODULES = [
  { key: 'dashboard', label: 'Dashboard', icon: 'space_dashboard',
    groups: [{ items: [
      { key: 'home', label: 'Dashboard', icon: 'space_dashboard' },
    ] }] },
  { key: 'finance', label: 'Finance', icon: 'account_balance_wallet',
    groups: [
      { label: 'Money', items: [
        { key: 'accounts', label: 'Accounts', icon: 'account_balance_wallet' },
        { key: 'transactions', label: 'Transactions', icon: 'receipt_long' },
        { key: 'budgets', label: 'Budgets', icon: 'pie_chart' },
      ] },
      { label: 'Commitments', items: [
        { key: 'tax-statements', label: 'Tax Statements', icon: 'request_quote' },
        { key: 'insurance', label: 'Insurance', icon: 'shield' },
        { key: 'contracts', label: 'Contracts', icon: 'handshake' },
        { key: 'subscriptions', label: 'Subscriptions', icon: 'subscriptions' },
      ] },
      { label: 'Documents', items: [
        { key: 'files', label: 'Files', icon: 'folder' },
      ] },
      { label: 'Reference', noDivider: true, items: [
        { key: 'tags', label: 'Transaction Tags', icon: 'local_offer' },
        { key: 'currencies', label: 'Currencies', icon: 'attach_money' },
        { key: 'exchange-rates', label: 'Exchange rates', icon: 'currency_exchange' },
      ] },
    ] },
  { key: 'journal', label: 'Journal', icon: 'menu_book',
    groups: [{ items: [
      { key: 'journal', label: 'Journal', icon: 'book' },
      { key: 'calendar', label: 'Calendar', icon: 'calendar_month' },
      { key: 'photos', label: 'Photos', icon: 'photo_library' },
      { key: 'albums', label: 'Albums', icon: 'photo_album' },
      { key: 'tasks', label: 'Tasks', icon: 'checklist' },
      { key: 'contacts', label: 'Contacts', icon: 'groups' },
      { key: 'journal-tags', label: 'Journal Tags', icon: 'local_offer' },
      { key: 'task-tags', label: 'Task Tags', icon: 'local_offer' },
      { key: 'photo-tags', label: 'Photo Tags', icon: 'local_offer' },
    ] }] },
  { key: 'user', label: 'User', icon: 'person',
    groups: [{ items: [
      { key: 'user-account', label: 'Account', icon: 'account_circle' },
      { key: 'preferences', label: 'Preferences', icon: 'tune' },
    ] }] },
  { key: 'system', label: 'System', icon: 'settings',
    groups: [{ items: [
      { key: 'users', label: 'Users', icon: 'manage_accounts', admin: true },
      { key: 'roles', label: 'Roles', icon: 'badge', admin: true },
      { key: 'analysis-log', label: 'Analysis log', icon: 'policy', admin: true },
      { key: 'legal-documents', label: 'Terms of Service', icon: 'gavel', admin: true },
      { key: 'settings', label: 'Settings', icon: 'settings', admin: true },
      { key: 'about', label: 'About', icon: 'github' },
    ] }] },
];

const MOD = k => MODULES.find(m => m.key === k);
// Visible groups/items for the current permission level.
const visGroups = (m, isAdmin) =>
  m.groups
    .map(g => ({ ...g, items: g.items.filter(it => isAdmin || !it.admin) }))
    .filter(g => g.items.length);
const visItems = (m, isAdmin) => visGroups(m, isAdmin).flatMap(g => g.items);
const allVisPages = isAdmin =>
  MODULES.flatMap(m => visItems(m, isAdmin).map(it => ({ ...it, modKey: m.key, mod: m.label })));
// Which module owns a page key (falls back to the first module).
const moduleOf = key => MODULES.find(m => m.groups.some(g => g.items.some(it => it.key === key))) || MODULES[0];
// Modules with at least one page the current user can see. Per the Journal spec
// (FE #1), a module whose every page is claim-gated away (zero visible pages)
// is DROPPED from the rail/switcher entirely — no dead "0 pages" row. In this
// kit `isAdmin` is the only gate, so fully-gated modules disappear for a
// non-admin the same way a Guest would lose the whole Journal module in prod.
const visModules = isAdmin => MODULES.filter(m => visItems(m, isAdmin).length);

// GitHub brand mark — Material Icons (Filled) has no GitHub glyph, so inline it.
const GithubIcon = () => (
  <svg className="nav-icon-svg" viewBox="0 0 24 24" width="20" height="20" fill="currentColor" aria-hidden="true">
    <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.09 3.29 9.4 7.86 10.93.57.1.78-.25.78-.55v-2.05c-3.19.7-3.86-1.36-3.86-1.36-.52-1.32-1.27-1.67-1.27-1.67-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.18 1.76 1.18 1.02 1.76 2.69 1.25 3.35.96.1-.74.4-1.25.72-1.54-2.55-.29-5.23-1.27-5.23-5.66 0-1.25.45-2.27 1.18-3.07-.12-.29-.51-1.46.11-3.04 0 0 .96-.31 3.15 1.17a10.94 10.94 0 0 1 5.74 0c2.19-1.48 3.15-1.17 3.15-1.17.62 1.58.23 2.75.11 3.04.74.8 1.18 1.82 1.18 3.07 0 4.4-2.69 5.37-5.25 5.65.41.35.78 1.05.78 2.13v3.16c0 .31.21.66.79.55C20.21 21.4 23.5 17.09 23.5 12 23.5 5.65 18.35.5 12 .5z"/>
  </svg>
);
const PageIcon = ({ icon }) => icon === 'github' ? <GithubIcon /> : <MIcon name={icon} />;

// ── Command palette ─────────────────────────────────────────────────
// Search-to-jump across every module. This is the power-user path; the rail
// covers everyday discovery. Keyboard: ↑/↓ move, ↵ open, esc close.
const CommandPalette = ({ isAdmin, currentModKey, onClose, onNavigate }) => {
  const { useState, useRef, useEffect } = React;
  const [q, setQ] = useState('');
  const [sel, setSel] = useState(0);
  const inputRef = useRef(null);
  useEffect(() => { inputRef.current && inputRef.current.focus(); }, []);

  const ql = q.trim().toLowerCase();
  const pages = allVisPages(isAdmin);
  const pageHits = ql ? pages.filter(p => p.label.toLowerCase().includes(ql)) : pages.slice(0, 6);
  const modHits = ql ? MODULES.filter(m => m.label.toLowerCase().includes(ql) && m.key !== currentModKey) : [];
  const flat = [
    ...pageHits.map(p => ({ type: 'page', p })),
    ...modHits.map(m => ({ type: 'mod', m })),
  ];
  useEffect(() => { setSel(0); }, [q]);

  const run = item => {
    if (!item) return;
    if (item.type === 'page') onNavigate(item.p.key);
    else { const first = visItems(item.m, isAdmin)[0]; if (first) onNavigate(first.key); }
    onClose();
  };
  const onKey = e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setSel(s => Math.min(s + 1, flat.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setSel(s => Math.max(s - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); run(flat[sel]); }
    else if (e.key === 'Escape') { e.preventDefault(); onClose(); }
  };

  let idx = -1;
  return (
    <div className="odn-scrim" onMouseDown={onClose} role="dialog" aria-modal="true" aria-label="Command palette">
      <div className="odn-cmd" onMouseDown={e => e.stopPropagation()}>
        <div className="odn-cmd-input">
          <MIcon name="search" />
          <input ref={inputRef} value={q} onChange={e => setQ(e.target.value)} onKeyDown={onKey}
                 placeholder="Jump to any page or module…" aria-label="Search pages and modules" />
          <span className="odn-cmd-esc">esc</span>
        </div>
        <div className="odn-cmd-list">
          {flat.length === 0 && <div className="odn-cmd-empty">No matches for “{q}”.</div>}
          {pageHits.length > 0 && <div className="odn-cmd-sec">{ql ? 'Go to · any module' : 'Jump to'}</div>}
          {pageHits.map(p => {
            idx++; const i = idx; const isSel = i === sel;
            return (
              <button key={'p' + p.key} type="button" className={`odn-cmd-row${isSel ? ' sel' : ''}`}
                      onMouseEnter={() => setSel(i)} onClick={() => run({ type: 'page', p })}>
                <PageIcon icon={p.icon} />{p.label}
                <span className="odn-cmd-grp">{p.mod}</span>
                {isSel && <span className="odn-cmd-kbd">↵</span>}
              </button>
            );
          })}
          {modHits.length > 0 && <div className="odn-cmd-sec">Switch module</div>}
          {modHits.map(m => {
            idx++; const i = idx; const isSel = i === sel;
            return (
              <button key={'m' + m.key} type="button" className={`odn-cmd-row${isSel ? ' sel' : ''}`}
                      onMouseEnter={() => setSel(i)} onClick={() => run({ type: 'mod', m })}>
                <MIcon name={m.icon} />{m.label}
                <span className="odn-cmd-grp">Module</span>
                {isSel && <span className="odn-cmd-kbd">↵</span>}
              </button>
            );
          })}
        </div>
        <div className="odn-cmd-foot">
          <span><span className="odn-kbd">↑</span><span className="odn-kbd">↓</span>navigate</span>
          <span><span className="odn-kbd">↵</span>open</span>
          <span><span className="odn-kbd">esc</span>close</span>
        </div>
      </div>
    </div>
  );
};

// ── Icon rail ───────────────────────────────────────────────────────
const Rail = ({ current, onNavigate, isAdmin, onOpenSwitcher, onOpenPalette, switcherOpen, darkMode, onToggleDark }) => {
  const mod = moduleOf(current);
  const groups = visGroups(mod, isAdmin);
  return (
    <aside className="odn-rail">
      <div className="odn-brand"><BrandMark size={30} /></div>

      <button type="button" className="odn-railbtn odn-tip" data-tip="Search  ⌘K"
              aria-label="Search (Command-K)" onClick={onOpenPalette}>
        <MIcon name="search" />
      </button>

      <button type="button" className="odn-modbtn odn-tip" data-tip="Switch module"
              aria-label={`Module: ${mod.label}. Switch module`} aria-haspopup="menu"
              aria-expanded={switcherOpen} onClick={onOpenSwitcher}>
        <MIcon name={mod.icon} />
        <span className="odn-modchev"><MIcon name="unfold_more" /></span>
      </button>
      <div className="odn-modlabel">{mod.label}</div>
      <div className="odn-divider" />

      <nav aria-label={`${mod.label} pages`} style={{ display: 'contents' }}>
        {groups.map((g, gi) => (
          <React.Fragment key={g.label || gi}>
            {gi > 0 && !g.noDivider && <div className="odn-subdiv" />}
            {g.items.map(it => (
              <button key={it.key} type="button"
                      className={`odn-railbtn odn-tip${it.key === current ? ' active' : ''}`}
                      data-tip={it.label} aria-label={it.label}
                      aria-current={it.key === current ? 'page' : undefined}
                      onClick={() => onNavigate(it.key)}>
                <PageIcon icon={it.icon} />
              </button>
            ))}
          </React.Fragment>
        ))}
      </nav>

      {/* Theme toggle — kit-level affordance, pinned to the bottom. */}
      <div className="odn-foot">
        <button className="icon-btn" aria-label={darkMode ? 'Switch to light mode' : 'Switch to dark mode'}
                onClick={onToggleDark}>
          <MIcon name={darkMode ? 'light_mode' : 'dark_mode'} />
        </button>
      </div>
    </aside>
  );
};

// ── Module switcher popover ─────────────────────────────────────────
const ModuleSwitcher = ({ current, isAdmin, onGoModule, onClose }) => {
  const activeKey = moduleOf(current).key;
  return (
    <React.Fragment>
      <div className="odn-catch" onClick={onClose} />
      <div className="odn-modpop" role="menu" aria-label="Switch module" onMouseDown={e => e.stopPropagation()}>
        <div className="odn-modpop-lg">Switch module</div>
        {visModules(isAdmin).map(m => {
          const count = visItems(m, isAdmin).length;
          const active = m.key === activeKey;
          return (
            <button key={m.key} type="button" role="menuitem" className={`odn-modrow${active ? ' active' : ''}`}
                    onClick={() => onGoModule(m.key)}>
              <span className="odn-modrow-ic"><MIcon name={m.icon} /></span>
              <span className="odn-modrow-t">{m.label}</span>
              <span className="odn-modrow-c">{active ? <MIcon name="check" /> : count}</span>
            </button>
          );
        })}
        <div className="odn-modsoon">More modules coming as the product grows.</div>
      </div>
    </React.Fragment>
  );
};

const AppShell = ({ current, onNavigate, onLogout, darkMode, onToggleDark, isAdmin = true, children }) => {
  const { useState, useEffect } = React;
  const [switcher, setSwitcher] = useState(false);
  const [palette, setPalette] = useState(false);

  // ⌘K / Ctrl-K toggles the palette from anywhere.
  useEffect(() => {
    const h = e => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault(); setPalette(p => !p); setSwitcher(false);
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, []);

  // Switching module lands on that module's first (visible) page.
  const goModule = mk => {
    const first = visItems(MOD(mk), isAdmin)[0];
    if (first) onNavigate(first.key);
    setSwitcher(false);
  };

  return (
    <div className="odn-app">
      {/* First focusable — bypass the rail, jump to content (WCAG 2.4.1). */}
      <a className="ods-skip-link" href="#main-content">Skip to main content</a>

      <Rail current={current} onNavigate={onNavigate} isAdmin={isAdmin}
            switcherOpen={switcher}
            onOpenSwitcher={() => { setSwitcher(s => !s); setPalette(false); }}
            onOpenPalette={() => { setPalette(true); setSwitcher(false); }}
            darkMode={darkMode} onToggleDark={onToggleDark} />

      {switcher && (
        <ModuleSwitcher current={current} isAdmin={isAdmin}
                        onGoModule={goModule} onClose={() => setSwitcher(false)} />
      )}

      <main className="main" id="main-content" tabIndex={-1}>
        <div className="container-lg">
          {children}
        </div>
      </main>

      {palette && (
        <CommandPalette isAdmin={isAdmin} currentModKey={moduleOf(current).key}
                        onClose={() => setPalette(false)} onNavigate={onNavigate} />
      )}
    </div>
  );
};

Object.assign(window, { AppShell, Rail, ModuleSwitcher, CommandPalette });
