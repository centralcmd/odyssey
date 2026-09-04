/* Preferences — the live settings page.
   Recreates centralcmd/odyssey · Preferences.razor in the Odyssey design language.

   Each setting is a single "preference card": an outlined card holding one row —
   a tide-tinted icon + label + one-line description on the left, and exactly one
   control on the right. The three cards map 1:1 to the Blazor page:

     Dark mode       → Switch              (DarkModeEnabled)
     Default currency→ Select (ISO code)   (DefaultCurrency — new transactions)
     Main currency   → Select (ISO code)   (MainCurrency — net-worth roll-up)

   The header carries Search (filters the cards by label/description, exactly like
   the Razor page's Matches()) and the Save primary. When a query hits nothing,
   the body collapses to a single "no matches" surface. */

const CURRENCIES = [
  { value: 'USD', label: 'US Dollar' },
  { value: 'EUR', label: 'Euro' },
  { value: 'GBP', label: 'British Pound' },
  { value: 'JPY', label: 'Japanese Yen' },
  { value: 'CAD', label: 'Canadian Dollar' },
  { value: 'AUD', label: 'Australian Dollar' },
  { value: 'CHF', label: 'Swiss Franc' },
  { value: 'SEK', label: 'Swedish Krona' },
];

// One source of truth for the cards — drives both render and search.
const PREF_DEFS = [
  { key: 'darkMode', icon: 'dark_mode', title: 'Dark mode',
    desc: 'Toggle between dark and light theme', control: 'switch' },
  { key: 'defaultCurrency', icon: 'attach_money', title: 'Default currency',
    desc: 'Currency used by default for new transactions', control: 'currency' },
  { key: 'mainCurrency', icon: 'account_balance', title: 'Main currency',
    desc: 'Currency used to display total net worth, assets and liabilities', control: 'currency' },
];

// One preference card — the shared DS SettingRow (icon + label + description | control).
const PrefCard = ({ icon, title, desc, children }) => (
  <SettingRow icon={icon} title={title} desc={desc}>{children}</SettingRow>
);

function PreferencesPage({ darkMode, onToggleDark }) {
  const { useState } = React;
  const [q, setQ] = useState('');
  // Local copy so the page can preview dark mode and "Save" commits it (mirrors the
  // Razor PreviewDarkMode / SaveUserPreferencesAsync split). darkMode is the app's
  // committed value; we preview through onToggleDark to stay in sync with the shell.
  const [defaultCurrency, setDefaultCurrency] = useState('USD');
  const [mainCurrency, setMainCurrency] = useState('USD');
  const [saved, setSaved] = useState(false);

  const matches = (d) => {
    const t = q.trim().toLowerCase();
    if (!t) return true;
    return d.title.toLowerCase().includes(t) || d.desc.toLowerCase().includes(t);
  };
  const visible = PREF_DEFS.filter(matches);

  const controlFor = (d) => {
    if (d.control === 'switch') {
      return <Switch checked={darkMode} onChange={onToggleDark} />;
    }
    const val = d.key === 'defaultCurrency' ? defaultCurrency : mainCurrency;
    const set = d.key === 'defaultCurrency' ? setDefaultCurrency : setMainCurrency;
    return (
      <div className="pref-select">
        <CurrencySelect label={null} value={val} onChange={set} options={CURRENCIES} searchThreshold={0} />
      </div>
    );
  };

  const save = () => { setSaved(true); setTimeout(() => setSaved(false), 1800); };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Preferences"
        icon="tune"
        sub="User preferences for the Odyssey application"
        searchDefaultOpen
        search={(
          // Search is the only control in this region, so the field spends the
          // full width to the right (flex:1 inside a full-bleed row).
          <div className="row gap-3" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search preferences…" value={q} onChange={setQ} />
            </div>
          </div>
        )}
        primary={{ label: saved ? 'Saved' : 'Save changes', icon: saved ? 'check' : 'save', onClick: save }}
      />

      {visible.length > 0 ? (
        <div className="pref-list">
          {visible.map(d => (
            <PrefCard key={d.key} icon={d.icon} title={d.title} desc={d.desc}>
              {controlFor(d)}
            </PrefCard>
          ))}
        </div>
      ) : (
        <EmptyState
          icon="search_off"
          mutedIcon
          title="No preferences match"
          desc={`Nothing here matches "${q.trim()}". Clear the search to see every setting.`}
        />
      )}
    </div>
  );
}

Object.assign(window, { PreferencesPage });
