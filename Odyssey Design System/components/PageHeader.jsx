/**
 * Odyssey DS — PageHeader
 * The shared page-header scaffold every Odyssey screen mounts first.
 *
 * Anatomy: title + sub-line (+ optional chips row) on the left, the action
 * cluster on the right. The cluster is composed, in order, of:
 *   1. an optional severity `signal` toggle (problem rollup — FRONT of cluster)
 *   2. an optional "Overview" toggle   (present iff an `overview` region is passed)
 *   3. an optional "Search" toggle     (present iff a `search` region is passed)
 *   4. an optional reference toggle    (present iff an `info` region is passed)
 *   5. any extra secondary `actions`
 *   6. the `primary` action — the filled create verb
 *   7. an optional overflow "More" menu — the RIGHTMOST control, always last.
 *
 * A toggle button's filled / outlined state IS its on/off indicator (no
 * chevron). When any region is open the header is wrapped in a surface card
 * and the region(s) drop in below the title row, each separated by a divider
 * (.ph-region). Plain pages (no regions) render the bare .page-head — pass
 * `card` to force the canonical carded variant.
 *
 * Styled by the kit sheet (.page-head / .ph-* / .btn.signal) which travels
 * with the design system's styles.css closure. Bundle components can't import
 * each other, so the Button / Chip / ActionMenu / SeverityIcon atoms are read
 * off the DS namespace at render time.
 */

export function PageHeader({
  title, sub, chips, icon,
  overview, search, signal,
  info, infoLabel, infoIcon,
  overviewDefaultOpen, searchDefaultOpen, infoDefaultOpen,
  menu, actions, primary,
  leadActions,
  card,
}) {
  const { useState } = React;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Button, Chip, ActionMenu, SeverityIcon } = NS;
  if (!Button) return null;

  const hasRegions = !!(overview || search || info || (signal && signal.region));
  const [showOverview, setShowOverview] = useState(!!overviewDefaultOpen);
  const [showSearch, setShowSearch] = useState(!!searchDefaultOpen);
  const [showInfo, setShowInfo] = useState(!!infoDefaultOpen);
  const [showSignal, setShowSignal] = useState(!!(signal && signal.defaultOpen));

  const hasChips = React.isValidElement(chips) || (Array.isArray(chips) && chips.length > 0);
  const renderChips = () => {
    if (!hasChips) return null;
    if (React.isValidElement(chips)) return <div className="ph-chips">{chips}</div>;
    return (
      <div className="ph-chips">
        {chips.map((c, i) => React.isValidElement(c) ? c : (
          <Chip key={i} tone={c.tone} icon={c.icon} dot={c.dot}>{c.label}</Chip>
        ))}
      </div>
    );
  };

  const renderActions = () => {
    if (!Array.isArray(actions)) return actions || null;
    // `color` was the kit's legacy MudBlazor prop — the DS Button folds the
    // primary CTA into variant="filled", so it's dropped here.
    return actions.map(({ color, ...a }, i) => (
      <Button key={i} variant={a.variant || 'outlined'}
        icon={a.icon} iconRight={a.iconRight} onClick={a.onClick}>{a.label}</Button>
    ));
  };

  const hasMenu = React.isValidElement(menu) || (Array.isArray(menu) && menu.length > 0);
  const renderMenu = () => {
    if (!hasMenu) return null;
    if (React.isValidElement(menu)) return menu;
    return <ActionMenu items={menu} />;
  };

  const renderPrimary = () => {
    if (!primary) return null;
    if (React.isValidElement(primary)) return primary;
    return (
      <Button variant="filled" icon={primary.icon} onClick={primary.onClick}>
        {primary.label}
      </Button>
    );
  };

  const hasActions = hasMenu || overview || search || info || (signal && signal.region) || primary || leadActions ||
    (Array.isArray(actions) ? actions.length : !!actions);

  const sev = signal && signal.severity ? signal.severity : 'warning';
  const cluster = hasActions ? (
    <div className="row gap-2">
      {leadActions || null}
      {signal && signal.region && (
        <button type="button"
          className={`btn signal ${sev} ${showSignal ? 'active' : ''}`}
          onClick={() => setShowSignal(s => !s)}
          aria-pressed={showSignal}
          aria-label={`${signal.count} ${signal.count === 1 ? 'problem needs' : 'problems need'} attention`}>
          <SeverityIcon severity={sev} size={18} />
          <span>{signal.label || 'Attention'}</span>
          {signal.count != null && <span className="signal-count"><span>{signal.count}</span></span>}
        </button>
      )}
      {overview && (
        <Button variant={showOverview ? 'filled' : 'outlined'} icon="donut_large"
          onClick={() => setShowOverview(s => !s)}>Overview</Button>
      )}
      {search && (
        <Button variant={showSearch ? 'filled' : 'outlined'} icon="search"
          onClick={() => setShowSearch(s => !s)}>Search</Button>
      )}
      {info && (
        <Button variant={showInfo ? 'filled' : 'outlined'} icon={infoIcon || 'menu_book'}
          onClick={() => setShowInfo(s => !s)}>{infoLabel || 'Reference'}</Button>
      )}
      {renderActions()}
      {renderPrimary()}
      {renderMenu()}
    </div>
  ) : null;

  const anyOpen = (overview && showOverview) || (search && showSearch) || (info && showInfo) || (signal && signal.region && showSignal);

  const lead = icon
    ? (React.isValidElement(icon)
        ? icon
        : <div className="ph-lead"><span className="material-icons" aria-hidden="true">{icon}</span></div>)
    : null;

  const headRow = (
    <div className="page-head" style={(hasRegions || card) ? { marginBottom: anyOpen ? 16 : 0 } : undefined}>
      <div className="ph-titles">
        {lead}
        <div className="ph-titletext">
          <h1>{title}</h1>
          {sub && <div className="sub">{sub}</div>}
          {renderChips()}
        </div>
      </div>
      {cluster}
    </div>
  );

  if (!hasRegions) {
    if (!card) return headRow;
    // Canonical design-system page header: the bare row wrapped in a surface card.
    return (
      <div className="card">
        <div className="card-body" style={{ padding: 16 }}>{headRow}</div>
      </div>
    );
  }

  return (
    <div className="card">
      <div className="card-body" style={{ padding: 16 }}>
        {headRow}
        {signal && signal.region && showSignal && <div className="ph-region">{signal.region}</div>}
        {overview && showOverview && <div className="ph-region">{overview}</div>}
        {search && showSearch && <div className="ph-region">{search}</div>}
        {info && showInfo && <div className="ph-region">{info}</div>}
      </div>
    </div>
  );
}
