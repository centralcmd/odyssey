/**
 * Odyssey DS — TermHistoryChart
 * ---------------------------------------------------------------------------
 * The assembled "how has this charge changed" card: a `StepChart` whose head
 * IS its controls. A quiet `MultiSelect` on the left says WHAT is plotted and
 * stands in for the title; a Change / Value `SegmentedControl` on the right
 * says HOW it reads. No title, no headline figure — the legend under the plot
 * carries every line's name, value in force and move.
 *
 * Plain data in: the caller resolves its own records into `series` (ordered —
 * the first is the default selection, normally the most recently changed) and
 * this component owns every rule about comparing them:
 *
 * - **Never nothing plotted.** Removing the last line keeps it.
 * - **One axis, one unit.** A series whose `group` differs from what is shown
 *   REPLACES the selection instead of joining an axis true of neither.
 * - **Stable colours.** Each series leases a hue, preferring its own `color`
 *   (a direction hue — money out is money out) and falling back to `palette`
 *   when that colour is already on screen. Leases outlive deselection, so a
 *   line never repaints when another joins, leaves or returns.
 * - **A cap.** `palette.length` lines at most — past it no hue is both neutral
 *   and distinct.
 *
 * Atoms are read off the DS namespace at render time.
 */
const THC_PALETTE = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-4)', 'var(--chart-6)'];

export function TermHistoryChart({
  series = [],
  pickerLabel = 'Terms',
  glyph = '§',
  palette = THC_PALETTE,
  searchLabel = 'Search terms',
  emptyText = 'No matching term',
  textEquivalentLabel = 'Effective from',
  curve = 'step',
  className,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { StepChart, MultiSelect, SegmentedControl } = NS;
  const [sel, setSel] = React.useState(null);
  // Null until the reader chooses: StepChart's `auto` then indexes a
  // comparison and shows real figures for one line. A choice sticks.
  const [scale, setScale] = React.useState(null);
  const leases = React.useRef({});

  const list = series.filter((s) => s && s.points && s.points.length);
  if (!list.length || !StepChart || !SegmentedControl) return null;

  const valid = (keys) => (keys || []).filter((k) => list.some((x) => x.key === k));
  const chosen = valid(sel);
  const active = chosen.length ? chosen : [list[0].key];
  const picked = active.map((k) => list.find((x) => x.key === k)).filter(Boolean).slice(0, palette.length);
  const multi = picked.length > 1;

  // Renew held leases in selection order, then give newcomers their own hue
  // if free, else the first free palette colour.
  const held = leases.current;
  const taken = new Set();
  picked.forEach((x) => {
    const h = held[x.key];
    if (!h) return;
    if (taken.has(h)) held[x.key] = null; else taken.add(h);
  });
  picked.forEach((x) => {
    if (held[x.key]) return;
    const own = x.color || palette[0];
    const hue = !taken.has(own) ? own : (palette.find((h) => !taken.has(h)) || palette[0]);
    held[x.key] = hue; taken.add(hue);
  });
  const colorOf = (x) => held[x.key] || x.color || palette[0];

  const head = picked[0];
  const lines = picked.map((x) => ({ id: x.key, label: x.label, color: colorOf(x), points: x.points }));

  const onPick = (next) => {
    setSel((prevRaw) => {
      const prev = valid(prevRaw);
      const cur = prev.length ? prev : [list[0].key];
      const added = next.filter((k) => !cur.includes(k));
      if (!added.length) return next.length ? next : cur.slice(0, 1);
      const x = list.find((y) => y.key === added[added.length - 1]);
      const first = list.find((y) => y.key === cur[0]);
      if ((x.group || '') !== (first.group || '')) return [x.key];
      if (cur.length >= palette.length) return cur;
      return [...cur, x.key];
    });
  };

  // The dash means "this is the line", so only a selected row carries one.
  const options = list.map((x) => ({
    value: x.key,
    text: [x.label, x.value, x.tone && x.tone.label].filter(Boolean).join(' '),
    label: (
      <React.Fragment>
        {x.label}
        {x.value ? <React.Fragment> · <span className="odc-thc-val" style={x.tone ? { color: x.tone.color } : undefined}>{x.value}</span></React.Fragment> : null}
        {x.tone ? <span className="odc-thc-dir"> · {x.tone.label}</span> : null}
      </React.Fragment>
    ),
    icon: 'remove',
    iconColor: active.includes(x.key) ? colorOf(x) : undefined,
  }));

  const picker = list.length > 1 && MultiSelect ? (
    <MultiSelect label={pickerLabel} glyph={glyph} align="start" quiet
      value={active} onChange={onPick} options={options}
      searchLabel={searchLabel} emptyText={emptyText} />
  ) : null;

  const axisToggle = (
    <SegmentedControl ariaLabel="Y-axis" value={scale || (multi ? 'indexed' : 'absolute')} onChange={setScale}
      options={[{ value: 'indexed', label: 'Change' }, { value: 'absolute', label: 'Value' }]} />
  );

  return (
    <div className={`odc-thc${className ? ` ${className}` : ''}`}>
      <StepChart
        controls={picker}
        controlsEnd={axisToggle}
        lines={lines}
        showFigure={false}
        scale={scale || 'auto'}
        curve={curve}
        color={lines[0].color}
        format={head.format}
        axisFormat={head.axisFormat}
        ariaLabel={`${picked.map((x) => x.label).join(', ')} — value over time`}
        textEquivalent
        textEquivalentLabel={textEquivalentLabel}
      />
    </div>
  );
}
