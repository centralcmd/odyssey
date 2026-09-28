/**
 * Odyssey DS — AllocationDonuts
 * The asset / liability pair: two recessed wells, each a stacked `Donut`
 * (ring above ledger + total). Used on Accounts (accounts only) and the
 * Dashboard (accounts + owned property at its in-force estimate).
 *
 * Plain data in — the caller resolves its records into slices (already in one
 * currency) and states anything left out in the sub-line. Slices sort
 * largest-first here; zero values drop. Either side may be omitted with
 * `showLiabilities={false}` / `showAssets={false}`.
 *
 * Amounts carry the money encoding: assets in --finance-income, liabilities
 * in --finance-expense (ledger rows and total).
 *
 * Styled by .odc-alloc-* + .odc-donut-* in components.css.
 */
export function AllocationDonuts({
  assets = [],
  liabilities = [],
  assetsTitle = 'Asset allocation',
  liabilitiesTitle = 'Liability allocation',
  assetsSub,
  liabilitiesSub,
  assetsTotalLabel = 'Total assets',
  liabilitiesTotalLabel = 'Total owed',
  assetsIcon = 'account_balance_wallet',
  liabilitiesIcon = 'credit_card',
  assetColors,
  liabilityColors,
  format = (v) => v,
  showAssets = true,
  showLiabilities = true,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Donut = NS.Donut;
  if (!Donut) return null;
  const sort = (xs) => xs.filter((x) => x && x.value > 0).map((x) => ({ ...x, value: Math.abs(x.value) })).sort((a, b) => b.value - a.value);
  const a = sort(assets), l = sort(liabilities.map((x) => ({ ...x, value: Math.abs(x.value) })));
  const well = (tone, props) => (
    <div className={`odc-alloc-card ${tone}`}>
      <Donut layout="stack" format={format} {...props} />
    </div>
  );
  return (
    <div className={`odc-alloc-row${className ? ' ' + className : ''}`}>
      {showAssets && well('income', { title: assetsTitle, sub: assetsSub, data: a, totalLabel: assetsTotalLabel, centerIcon: assetsIcon, ...(assetColors ? { colors: assetColors } : {}) })}
      {showLiabilities && well('expense', { title: liabilitiesTitle, sub: liabilitiesSub, data: l, totalLabel: liabilitiesTotalLabel, centerIcon: liabilitiesIcon, ...(liabilityColors ? { colors: liabilityColors } : {}) })}
    </div>
  );
}
