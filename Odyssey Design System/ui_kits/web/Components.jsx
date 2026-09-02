/* Odyssey UI kit — atoms.
   SINGLE SOURCE OF TRUTH: the shared atoms (Button, IconButton, Card, Field,
   Select, Chip, Alert, Avatar, Switch, StatTile, EmptyState, MIcon) are the
   typed design-system components, loaded from `_ds_bundle.js` and read off
   window.OdysseyDesignSystem_d5aa51. The thin wrappers below only adapt the
   kit's historical prop names to the DS contract (e.g. `helper`→`help`,
   IconButton `label`→`ariaLabel`, Button `color` dropped → `variant`) so the
   25 page files keep working unchanged. Edit the components in /components to
   change behavior — there is no second implementation here.

   Kit-only atoms with no DS equivalent (CardBody/CardHeader,
   SeverityIcon, BrandMark, AddRow, TONE_MAP, usePopover) are defined locally.
   Everything is exported to window for the other Babel-script page files. */

const { useState, useRef, useEffect } = React;
const DS = window.OdysseyDesignSystem_d5aa51 || {};

// ============================================================
// Bridged atoms — the DS components, with kit prop-name adapters
// ============================================================

// MIcon, Chip, Avatar, Switch, StatTile: prop-compatible — alias straight through.
// (DS Avatar now accepts a {bg,fg} tone object + `square`; DS Chip now has the
//  outline/warning/error tones + className; DS StatTile has valueClass.)
const MIcon    = DS.MIcon;
const Chip     = DS.Chip;
const Avatar   = DS.Avatar;
const Switch   = DS.Switch;
const Checkbox = DS.Checkbox;
const StatTile = DS.StatTile;
const Alert    = DS.Alert;

// Record-table family — the sortable / expandable / editable admin table and
// its atoms. Prop-compatible with the kit's historical local definitions, so
// the page files (Transaction tags, Contacts, Currencies, Exchange
// rates, Users, …) consume them unchanged. These USED to be defined inline in
// Accounts.jsx (ActionMenu, MetaTile) and Users.jsx (SortHeader); they now
// live in /components as typed DS components — there is no second impl here.
const ActionMenu  = DS.ActionMenu;
const SortHeader  = DS.SortHeader;
const MetaTile    = DS.MetaTile;
// InfoTile — labeled fact/stat tile. Aliased from the bundle, with a fallback
// that renders the same shipped .odc-infotile markup (so the kit holds even if
// the compiled bundle lags a turn behind a freshly-added component).
// RecordCard / InfoTileGrid / SectionDivider — the record-card pattern, aliased
// straight from the bundle (no kit fallbacks: the pattern IS the DS component).
const RecordCard = DS.RecordCard;
const InfoTileGrid = DS.InfoTileGrid || (({ dense = false, className = '', style, children }) => (
  <div className={['odc-tilegrid', dense ? 'dense' : '', className].filter(Boolean).join(' ')} style={style}>{children}</div>
));
const SectionDivider = DS.SectionDivider || (({ label, meta, className = '', id }) => (
  <div className={`odc-sectiondivider ${className}`.trim()} id={id}>
    <span className="odc-sectiondivider-l">{label}</span>
    <span className="odc-sectiondivider-rule" aria-hidden="true" />
    {meta != null ? <span className="odc-sectiondivider-meta">{meta}</span> : null}
  </div>
));
const InfoTile = DS.InfoTile || (({ icon, iconColor, iconSoft, label, value, foot, valueVariant = 'mono', wide = false, elevated = true, className = '', style }) => {
  const cls = ['odc-infotile', elevated ? 'elevated' : '', wide ? 'wide' : '', className].filter(Boolean).join(' ');
  return (
    <div className={cls} style={style}>
      <div className="odc-infotile-top">
        {icon ? <span className="odc-infotile-ic" style={iconColor ? { background: iconSoft || undefined, color: iconColor } : undefined}><span className="material-icons" aria-hidden="true">{icon}</span></span> : null}
        {label ? <span className="odc-infotile-k">{label}</span> : null}
      </div>
      <div className={`odc-infotile-v ${valueVariant}`}>{value}</div>
      {foot ? <div className="odc-infotile-foot">{foot}</div> : null}
    </div>
  );
});
const RecordTable = DS.RecordTable;
// Pager + its toolbar mirror — the shared server-pagination controls for the
// flat-table list pages (Transactions, Files, Users, Contacts, Currencies,
// Exchange rates, Transaction tags). The footer Pager is the canonical home of
// the rows-per-page selector; PageSizeSelect mirrors it into the search bar.
const Pager = DS.Pager;
const PageSizeSelect = DS.PageSizeSelect;
// InlinePager — footer pagination for EMBEDDED/inlined tables: a collapsible
// child-collection table inside a detail page (an account's Transactions / Files
// sections, a budget's matched transactions, a document well). These have no
// toolbar, so there is no PageSizeSelect mirror — the footer Pager is the only
// control, and it is always shown when the list has any rows (same as the main
// pages). Owns its own page/pageSize state so each embedded instance paginates
// independently. Usage: <InlinePager items={rows}>{(page) => <TxnTable txns={page} …/>}</InlinePager>
function InlinePager({ items = [], children, pageSize: initial = 25 }) {
  const { useState, useMemo, useEffect } = React;
  const [pageSize, setPageSize] = useState(initial);
  const [page, setPage] = useState(1);
  const total = items.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return items;
    return items.slice((page - 1) * pageSize, page * pageSize);
  }, [items, page, pageSize]);
  useEffect(() => { setPage(1); }, [pageSize]);
  useEffect(() => {
    const tp = pageSize === 'all' ? 1 : Math.max(1, Math.ceil(total / pageSize));
    if (page > tp) setPage(tp);
  }, [total, pageSize, page]);
  return (
    <React.Fragment>
      {children(paged)}
      {total > 0 && (
        <Pager page={page} pageSize={pageSize} totalCount={total}
          onPageChange={setPage}
          onPageSizeChange={(s) => { setPageSize(s); setPage(1); }} />
      )}
    </React.Fragment>
  );
}

// InfiniteList — the card-list counterpart to InlinePager, for the expandable
// card pages (Accounts, Budgets). Renders the first `batchSize` items and
// appends the next batch when an IntersectionObserver sentinel near the end
// scrolls into view (synthetic ~500ms latency stands in for the server fetch in
// this prototype); skeleton cards hold the space while a batch is in flight and
// a centered status pill reports "X of N loaded". No footer pager — the only
// control is the toolbar's "Load N at a time" batch size (a PageSizeSelect the
// page owns). `trailing` (e.g. the AddRow) renders only once everything is
// loaded, since it would be unreachable mid-scroll. `revealKey` force-loads up
// to a specific item (used by the Accounts signal "jump to" so a flagged card
// beyond the window still renders).
function InfiniteListSkeleton() {
  return (
    <React.Fragment>
      {[0, 1].map((i) => (
        <div className="odc-skel-card" aria-hidden="true" key={`sk-${i}`}>
          <span className="odc-skel-ic odc-skel" />
          <span className="odc-skel-lines">
            <span className="odc-skel-bar odc-skel" style={{ width: `${38 - i * 6}%` }} />
            <span className="odc-skel-bar odc-skel" style={{ width: `${22 + i * 4}%`, height: 10 }} />
          </span>
          <span className="odc-skel-bar odc-skel" style={{ width: 90, height: 14 }} />
        </div>
      ))}
    </React.Fragment>
  );
}
function InfiniteList({ items = [], renderItem, itemKey, batchSize = 25, noun = 'items', skeleton, trailing, empty, revealKey }) {
  const { useState, useRef, useEffect } = React;
  const [visible, setVisible] = useState(batchSize);
  const [loading, setLoading] = useState(false);
  const sentinelRef = useRef(null);
  const total = items.length;
  const shown = Math.min(visible, total);
  const hasMore = shown < total;

  // Reset the window to the first batch when the RESULT SET changes (a search /
  // filter that changes the count) or the batch size changes. Keyed on `total`,
  // not the `items` array identity — `items` (a freshly sorted array) changes
  // every render, which would otherwise reset the window mid-scroll and pin the
  // list to the first batch forever.
  useEffect(() => { setVisible(batchSize); setLoading(false); }, [total, batchSize]);

  // Force-reveal a specific item (jump-to): grow the window to include its index.
  useEffect(() => {
    if (revealKey == null || !itemKey) return;
    const idx = items.findIndex((it) => itemKey(it) === revealKey);
    if (idx >= 0) setVisible((v) => Math.max(v, idx + 1));
  }, [revealKey]);

  // Sentinel auto-load.
  useEffect(() => {
    if (!hasMore) return undefined;
    const el = sentinelRef.current;
    if (!el || typeof IntersectionObserver === 'undefined') return undefined;
    const io = new IntersectionObserver((entries) => {
      if (entries[0] && entries[0].isIntersecting) {
        setLoading((busy) => {
          if (busy) return busy;
          setTimeout(() => { setVisible((v) => Math.min(v + batchSize, total)); setLoading(false); }, 500);
          return true;
        });
      }
    }, { rootMargin: '160px' });
    io.observe(el);
    return () => io.disconnect();
  }, [hasMore, batchSize, total]);

  if (total === 0) return empty || null;

  return (
    <React.Fragment>
      {items.slice(0, shown).map((it) => (
        <React.Fragment key={itemKey ? itemKey(it) : undefined}>{renderItem(it)}</React.Fragment>
      ))}
      {loading ? (skeleton || <InfiniteListSkeleton />) : null}
      {hasMore ? <div ref={sentinelRef} aria-hidden="true" style={{ height: 1 }} /> : null}
      {hasMore ? (
        <div className="odc-infinite-status" role="status" aria-live="polite">
          <span className="odc-infinite-pill">
            {loading ? (
              <React.Fragment>
                <span className="odc-infinite-spin" aria-hidden="true" />
                Loading {Math.min(batchSize, total - shown).toLocaleString()} more…
              </React.Fragment>
            ) : (
              <React.Fragment>
                <span className="material-icons" aria-hidden="true" style={{ fontSize: 16 }}>expand_more</span>
                {shown.toLocaleString()} of {total.toLocaleString()} {noun} · scroll for more
              </React.Fragment>
            )}
          </span>
        </div>
      ) : (trailing || null)}
    </React.Fragment>
  );
}
// SortSelect — the filter-bar "Sort by" control. The kit wrapper injects the
// Tweaks-selected anatomy variant (window.__sortVariant, set by the App shell)
// so every page picks it up without prop threading; explicit props still win.
const SortHelpers = DS.SortHelpers;
const SortSelect = (props) => (DS.SortSelect
  ? <DS.SortSelect variant={(typeof window !== 'undefined' && window.__sortVariant) || 'split'} {...props} />
  : null);
const LineChart   = DS.LineChart;
const Delta        = DS.Delta;
const ProblemAlert = DS.ProblemAlert;

// BreakdownTile — labelled icon·label·count distribution tile ("By type" / "By
// status" / "By currency"). Aliased from the bundle with a shipped-markup
// fallback (same pattern as InfoTile) until the compiled bundle carries it.
const BreakdownTile = DS.BreakdownTile || (({ label, rows = [], empty = 'Nothing to show.', className = '', style }) => (
  <div className={`odc-breakdown ${className}`.trim()} style={style}>
    {label ? <span className="odc-breakdown-ov">{label}</span> : null}
    {rows.length ? (
      <div className="odc-breakdown-rows">
        {rows.map((r, i) => (
          <div className="odc-breakdown-row" key={r.key != null ? r.key : i}>
            {r.icon ? <MIcon name={r.icon} size={16} style={r.iconColor ? { color: r.iconColor } : undefined} /> : null}
            <span className="odc-breakdown-label">{r.label}</span>
            <span className="odc-breakdown-n">{r.count}</span>
          </div>
        ))}
      </div>
    ) : <div className="odc-breakdown-empty">{empty}</div>}
  </div>
));

// Modal — the DS dialog shell (scrim + header + scrollable body + footer,
// Esc / scrim-click / focus trap built in). All the kit dialogs
// (AddAccountModal, AddTransactionModal, …) compose it; their old hand-rolled
// .aam-* scaffolds are gone.
const Modal = DS.Modal;

// Button — the kit always passed color="primary" (or ""); the DS folds the
// primary CTA into variant="filled", so `color` is simply dropped.
const Button = ({ color, ...props }) => <DS.Button {...props} />;

// IconButton — the kit names the accessible label `label`; the DS requires
// `ariaLabel`.
const IconButton = ({ label, ariaLabel, ...props }) => (
  <DS.IconButton ariaLabel={ariaLabel || label} {...props} />
);

// Card — the kit's Card never carried its own padding (CardBody/CardHeader
// own it), so always render the DS Card `flush` and let the body pad. `flat`
// (legacy: no shadow, no border) maps to outlined + a flat flag. The ref is
// forwarded only when the bundle's Card actually accepts one (it's a
// forwardRef component) — so this bridge is safe against either bundle build.
const DS_CARD_TAKES_REF = DS.Card && DS.Card.$$typeof === Symbol.for('react.forward_ref');
const Card = React.forwardRef(({ flat, outlined, flush = true, className = '', ...props }, ref) => {
  const refProp = DS_CARD_TAKES_REF ? { ref } : {};
  return (
    <DS.Card
      outlined={outlined || flat}
      flush={flush}
      className={`${flat ? 'flat' : ''} ${className}`.trim()}
      {...refProp}
      {...props}
    />
  );
});

// Field — `helper`→`help`; type="date" routes to the DS DateField (the DS
// Field is text-only). `help` + `helper` are both forwarded so the field's
// helper line shows whether the DS component or the local fallback renders it.
// Everything else (clearable, icon, error, value) passes straight through.
const Field = ({ helper, ...props }) => {
  if (props.type === 'date') {
    const { label, value, onChange, placeholder } = props;
    return <DateField label={label} value={value} onChange={onChange} help={helper} helper={helper} placeholder={placeholder} />;
  }
  return <DS.Field help={helper} {...props} />;
};

// SearchField — the canonical filter/search input: a Field pre-set with a
// leading search glyph + clear button. Delegates to the typed DS component;
// falls back to a search-decorated Field until the bundle carries it (keeps the
// live screens working across a bundle rebuild). `helper`→`help` like Field.
const SearchField = ({ helper, ...props }) => {
  if (DS.SearchField) return <DS.SearchField help={helper} {...props} />;
  return <Field helper={helper} type="search" icon="search" clearable {...props} />;
};

// Select — `helper`→`help`. The DS Select is now the themed popover (matching
// what the kit used to ship locally), so options/value/onChange are unchanged.
const Select = ({ helper, ...props }) => <DS.Select help={helper} {...props} />;

// AmountField — the canonical money / numeric input (currency-or-unit adornment,
// md + lg sizes), replacing the kit's hand-rolled .trm-value / .est-value money
// boxes. Typed DS component; falls back to the shipped .odc-amount markup until
// the bundle carries it, same pattern as SearchField.
const AmountField = ({ helper, ...props }) => {
  if (DS.AmountField) return <DS.AmountField help={helper} {...props} />;
  const { label, value = '', onChange, prefix, suffix, size, align, allowNegative, error, optional, required, disabled, autoFocus, placeholder = '0.00', className = '' } = props;
  const re = allowNegative ? /[^0-9.,\-]/g : /[^0-9.,]/g;
  const msg = error || helper;
  return (
    <div className={`odc-field${error ? ' error' : ''} ${className}`.trim()}>
      {label ? <label className="odc-field-label">{label}{required ? <span className="odc-field-req">*</span> : null}{optional ? <span className="odc-field-opt">Optional</span> : null}</label> : null}
      <div className={`odc-amount${size === 'lg' ? ' lg' : ''}${error ? ' error' : ''}${disabled ? ' disabled' : ''}`}>
        {prefix ? <span className="odc-amount-adorn pre">{prefix}</span> : null}
        <input className="odc-amount-input" inputMode="decimal" type="text" value={value} placeholder={placeholder} disabled={disabled} autoFocus={autoFocus}
          style={align === 'right' ? { textAlign: 'right' } : undefined}
          onChange={(e) => onChange && onChange(e.target.value.replace(re, ''), e)} />
        {suffix ? <span className="odc-amount-adorn suf">{suffix}</span> : null}
      </div>
      {msg ? <div className="odc-field-help">{msg}</div> : null}
    </div>
  );
};

// MoneyField — amount + ISO currency code as one control (the canonical money
// editor). Falls back to AmountField + a separate Select until the bundle
// carries it.
const MoneyField = ({ helper, ...props }) => {
  if (DS.MoneyField) return <DS.MoneyField help={helper} {...props} />;
  const { label, currency, onCurrencyChange, currencyOptions = [], currencyEditable = true, ...amt } = props;
  return (
    <div>
      <AmountField label={label} helper={helper} {...amt} />
      {currencyEditable && onCurrencyChange ? (
        <div style={{ marginTop: 8 }}>
          <Select value={currency} onChange={onCurrencyChange} options={currencyOptions} />
        </div>
      ) : null}
    </div>
  );
};

// CurrencySelect — currency-only picker (same list + search as MoneyField's
// segment). Falls back to a plain Select until the bundle carries it.
const CurrencySelect = ({ helper, ...props }) => {
  if (DS.CurrencySelect) return <DS.CurrencySelect help={helper} {...props} />;
  const { label = 'Currency', options = [], ...rest } = props;
  return <Select label={label} options={options} helper={helper} {...rest} />;
};

// NoteField — the canonical multi-line note / description field with a live
// character counter, replacing the kit's hand-rolled .field + .atm-textarea +
// .*-charcount pattern. Typed DS component with a shipped-markup fallback.
const NoteField = ({ helper, ...props }) => {
  if (DS.NoteField) return <DS.NoteField help={helper} {...props} />;
  const { label, value = '', onChange, placeholder, maxLength, rows = 3, showCount = true, error, optional, required, disabled, autoFocus, className = '' } = props;
  const msg = error || helper;
  const counted = showCount && typeof maxLength === 'number';
  const over = counted && value.length >= maxLength;
  return (
    <div className={`odc-field${error ? ' error' : ''} ${className}`.trim()}>
      {(label || counted) ? (
        <div className="odc-field-head">
          {label ? <label className="odc-field-label">{label}{required ? <span className="odc-field-req">*</span> : null}{optional ? <span className="odc-field-opt">Optional</span> : null}</label> : <span />}
          {counted ? <span className={`odc-field-count${over ? ' over' : ''}`}>{value.length}/{maxLength}</span> : null}
        </div>
      ) : null}
      <textarea className="odc-input odc-input-multiline" rows={rows} value={value} placeholder={placeholder} maxLength={maxLength} disabled={disabled} autoFocus={autoFocus}
        onChange={(e) => onChange && onChange(e.target.value, e)} />
      {msg ? <div className="odc-field-help">{msg}</div> : null}
    </div>
  );
};

// FieldShell — the labelled-field wrapper (label + required/optional marker +
// helper/error line) to wrap controls the kit doesn't field-wrap itself
// (Combobox, MultiSelect, segmented controls, locked displays, dropzones).
// Replaces the hand-rolled .field + .label + .atm-opt + .helper/aam-err markup.
const FieldShell = ({ helper, ...props }) => {
  if (DS.FieldShell) return <DS.FieldShell help={helper} {...props} />;
  const { label, htmlFor, required, optional, error, aside, children, className = '' } = props;
  const msg = error || helper;
  const labelNode = label ? (
    <label className="odc-field-label" htmlFor={htmlFor}>{label}
      {required ? <span className="odc-field-req">*</span> : null}
      {optional ? <span className="odc-field-opt">Optional</span> : null}</label>
  ) : null;
  return (
    <div className={`odc-field${error ? ' error' : ''} ${className}`.trim()}>
      {(label || aside) ? (aside ? <div className="odc-field-head">{labelNode || <span />}{aside}</div> : labelNode) : null}
      {children}
      {msg ? <div className="odc-field-help" id={htmlFor ? `${htmlFor}-help` : undefined}>{msg}</div> : null}
    </div>
  );
};

// NumberField — labelled numeric input (native type="number") emitting number|null.
// Replaces the duplicated ATS_NumField / TS_NumField helpers. Typed DS component
// with a shipped-markup fallback.
const NumberField = ({ helper, ...props }) => {
  if (DS.NumberField) return <DS.NumberField help={helper} {...props} />;
  const { label, value, onChange, placeholder = '—', min, max, step, error, optional, required, disabled, align, className = '' } = props;
  const msg = error || helper;
  return (
    <div className={`odc-field${error ? ' error' : ''} ${className}`.trim()}>
      {label ? <label className="odc-field-label">{label}{required ? <span className="odc-field-req">*</span> : null}{optional ? <span className="odc-field-opt">Optional</span> : null}</label> : null}
      <input className="odc-input" type="number" value={value == null ? '' : value} placeholder={placeholder}
        min={min} max={max} step={step} disabled={disabled} style={align === 'right' ? { textAlign: 'right' } : undefined}
        onChange={(e) => onChange && onChange(e.target.value === '' ? null : parseFloat(e.target.value), e)} />
      {msg ? <div className="odc-field-help">{msg}</div> : null}
    </div>
  );
};

// FormRow — equal-width column grid for paired form fields (replaces the kit's
// former .aam-row2 grid). Typed DS component with a shipped-markup fallback.
const FormRow = (props) => {
  if (DS.FormRow) return <DS.FormRow {...props} />;
  const { cols = 2, gap = 14, align = 'start', className = '', style, children } = props;
  return (
    <div className={`odc-form-row${className ? ' ' + className : ''}`}
      style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gap, alignItems: align, ...style }}>
      {children}
    </div>
  );
};

// ContactTypeSelect — the typed, registry-backed Type picker (icon + color
// per option). `helper`→`help`, same as Select; value is the ContactType key.
// Falls back to a registry-fed DS.Select until the bundle carries the typed
// component (keeps the live screen working across a bundle rebuild).
const ContactTypeSelect = ({ helper, ...props }) => {
  if (DS.ContactTypeSelect) return <DS.ContactTypeSelect help={helper} {...props} />;
  const reg = (window.OdysseyData && window.OdysseyData.contactTypes) || [];
  const options = reg.map((t) => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }));
  return <DS.Select help={helper} options={options} {...props} />;
};

// FileType pickers — typed, registry-backed (icon + color per option). Two
// vocabularies: AccountFileType (files on an account) and TransactionFileType
// (files on a transaction). Same fallback pattern: until the bundle carries the
// typed components, feed the registry into the base DS.Select / DS.MultiSelect.
const optsFrom = (arr) => (arr || []).map((t) => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }));
const acctFileOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.accountFileTypes);
const txnFileOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.transactionFileTypes);
const taxFileOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.taxStatementFileTypes);
const AccountFileTypeSelect = ({ helper, types, ...props }) => {
  if (DS.AccountFileTypeSelect) return <DS.AccountFileTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : acctFileOpts()} {...props} />;
};
const AccountFileTypeMultiSelect = ({ types, ...props }) => {
  if (DS.AccountFileTypeMultiSelect) return <DS.AccountFileTypeMultiSelect types={types} {...props} />;
  return <DS.MultiSelect label="Any type" icon="folder" options={types ? optsFrom(types) : acctFileOpts()} {...props} />;
};
const TransactionFileTypeSelect = ({ helper, types, ...props }) => {
  if (DS.TransactionFileTypeSelect) return <DS.TransactionFileTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : txnFileOpts()} {...props} />;
};
const TransactionFileTypeMultiSelect = ({ types, ...props }) => {
  if (DS.TransactionFileTypeMultiSelect) return <DS.TransactionFileTypeMultiSelect types={types} {...props} />;
  return <DS.MultiSelect label="Any type" icon="receipt_long" options={types ? optsFrom(types) : txnFileOpts()} {...props} />;
};
const TaxStatementFileTypeSelect = ({ helper, types, ...props }) => {
  if (DS.TaxStatementFileTypeSelect) return <DS.TaxStatementFileTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : taxFileOpts()} {...props} />;
};
const TaxStatementFileTypeMultiSelect = ({ types, ...props }) => {
  if (DS.TaxStatementFileTypeMultiSelect) return <DS.TaxStatementFileTypeMultiSelect types={types} {...props} />;
  return <DS.MultiSelect label="Any type" icon="request_quote" options={types ? optsFrom(types) : taxFileOpts()} {...props} />;
};

// Insurance pickers — typed, registry-backed (icon + color per option), same
// fallback pattern as the other file-type selects. Insurer + insured-account
// selectors reuse the accessible Combobox atom (see InsurerCombobox below).
const policyFileOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.policyFileTypes);
const insTypeOpts   = () => optsFrom(window.OdysseyData && window.OdysseyData.insurancePolicyTypes);
const InsurancePolicyTypeSelect = ({ helper, types, ...props }) => {
  if (DS.InsurancePolicyTypeSelect) return <DS.InsurancePolicyTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : insTypeOpts()} {...props} />;
};
const PolicyFileTypeSelect = ({ helper, types, ...props }) => {
  if (DS.PolicyFileTypeSelect) return <DS.PolicyFileTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : policyFileOpts()} {...props} />;
};
const contractTypeOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.contractTypes);
const ContractTypeSelect = ({ helper, types, ...props }) => {
  if (DS.ContractTypeSelect) return <DS.ContractTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : contractTypeOpts()} {...props} />;
};
// BudgetCategoryTypeSelect — Expense / Income, each with its category glyph + color.
const BUDGET_CATEGORY_OPTS = [
  { value: 'Expense', label: 'Expense', icon: 'trending_down', iconColor: 'oklch(0.72 0.16 22)' },
  { value: 'Income',  label: 'Income',  icon: 'trending_up',   iconColor: 'oklch(0.80 0.15 150)' },
];
const BudgetCategoryTypeSelect = ({ helper, types, ...props }) => {
  if (DS.BudgetCategoryTypeSelect) return <DS.BudgetCategoryTypeSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : BUDGET_CATEGORY_OPTS} {...props} />;
};
const PolicyFileTypeMultiSelect = ({ types, ...props }) => {
  if (DS.PolicyFileTypeMultiSelect) return <DS.PolicyFileTypeMultiSelect types={types} {...props} />;
  return <DS.MultiSelect label="Any type" icon="shield" options={types ? optsFrom(types) : policyFileOpts()} {...props} />;
};

// CoverageStatusChip — the derived insurance coverage status as a chip (status
// word as text, tone-colored dot/icon aria-hidden). Typed DS component; the
// fallback renders the same shell off the kit's status meta.
const CoverageStatusChip = DS.CoverageStatusChip || (({ status = 'NoCoverage', detail, showIcon = false, size, className = '', style }) => {
  const meta = (window.OdysseyHelpers && window.OdysseyHelpers.insCoverageStatusMeta(status)) || { label: status, tone: 'outline', dot: true, icon: 'shield' };
  return (
    <span className={`odc-chip ${meta.tone}${size === 'sm' ? ' sm' : ''} ${className}`.trim()} style={style}>
      {showIcon ? <span className="material-icons" aria-hidden="true">{meta.icon}</span> : meta.dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
      {meta.label}
      {detail ? <span className="odc-coverage-detail">{detail}</span> : null}
    </span>
  );
});

// Subscriptions — BillingInterval pickers + read chips. Typed DS components with
// registry-fed / shipped-markup fallbacks (same pattern as the insurance atoms),
// so the Subscriptions page holds even if the compiled bundle lags a turn behind.
const subIntervalOpts = () => optsFrom(window.OdysseyData && window.OdysseyData.billingIntervals);
const BillingIntervalSelect = ({ helper, types, ...props }) => {
  if (DS.BillingIntervalSelect) return <DS.BillingIntervalSelect help={helper} types={types} {...props} />;
  return <DS.Select help={helper} options={types ? optsFrom(types) : subIntervalOpts()} {...props} />;
};
const BillingIntervalMultiSelect = ({ types, ...props }) => {
  if (DS.BillingIntervalMultiSelect) return <DS.BillingIntervalMultiSelect types={types} {...props} />;
  return <DS.MultiSelect label="Any interval" icon="autorenew" options={types ? optsFrom(types) : subIntervalOpts()} {...props} />;
};
const BillingIntervalChip = DS.BillingIntervalChip || (({ interval = 'Monthly', count = 1, firstBillingDate, anchor, size, className = '', style }) => {
  const reg = (window.OdysseyData && window.OdysseyData.billingIntervalByKey) || {};
  const meta = reg[interval] || { label: interval, icon: 'autorenew', color: 'var(--ink-300)' };
  const n = Math.round(Number(count)); const every = Number.isFinite(n) && n > 0 ? n : 1;
  const nouns = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };
  const label = every > 1 ? `Every ${every} ${nouns[interval] || 'cycle'}s` : meta.label;
  const derived = anchor != null ? anchor : (window.OdysseyHelpers && window.OdysseyHelpers.subBillingAnchor({ interval, firstBillingDate }));
  return (
    <span className={`odc-typechip${size === 'sm' ? ' sm' : ''} ${className}`.trim()} style={style}>
      <span className="material-icons odc-typechip-ic" style={{ color: meta.color }} aria-hidden="true">{meta.icon}</span>
      <span className="odc-typechip-name">{label}</span>
      {derived ? <span className="odc-typechip-group">{derived}</span> : null}
    </span>
  );
});
const SUB_STATE_META = { Paused: { label: 'Paused', tone: 'pending' }, Ended: { label: 'Ended', tone: 'expense' }, Archived: { label: 'Archived', tone: 'outline' }, Active: { label: 'Active', tone: 'income' } };
const SubscriptionStatusChip = DS.SubscriptionStatusChip || (({ paused, ended, archived, showActive = false, size, className = '', style }) => {
  // One state, by precedence: Archived → Ended → Paused → Active.
  const key = archived ? 'Archived' : ended ? 'Ended' : paused ? 'Paused' : null;
  if (!key && !showActive) return null;
  const keys = [key || 'Active'];
  return (
    <span className={`odc-substatus ${className}`.trim()} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', ...style }}>
      {keys.map((k) => { const m = SUB_STATE_META[k]; return <span key={k} className={`odc-chip ${m.tone}${size === 'sm' ? ' sm' : ''}`}>{m.label}</span>; })}
    </span>
  );
});

// Combobox — the accessible searchable single-select (insurer / insured-account
// pickers). Aliased straight from the bundle.
const Combobox = DS.Combobox;

// SegmentedControl — compact 2–3 option toggle (the contract Term / One-off
// switch, dense view switches). Aliased straight from the bundle.
const SegmentedControl = DS.SegmentedControl;

// ---- Overview breakdown helpers (shared by every page's header Overview) ----
// Status tone → the finance accent it maps to, so "By status" rows tint
// consistently with the chips.
const ODC_TONE = {
  income: 'var(--finance-income)', expense: 'var(--finance-expense)',
  pending: 'var(--finance-pending)', info: 'var(--sea-400)',
  outline: 'var(--mud-palette-text-secondary)', neutral: 'var(--mud-palette-text-secondary)',
  tag: 'var(--tag-text)',
};
// Build BreakdownTile "By type" rows from a registry (icon + color per key),
// counting `list` by `typeOf`; only types present (count > 0) are shown, in
// registry order.
const odcTypeRows = (list, registry, typeOf) => {
  const counts = {};
  for (const x of list) { const k = typeOf(x); counts[k] = (counts[k] || 0) + 1; }
  return (registry || []).filter(t => counts[t.key]).map(t => ({ key: t.key, icon: t.icon, iconColor: t.color, label: t.label, count: counts[t.key] }));
};
// Build BreakdownTile "By status" rows from fixed status defs
// ([{key,label,tone,icon}]), counting `list` by `statusOf`. All defs are shown
// (including zero counts), matching the Contracts / Insurance breakdowns.
const odcStatusRows = (list, defs, statusOf) => {
  const counts = {};
  for (const x of list) { const k = statusOf(x); counts[k] = (counts[k] || 0) + 1; }
  return (defs || []).map(d => ({ key: d.key, icon: d.icon, iconColor: ODC_TONE[d.tone] || ODC_TONE.outline, label: d.label, count: counts[d.key] || 0 }));
};

// MatchIndicator — the per-cell AI-match annotation (source + confidence as text).
// Aliased from the bundle, with a shipped-markup fallback so the Analyze dialog
// renders match state even if the compiled bundle lags a turn behind (same
// pattern as SearchField / AmountField).
const MatchIndicator = DS.MatchIndicator || (({ state = 'none', confidence = null, name, applyLabel = 'Use', onApply, onDismiss, createName, createLabel = 'Create', onCreate, size, className = '', id }) => {
  const pct = confidence == null ? null : Math.round(confidence * 100);
  const band = pct == null ? null : pct >= 85 ? 'High match' : pct >= 60 ? 'Good match' : 'Low match';
  const cls = ['odc-match', state, size === 'sm' ? 'sm' : '', className].filter(Boolean).join(' ');
  if (state === 'suggestion') {
    return (
      <span className={cls} id={id}>
        <span className="odc-match-sugg-head">
          <span className="material-icons odc-match-ic" aria-hidden="true">auto_awesome</span>
          <span className="odc-match-txt">Suggested by AI</span>
          {pct != null ? <span className="odc-match-pct">· {pct}%</span> : null}
          {band ? <span className="sr-only"> — {band}, below the auto-link threshold</span> : null}
        </span>
        {(onApply || onDismiss) ? (
          <span className="odc-match-sugg-actions">
            {onApply ? <button type="button" className="odc-match-act" onClick={onApply} aria-label={`${applyLabel} ${name}`}><span className="material-icons" aria-hidden="true">add</span><span className="odc-match-act-txt">{`${applyLabel} ${name}`}</span></button> : null}
            {onDismiss ? <button type="button" className="odc-match-x" aria-label={name ? `Dismiss suggestion ${name}` : 'Dismiss suggestion'} onClick={onDismiss}><span className="material-icons" aria-hidden="true">close</span></button> : null}
          </span>
        ) : null}
      </span>
    );
  }
  const META = { ai: { icon: 'auto_awesome', label: 'Suggested by AI' }, created: { icon: 'add_circle', label: 'Created here' }, manual: { icon: 'edit', label: 'You chose' }, none: { icon: 'remove', label: 'No match' } };
  const m = META[state] || META.none;
  const offerCreate = state === 'none' && !!onCreate && !!createName;
  return (
    <span className={cls} id={id}>
      <span className="material-icons odc-match-ic" aria-hidden="true">{m.icon}</span>
      <span className="odc-match-txt">{m.label}</span>
      {state === 'ai' && pct != null ? <span className="odc-match-pct">· {pct}%</span> : null}
      {state === 'ai' && band ? <span className="sr-only"> — {band}</span> : null}
      {offerCreate ? (
        <span className="odc-match-createline">
          <button type="button" className="odc-match-create" onClick={onCreate} aria-label={`${createLabel} ${createName}`}>
            <span className="material-icons" aria-hidden="true">add</span>
            <span className="odc-match-create-txt">{`${createLabel} "${createName}"`}</span>
          </button>
        </span>
      ) : null}
    </span>
  );
});

// FileUpload — the DS drag-and-drop upload field (dropzone + ready-file list
// with inline rename / kind picker / remove). Every kit upload modal consumes
// it directly now (the old hand-rolled AfmUpload is gone); `guessKind` carries
// each surface's vocabulary and `renderFileExtra` the per-row validity editor.
const FileUpload = DS.FileUpload;

// EmptyState — the DS EmptyState is bare by design (meant to live inside a
// card); the kit shows it as a bordered surface, so wrap it. `mutedIcon`
// passes through.
const EmptyState = (props) => (
  <div className="empty-state-surface">
    <DS.EmptyState {...props} />
  </div>
);

// CardBody / CardHeader — now typed DS components (composition around a flush
// Card). Prefer the bundle versions; the local copies below are kept only as a
// bundle-lag fallback (same pattern as SeverityIcon / DateField). The DS
// versions render `.odc-card-body` / `.odc-card-header`; the fallbacks render
// the kit's `.card-body` / `.card-header` — identical metrics either way.
const LocalCardBody   = ({ children, className = '', style = {} }) => <div className={`card-body ${className}`} style={style}>{children}</div>;
const LocalCardHeader = ({ title, action }) => (
  <div className="card-header"><div className="ttl">{title}</div>{action}</div>
);
const CardBody   = DS.CardBody || LocalCardBody;
const CardHeader = DS.CardHeader || LocalCardHeader;

// ============================================================
// Kit-only atoms (no DS equivalent)
// ============================================================

// SettingRow — the typed DS setting-card row (icon + label + desc | control).
// Preferences (PrefCard) and System settings (SettingCard) both delegate to it.
// Falls back to a local copy until the bundle carries it.
const SettingRow = DS.SettingRow || (({ icon, title, desc, danger, children }) => (
  <Card outlined>
    <CardBody className="pref-row">
      <div className="pref-main">
        <span className={`pref-ic ${danger ? 'danger' : ''}`.trim()}><MIcon name={icon} size={20} /></span>
        <div className="pref-text">
          <div className="pref-ttl">{title}</div>
          {desc && <div className="pref-desc">{desc}</div>}
        </div>
      </div>
      <div className="pref-control">{children}</div>
    </CardBody>
  </Card>
));

// SettingField — one setting as a notched-outline field block (label on the
// outline, control inside, description + last-changed stamp on one helper line).
// The scaffold for the System settings grid. Typed DS component; the fallback
// mirrors the markup so the page renders across a bundle rebuild.
const SettingField = DS.SettingField || (({ label, htmlFor, labelId, help, meta, error, advisory, bound, dirty, wide, className = '', children, ...rest }) => (
  <div className={`odc-sfield${wide ? ' wide' : ''}${className ? ' ' + className : ''}`} {...rest}>
    <fieldset className={`odc-sfield-frame${error ? ' error' : ''}${advisory ? ' advised' : ''}`}>
      <legend className="odc-sfield-legend">
        <label className="odc-sfield-label" id={labelId || (htmlFor ? htmlFor + '-label' : undefined)} htmlFor={htmlFor}>{label}</label>
        {bound ? <span className="odc-sfield-bound">{bound === 'raise-only' ? 'raise only' : 'lower only'}</span> : null}
      </legend>
      <div className="odc-sfield-ctrl">{children}</div>
    </fieldset>
    {error ? <div className="odc-sfield-err" role="alert">{error}</div> : null}
    {(help || meta) ? (
      <div className="odc-sfield-help" id={htmlFor ? htmlFor + '-help' : undefined}>
        {help ? <span>{help} </span> : null}
        {meta ? <span className="odc-sfield-stamp">{meta}</span> : null}
        {dirty ? <span className="odc-setting-dot" title="Unsaved change" aria-hidden="true" /> : null}
      </div>
    ) : null}
    {advisory ? (
      <div className="odc-sfield-advisory" role="status">
        <span className="material-icons" aria-hidden="true">info</span>
        <div><b className="odc-sfield-advisory-t">Advisory</b> {advisory}</div>
      </div>
    ) : null}
  </div>
));

// CapacityField — the numeric-limit-or-"No limit" control behind the count caps
// on the System settings import/export groups. Typed DS component; the fallback
// composes the kit's NumberField + Switch so it renders across a bundle rebuild.
const CapacityField = DS.CapacityField || (({ value = null, onValueChange, unlimited = false, onUnlimitedChange, label, ariaLabelledBy, ariaDescribedBy, error, min = 1, max = 1000000, disabled = false, variant = 'stacked', className = '' }) => (
  variant === 'inline' ? (
    <div className={`odc-capacity inline${className ? ' ' + className : ''}`}>
      {unlimited
        ? <span className="odc-capacity-value">No limit</span>
        : <NumberField className="odc-capacity-num" value={value} min={min} max={max} step={1} align="right"
            disabled={disabled} error={error || undefined} ariaLabelledBy={ariaLabelledBy} ariaDescribedBy={ariaDescribedBy}
            onChange={(v) => onValueChange && onValueChange(v)} />}
      <button type="button" className="odc-capacity-pill" disabled={disabled} aria-pressed={unlimited}
        onClick={() => onUnlimitedChange && onUnlimitedChange(!unlimited)}>
        {unlimited ? 'Set a limit' : 'No limit'}
      </button>
    </div>
  ) : (
  <div className={`odc-capacity${className ? ' ' + className : ''}`}>
    {unlimited
      ? <div className="odc-capacity-nolimit">No limit</div>
      : <NumberField className="odc-capacity-num" value={value} min={min} max={max} step={1} align="right"
          disabled={disabled} error={error || undefined} ariaLabelledBy={ariaLabelledBy} ariaDescribedBy={ariaDescribedBy}
          onChange={(v) => onValueChange && onValueChange(v)} />}
    <label className={`odc-capacity-toggle${disabled ? ' disabled' : ''}`}>
      <span className="odc-capacity-toggle-lbl">No limit</span>
      <Switch checked={unlimited} disabled={disabled} aria-label={`${label || 'Limit'} \u2014 no limit`}
        onChange={(c) => onUnlimitedChange && onUnlimitedChange(c)} />
    </label>
  </div>
  )
));

// TextInputField — the native single-line text input for controls that must be
// labelled/described by elements they don't own (a setting row's title). Typed
// DS component; the fallback composes the shell markup directly so the live
// screens keep working across a bundle rebuild.
const TextInputField = DS.TextInputField || (({ label, value = '', onChange, placeholder, maxLength, showCount, inputMode, help, error, required, optional, disabled, className = '', id, ariaLabelledBy, ariaDescribedBy }) => {
  const autoId = React.useId();
  const fid = id || autoId;
  const helpId = `${fid}-help`;
  const described = [ariaDescribedBy, help ? helpId : null, error ? helpId : null].filter(Boolean).join(' ') || undefined;
  const input = (
    <input id={fid} className="odc-input" type="text" value={value == null ? '' : value}
      placeholder={placeholder} maxLength={maxLength} inputMode={inputMode} disabled={disabled}
      aria-invalid={error ? true : undefined} aria-labelledby={ariaLabelledBy} aria-describedby={described}
      onChange={(ev) => onChange && onChange(ev.target.value, ev)} />
  );
  const count = (showCount && maxLength) ? <span className="odc-field-count">{(value || '').length}/{maxLength}</span> : null;
  if (DS.FieldShell) {
    return <DS.FieldShell label={label} htmlFor={fid} required={required} optional={optional}
      help={help} error={error} aside={count} className={className}>{input}</DS.FieldShell>;
  }
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {input}
      {(error || help) ? <div className={`odc-field-help${error ? ' error' : ''}`} id={helpId} role={error ? 'alert' : undefined}>{error || help}</div> : null}
    </div>
  );
});

// ErrorSummary — the "n problems · Review" control that sits before a disabled
// primary action on a page long enough that the blocking field is off-screen.
// With `problems` it expands to a list that focuses each offending field.
const ErrorSummary = (props) => {
  // Derive the count from `problems` so this renders against either the current
  // DS component (expandable list) or an older compiled one (count only).
  const n = props.count || (props.problems || []).length;
  if (!n) return null;
  if (DS.ErrorSummary) return <DS.ErrorSummary {...props} count={n} />;
  return <KitErrorSummary {...props} count={n} />;
};

const KitErrorSummary = ({ count, problems, onReview, onJump, noun = 'problem', action = 'Review' }) => {
  const [open, setOpen] = useState(false);
  const list = problems || [];
  const n = count || list.length;
  if (!n) return null;
  const label = `${n} ${noun}${n === 1 ? '' : 's'}`;
  const expandable = list.length > 0;
  const jump = (p) => {
    setOpen(false);
    if (onJump) onJump(p);
    else if (p.targetId) { const el = document.getElementById(p.targetId); if (el) el.focus(); }
  };
  return (
    <div className="odc-errsum-wrap">
      <button type="button" className="odc-errsum" aria-label={`${label}, ${action.toLowerCase()}`}
        aria-expanded={expandable ? open : undefined}
        onClick={() => (expandable ? setOpen(o => !o) : onReview && onReview())}>
        <span className="material-icons" aria-hidden="true">error_outline</span>
        <span className="odc-errsum-count" aria-hidden="true">{label}</span>
        <span className="odc-errsum-sep" aria-hidden="true">·</span>
        <span className="odc-errsum-act" aria-hidden="true">{action}</span>
        {expandable ? <span className="material-icons odc-errsum-chev" aria-hidden="true">{open ? 'expand_less' : 'expand_more'}</span> : null}
      </button>
      {expandable && open ? (
        <div className="odc-errsum-panel">
          <div className="odc-errsum-panel-head">Fix these to save</div>
          <ul className="odc-errsum-list">
            {list.map((p, i) => (
              <li key={p.targetId || i}>
                <button type="button" className="odc-errsum-item" onClick={() => jump(p)}>
                  <span className="odc-errsum-item-txt">
                    <span className="odc-errsum-item-lbl">{p.label}</span>
                    {p.section ? <span className="odc-errsum-item-sec">{p.section}</span> : null}
                  </span>
                  <span className="material-icons odc-errsum-item-go" aria-hidden="true">arrow_forward</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
};

// ---- Fixed-position popover helper ----
// Measures the trigger on open and returns coords so the pop escapes any
// overflow:hidden ancestor (cards / collapsibles). Used by DateField + the
// page-level MultiSelect filters.
const usePopover = () => {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const btnRef = useRef(null);
  const openWith = () => {
    if (btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 6, left: r.left, width: r.width });
    }
    setOpen(true);
  };
  const toggle = () => (open ? setOpen(false) : openWith());
  useEffect(() => {
    if (!open) return;
    const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    const onScroll = (e) => { if (ref.current && ref.current.contains(e.target)) return; setOpen(false); };
    const onResize = () => setOpen(false);
    // Esc closes only this popover (capture + stop — a Modal underneath stays
    // open) and restores keyboard focus to the trigger.
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      setOpen(false);
      if (btnRef.current) btnRef.current.focus();
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open]);
  return { open, setOpen, pos, ref, btnRef, toggle };
};

// ---- DateField (labelled date field — DatePicker in FieldShell) ----
// Now a typed DS component (components/DateField.jsx). The local calendar below
// is kept only as a bundle-lag fallback (same pattern as SeverityIcon /
// BrandMark / AddRow) — `const DateField = DS.DateField || LocalDateField`
// after the definition upgrades every date field (and Field type="date") to the
// DS DatePicker's keyboard grid / portaled, flipping popover / min-max.
const DATE_MONTHS = ['January','February','March','April','May','June',
  'July','August','September','October','November','December'];
const DATE_WD = ['Mo','Tu','We','Th','Fr','Sa','Su'];
const datePad = (n) => String(n).padStart(2, '0');
const dateToISO = (y, m, d) => `${y}-${datePad(m + 1)}-${datePad(d)}`;
const dateParseISO = (s) => {
  if (!s) return null;
  const [y, m, d] = String(s).split('-').map(Number);
  if (!y || !m || !d) return null;
  return { y, m: m - 1, d };
};
const dateDisplay = (s) => {
  const p = dateParseISO(s);
  return p ? `${datePad(p.d)} ${DATE_MONTHS[p.m].slice(0, 3)} ${p.y}` : '';
};

const LocalDateField = ({ label, value, onChange, helper, placeholder = 'Select date' }) => {
  const { open, pos, ref, btnRef, toggle, setOpen } = usePopover();
  const dpId = React.useId();
  const parsed = dateParseISO(value);
  const now = new Date();
  const [view, setView] = useState({
    y: parsed ? parsed.y : now.getFullYear(),
    m: parsed ? parsed.m : now.getMonth(),
  });
  useEffect(() => {
    if (open && parsed) setView({ y: parsed.y, m: parsed.m });
  }, [open]);

  const firstDow = (new Date(view.y, view.m, 1).getDay() + 6) % 7; // Mon-first
  const daysInMonth = new Date(view.y, view.m + 1, 0).getDate();
  const cells = [];
  for (let i = 0; i < firstDow; i++) cells.push(null);
  for (let d = 1; d <= daysInMonth; d++) cells.push(d);

  const step = (delta) => setView(v => {
    const m = v.m + delta;
    return { y: v.y + Math.floor(m / 12), m: ((m % 12) + 12) % 12 };
  });
  const isToday = (d) => d && view.y === now.getFullYear() && view.m === now.getMonth() && d === now.getDate();
  const isSel = (d) => d && parsed && parsed.y === view.y && parsed.m === view.m && parsed.d === d;

  return (
    <div className="field">
      {label && <div className="label">{label}</div>}
      <div className="multiselect" ref={ref}>
        <button type="button" ref={btnRef}
          className={`multiselect-trigger datefield-trigger ${open ? 'active' : ''} ${value ? '' : 'placeholder'}`}
          aria-haspopup="dialog" aria-expanded={open} aria-controls={open ? dpId : undefined}
          aria-label={label ? `${label}${value ? `: ${dateDisplay(value)}` : ''}` : undefined}
          onClick={toggle}>
          <span className="multiselect-summary">{value ? dateDisplay(value) : placeholder}</span>
          <MIcon name="calendar_today" size={18} className="datefield-ic" />
        </button>
        {open && pos && (
          <div id={dpId} className="acct-menu-pop datepicker-pop" role="dialog" aria-label={label || 'Choose date'}
            style={{ top: pos.top, left: pos.left }}>
            <div className="dp-head">
              <button type="button" className="dp-nav" onClick={() => step(-1)} aria-label="Previous month">
                <MIcon name="chevron_left" size={20} />
              </button>
              <span className="dp-title">{DATE_MONTHS[view.m]} {view.y}</span>
              <button type="button" className="dp-nav" onClick={() => step(1)} aria-label="Next month">
                <MIcon name="chevron_right" size={20} />
              </button>
            </div>
            <div className="dp-grid dp-wd">
              {DATE_WD.map(w => <span key={w} className="dp-wd-cell">{w}</span>)}
            </div>
            <div className="dp-grid">
              {cells.map((d, i) => d === null
                ? <span key={i} className="dp-cell empty" />
                : (
                  <button key={i} type="button"
                    className={`dp-cell ${isSel(d) ? 'sel' : ''} ${isToday(d) ? 'today' : ''}`}
                    onClick={() => { onChange && onChange(dateToISO(view.y, view.m, d)); setOpen(false); }}>
                    {d}
                  </button>
                ))}
            </div>
            <div className="dp-foot">
              <button type="button" className="dp-foot-btn" onClick={() => {
                onChange && onChange(dateToISO(now.getFullYear(), now.getMonth(), now.getDate())); setOpen(false);
              }}>Today</button>
              {value && (
                <button type="button" className="dp-foot-btn clear" onClick={() => { onChange && onChange(''); setOpen(false); }}>Clear</button>
              )}
            </div>
          </div>
        )}
      </div>
      {helper && <div className="helper">{helper}</div>}
    </div>
  );
};

// Prefer the typed DS DateField; fall back to the local calendar above until
// the compiled bundle carries it (keeps date entry working across a rebuild).
const DateField = DS.DateField || LocalDateField;

// DateRangePicker — the DS two-field date-range pill (components/DateRangePicker.jsx).
// Prefer the bundle version; until it carries the freshly-added component, fall
// back to a local compose of two DS DatePickers (already in the bundle) with the
// same `.odc-dpr` shell — so a range filter renders identically across a rebuild.
const LocalDateRangePicker = ({ value, onChange, label, icon = 'event', fromPlaceholder = 'From', toPlaceholder = 'To', min, max, clamp = true, align = 'start', ariaLabel = 'Filter by date range', id, className = '', style }) => {
  const autoId = React.useId();
  const rootId = id || autoId;
  const DatePicker = DS.DatePicker;
  const from = (value && value.from) || null;
  const to = (value && value.to) || null;
  const emit = (next) => onChange && onChange(next);
  const fromMax = clamp && to ? to : max;
  const toMin = clamp && from ? from : min;
  return (
    <div className={`odc-dpr${className ? ' ' + className : ''}`} role="group" aria-label={ariaLabel} style={style}>
      {icon ? <MIcon name={icon} size={16} className="odc-dpr-ic" /> : null}
      {label ? <span className="odc-dpr-lab">{label}</span> : null}
      {DatePicker ? (
        <>
          <DatePicker id={`${rootId}-from`} value={from} placeholder={fromPlaceholder} onChange={(v) => emit({ from: v || null, to })} min={min} max={fromMax} align={align} />
          <span className="odc-dpr-dash" aria-hidden="true">–</span>
          <DatePicker id={`${rootId}-to`} value={to} placeholder={toPlaceholder} onChange={(v) => emit({ from, to: v || null })} min={toMin} max={max} align={align} />
        </>
      ) : (
        <>
          <input className="odc-input" type="date" value={from || ''} aria-label={fromPlaceholder} min={min} max={fromMax} onChange={(e) => emit({ from: e.target.value || null, to })} />
          <span className="odc-dpr-dash" aria-hidden="true">–</span>
          <input className="odc-input" type="date" value={to || ''} aria-label={toPlaceholder} min={toMin} max={max} onChange={(e) => emit({ from, to: e.target.value || null })} />
        </>
      )}
      {(from || to) ? (
        <button type="button" className="odc-dpr-clear" aria-label="Clear date range" onClick={() => emit({ from: null, to: null })}>
          <MIcon name="close" size={15} />
        </button>
      ) : null}
    </div>
  );
};
const DateRangePicker = DS.DateRangePicker || LocalDateRangePicker;

// SeverityIcon — now a typed DS component (components/SeverityIcon.jsx).
// Falls back to a local copy until the bundle carries it (keeps the live
// screens working across a bundle rebuild), same pattern as SearchField.
const SeverityIcon = DS.SeverityIcon || (({ severity = 'warning', size = 18, className = '', style = {} }) => {
  if (severity === 'warning') return (
    <svg className={className} width={size} height={size} viewBox="0 0 24 24"
      fill="currentColor" aria-hidden="true" style={{ flex: 'none', ...style }}>
      <path d="M11.13 3.66 1.73 19.5a1 1 0 0 0 .87 1.5h18.8a1 1 0 0 0 .87-1.5L12.87 3.66a1 1 0 0 0-1.74 0Zm.87 4.59a1.05 1.05 0 0 1 1.05 1.13l-.33 4.7a.72.72 0 0 1-1.44 0l-.33-4.7A1.05 1.05 0 0 1 12 8.25Zm0 8.0a1.12 1.12 0 1 1 0 2.25 1.12 1.12 0 0 1 0-2.25Z"/>
    </svg>
  );
  return <MIcon name={severity === 'error' ? 'error_outline' : 'info_outline'} size={size} className={className} style={style} />;
});

// Account tone helper — maps the data.js tone keys to {bg,fg} pairs the DS
// Avatar accepts as a custom tone object.
const TONE_MAP = {
  tide:   { bg: 'rgba(79, 215, 203, 0.14)', fg: 'var(--tide-400)' },
  sea:    { bg: 'rgba(26,165,224,0.14)', fg: 'var(--sea-400)' },
  violet: { bg: 'rgba(139,92,246,0.14)', fg: 'var(--violet-500)' },
  mint:   { bg: 'rgba(61,214,140,0.14)', fg: 'var(--mint-500)' },
  coral:  { bg: 'rgba(255,107,107,0.14)', fg: 'var(--coral-500)' },
};

// ---- Brand mark (compass rose) ----
// Now a typed DS component (components/BrandMark.jsx) — falls back to a local
// copy until the bundle carries it (the drawer + login lockups must never
// render blank). Colors are the brand's exact hex values.
const BrandMark = DS.BrandMark || (({ size = 28, withWordmark = false }) => {
  const FRAME = '#006B5A';
  const GLOW  = '#00F5D4';
  const GRAY  = '#707070';
  const DARK  = '#404040';
  const vb = withWordmark ? '0 0 200 240' : '0 0 200 210';
  // Render compass at the (100,105) center, r=90 outer. Bold strokes + rose.
  return (
    <svg width={size} height={size * (withWordmark ? 1.2 : 1.05)} viewBox={vb} fill="none" role="img" aria-label="Odyssey">
      {/* Rings */}
      <circle cx="100" cy="105" r="90" fill="none" stroke={FRAME} strokeWidth="13"/>
      <circle cx="100" cy="105" r="66" fill="none" stroke={FRAME} strokeWidth="2.5" strokeDasharray="6 4"/>
      {/* Cardinal ticks */}
      <line x1="100" y1="9"   x2="100" y2="29"  stroke={FRAME} strokeWidth="7"/>
      <line x1="100" y1="181" x2="100" y2="201" stroke={FRAME} strokeWidth="7"/>
      <line x1="4"   y1="105" x2="24"  y2="105" stroke={FRAME} strokeWidth="7"/>
      <line x1="176" y1="105" x2="196" y2="105" stroke={FRAME} strokeWidth="7"/>
      {/* Ordinal ticks */}
      <line x1="36"  y1="41"  x2="50"  y2="55"  stroke={FRAME} strokeWidth="5"/>
      <line x1="164" y1="41"  x2="150" y2="55"  stroke={FRAME} strokeWidth="5"/>
      <line x1="36"  y1="169" x2="50"  y2="155" stroke={FRAME} strokeWidth="5"/>
      <line x1="164" y1="169" x2="150" y2="155" stroke={FRAME} strokeWidth="5"/>
      {/* Compass rose */}
      <polygon points="100,25  91,105 100,82  109,105" fill={GLOW}/>
      <polygon points="100,105 91,105 100,82  109,105" fill={FRAME}/>
      <polygon points="100,185 91,105 100,128 109,105" fill={GRAY}/>
      <polygon points="100,105 91,105 100,128 109,105" fill={DARK}/>
      <polygon points="185,105 100,96 123,105 100,114" fill={GRAY}/>
      <polygon points="100,105 100,96 123,105 100,114" fill={DARK}/>
      <polygon points="15,105  100,96 77,105  100,114" fill={GRAY}/>
      <polygon points="100,105 100,96 77,105  100,114" fill={DARK}/>
      {/* Center pivot */}
      <circle cx="100" cy="105" r="11" fill="none" stroke={FRAME} strokeWidth="3"/>
      <circle cx="100" cy="105" r="5"  fill={GLOW}/>
      {withWordmark && (
        <text x="100" y="226" textAnchor="middle"
              fontFamily="Roboto, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif"
              fontWeight="500" fontSize="18" letterSpacing="5" fill={GLOW}>ODYSSEY</text>
      )}
    </svg>
  );
});

// ---- AddRow (closing affordance for a list — full-width dashed "add" row) ----
// Now a typed DS component (components/AddRow.jsx) — falls back to a local copy
// until the bundle carries it. Pass a `title` (the verb) and an optional
// one-line `sub`; `icon` defaults to `add`.
const AddRow = DS.AddRow || (({ title, sub, icon = 'add', onClick, className = '' }) => (
  <button type="button" className={`acct-add ${className}`} onClick={onClick}>
    <Avatar icon={icon} size="lg" square tone={{ bg: 'rgba(255,255,255,0.05)', fg: 'var(--ink-200)' }} />
    <div className="acct-add-text">
      <div className="acct-add-title">{title}</div>
      {sub && <div className="acct-add-sub">{sub}</div>}
    </div>
  </button>
));

// ---- Collapsible (disclosure) ----
// The DS Collapsible gained a leading `icon` + a right-aligned `action` slot
// (the Files / Transactions / Terms record sections, Budgets items, Users
// role-permissions all need them). Until the compiled bundle carries that
// revision, fall back to a local copy with identical markup — same pattern as
// SeverityIcon / BrandMark / AddRow. `supportsAction` flags the new bundle.
const LocalCollapsible = ({ title, icon, lead, count, action, open, defaultOpen = false, onToggle, flush = false, headingLevel = 2, children }) => {
  const isControlled = open !== undefined;
  const [cOpen, setCOpen] = useState(defaultOpen);
  const isOpen = isControlled ? open : cOpen;
  const glyph = icon || lead;
  const toggle = () => { const next = !isOpen; if (!isControlled) setCOpen(next); if (onToggle) onToggle(next); };
  const trigger = (
    <button type="button" className="odc-collapsible-trigger" aria-expanded={isOpen} onClick={toggle}>
      <span className="material-icons odc-collapsible-chev" aria-hidden="true">expand_more</span>
      {glyph ? (
        /^[a-z0-9_]+$/.test(glyph)
          ? <span className="material-icons odc-collapsible-lead" aria-hidden="true">{glyph}</span>
          : <span className="odc-collapsible-lead glyph" aria-hidden="true">{glyph}</span>
      ) : null}
      <span className="odc-collapsible-title">{title}</span>
      {count != null ? <span className="odc-collapsible-count">{count}</span> : null}
    </button>
  );
  return (
    <div className={`odc-collapsible${flush ? ' flush' : ''}`} data-open={isOpen ? '' : undefined}>
      <div className="odc-collapsible-head">
        {headingLevel
          ? <div role="heading" aria-level={headingLevel} style={{ display: 'contents' }}>{trigger}</div>
          : trigger}
        {action ? <div className="odc-collapsible-action" onClick={(e) => e.stopPropagation()}>{action}</div> : null}
      </div>
      {isOpen ? <div className="odc-collapsible-body">{children}</div> : null}
    </div>
  );
};
const Collapsible = (DS.Collapsible && DS.Collapsible.supportsAction) ? DS.Collapsible : LocalCollapsible;

// Multi-tag picker + read display, from the bundle (no kit fallback — these are
// new typed components for the many-to-many transaction tags).
const TagMultiSelect = DS.TagMultiSelect || (() => null);
// Journal module composites — new typed bundle components. Resolved LAZILY at
// render (not captured at load) so they appear as soon as _ds_bundle.js carries
// them, regardless of script/compile order.
const TodoStatusChip = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).TodoStatusChip; return C ? <C {...props} /> : null; };
const JournalPhotoGallery = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).JournalPhotoGallery; return C ? <C {...props} /> : null; };
const TaskBoard = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).TaskBoard; return C ? <C {...props} /> : null; };
// Calendar module — new typed bundle components (CalendarGrid month view,
// TimeField, ColorSwatchSelect). Resolved LAZILY at render so they appear as
// soon as _ds_bundle.js carries them, regardless of script/compile order.
const CalendarGrid = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).CalendarGrid; return C ? <C {...props} /> : <div className="cal-loading">Loading calendar…</div>; };
const TimeField = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).TimeField; return C ? <C {...props} /> : null; };
const CoordinateField = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).CoordinateField; return C ? <C {...props} /> : null; };
const StepperField = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).StepperField; return C ? <C {...props} /> : null; };
const ColorSwatchSelect = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).ColorSwatchSelect; return C ? <C {...props} /> : null; };
const RevealPanel = (props) => { const C = (window.OdysseyDesignSystem_d5aa51 || {}).RevealPanel; return C ? <C {...props} /> : null; };
// Password policy + rules checklist — the ONE shared source for the 16-char +
// four-class rules, consumed by Register, /account change-password, and
// /reset-password. Prefer the typed DS export; fall back to a local mirror so
// the surfaces still render while the bundle build lags a turn behind.
const PASSWORD_POLICY = (window.OdysseyDesignSystem_d5aa51 || {}).PASSWORD_POLICY || {
  minLength: 16,
  rules(candidate) {
    const s = candidate || '', n = 16;
    return [
      { key: 'len', label: `At least ${n} characters`, met: s.length >= n },
      { key: 'upper', label: 'An uppercase letter', met: /[A-Z]/.test(s) },
      { key: 'lower', label: 'A lowercase letter', met: /[a-z]/.test(s) },
      { key: 'digit', label: 'A number', met: /\d/.test(s) },
      { key: 'sym', label: 'A symbol (!@#$…)', met: /[^A-Za-z0-9]/.test(s) },
    ];
  },
  isSatisfied(candidate) { return this.rules(candidate).every((r) => r.met); },
};
const PasswordRules = (props) => {
  const C = (window.OdysseyDesignSystem_d5aa51 || {}).PasswordRules;
  if (C) return <C {...props} />;
  const rules = PASSWORD_POLICY.rules(props.password || '');
  return (
    <ul className={`odc-pw-rules${props.columns === 2 ? ' cols-2' : ''}${props.className ? ' ' + props.className : ''}`}
        aria-label={props['aria-label'] || 'Password requirements'}>
      {rules.map((r) => (
        <li key={r.key} className={`odc-pw-rule${r.met ? ' met' : ''}`}>
          <span className="material-icons" aria-hidden="true">{r.met ? 'check_circle' : 'radio_button_unchecked'}</span>
          <span>{r.label}</span>
          <span className="sr-only">{r.met ? ' — met' : ' — not yet met'}</span>
        </li>
      ))}
    </ul>
  );
};
// Palette + lookup — value data, with a local mirror as the bundle-lag fallback.
const CALENDAR_SWATCHES = (DS.CALENDAR_SWATCHES && DS.CALENDAR_SWATCHES.length) ? DS.CALENDAR_SWATCHES : [
  { key: 'blue', name: 'Blue', hex: '#0369A1', fg: '#FFFFFF' }, { key: 'teal', name: 'Teal', hex: '#006B5A', fg: '#FFFFFF' },
  { key: 'green', name: 'Green', hex: '#15803D', fg: '#FFFFFF' }, { key: 'coral', name: 'Coral', hex: '#B23B3B', fg: '#FFFFFF' },
  { key: 'violet', name: 'Violet', hex: '#6D28D9', fg: '#FFFFFF' }, { key: 'slate', name: 'Slate', hex: '#4A5670', fg: '#FFFFFF' },
  { key: 'amber', name: 'Amber', hex: '#F59E0B', fg: '#0E1525' }, { key: 'sky', name: 'Sky', hex: '#7DD3FC', fg: '#0E1525' },
];
const swatchFor = (hex) => {
  const f = (window.OdysseyDesignSystem_d5aa51 || {}).swatchFor;
  if (f) return f(hex);
  const up = String(hex || '').toUpperCase();
  return CALENDAR_SWATCHES.find((s) => s.hex.toUpperCase() === up) || CALENDAR_SWATCHES[0];
};

// Status vocabulary — a small local mirror is the fallback until the bundle's
// TODO_STATUSES is present (keeps the status Selects populated on first paint).
const TODO_STATUSES = (DS.TODO_STATUSES && DS.TODO_STATUSES.length) ? DS.TODO_STATUSES : [
  { key: 'Backlog', label: 'Backlog', value: 0 },
  { key: 'Doing', label: 'Doing', value: 1 },
  { key: 'Done', label: 'Done', value: 2 },
  { key: 'Archived', label: 'Archived', value: 3 },
];
const TagChips = DS.TagChips || (({ tags = [], empty = '—' }) => (
  tags.length ? tags.map((t, i) => <span className="odc-chip tag" key={i}>{typeof t === 'string' ? t : (t.label || t.name)}</span>) : <span>{empty}</span>
));

// AccountSmartTagsSection — the per-account "Smart tags" disclosure (typed DS
// component, no kit fallback). Accounts.jsx wraps it with per-account state.
const AccountSmartTagsSection = DS.AccountSmartTagsSection || (() => null);

// Custodian — the account ↔ contact ("held at") link. CustodianChip is the
// read-only informational chip on the account card; CustodianSelect is the
// optional picker on the create dialog + inline edit grid. Typed DS components;
// minimal fallbacks keep the live screens working across a bundle rebuild.
// PasswordChangeForm — the shared current→new→confirm triad + live rules +
// error banner, consumed by BOTH /account's change-password section and the
// admin forced-reset gate (/change-password-required). Prefer the typed DS
// export; fall back to a local mirror so both surfaces render while the bundle
// build lags a turn behind (same pattern as PasswordRules / PASSWORD_POLICY).
const PasswordChangeForm = (props) => {
  const C = (window.OdysseyDesignSystem_d5aa51 || {}).PasswordChangeForm;
  if (C) return <C {...props} />;
  const { onSubmit, error, busy = false, submitLabel = 'Update password', busyLabel = 'Updating\u2026', submitIcon = 'lock_reset', columns = 1, autoFocus = false, className = '' } = props;
  const { useState } = React;
  const [cur, setCur] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const allMet = PASSWORD_POLICY.isSatisfied(next);
  const matches = confirm.length > 0 && next === confirm;
  const sameAsOld = next.length > 0 && next === cur;
  const canSubmit = cur.length > 0 && allMet && matches && !sameAsOld && !busy;
  const submit = (e) => { if (e && e.preventDefault) e.preventDefault(); if (!canSubmit) return; onSubmit && onSubmit({ currentPassword: cur, newPassword: next }); };
  return (
    <form className={`odc-pw-form${className ? ' ' + className : ''}`} onSubmit={submit}>
      <Field label="Current password" type="password" value={cur} onChange={setCur} placeholder="••••••••" autoComplete="current-password" autoFocus={autoFocus} />
      <div className="odc-pw-form-sep" aria-hidden="true" />
      <Field label="New password" type="password" value={next} onChange={setNext} placeholder="••••••••" autoComplete="new-password"
        error={allMet && sameAsOld ? 'Choose a password different from your current one.' : ''} />
      <PasswordRules password={next} columns={columns} />
      <Field label="Confirm new password" type="password" value={confirm} onChange={setConfirm} placeholder="••••••••" autoComplete="new-password"
        error={confirm.length > 0 && !matches ? 'Passwords do not match.' : ''} helper={matches && !sameAsOld ? 'Passwords match.' : ''} />
      {error ? <Alert severity="error">{error}</Alert> : null}
      <div className="odc-pw-form-actions">
        <Button variant="filled" color="primary" icon={submitIcon} type="submit" disabled={!canSubmit} loading={busy} onClick={submit}>{busy ? busyLabel : submitLabel}</Button>
      </div>
    </form>
  );
};

const CustodianChip = DS.CustodianChip || (({ custodian }) => (
  custodian
    ? <span className="odc-custodian"><span className="material-icons odc-custodian-ic" aria-hidden="true">account_balance</span><span className="odc-custodian-name">{custodian.name}</span></span>
    : <span className="odc-custodian empty"><span className="material-icons odc-custodian-ic" aria-hidden="true">account_balance</span><span className="odc-custodian-name">No custodian</span></span>
));
const CustodianSelect = DS.CustodianSelect || (({ value, onChange, contacts = [], label = 'Custodian', optional = true, help, error, loading, disabled }) => {
  // Functional fallback over the bundle's Combobox until it carries the typed
  // CustodianSelect — active contacts only, clearable, optional.
  const reg = (window.OdysseyData && window.OdysseyData.contactTypeByKey) || {};
  const active = contacts.filter((c) => !c.archived);
  const options = active.map((c) => {
    const m = reg[c.type] || {};
    return { value: c.id || c.contactId, label: c.name, icon: m.icon, iconColor: m.color };
  });
  const msg = error || help;
  return (
    <div className={`odc-field${error ? ' error' : ''}`}>
      <label className="odc-field-label">{label}{optional ? <span className="odc-field-opt">Optional</span> : null}</label>
      {DS.Combobox
        ? <DS.Combobox value={value || ''} onChange={(v) => onChange && onChange(v || '')} options={options} placeholder="Search contacts…" clearable loading={loading} disabled={disabled} />
        : <DS.Select value={value || ''} onChange={(v) => onChange && onChange(v || '')} options={options} placeholder="Search contacts…" />}
      {msg ? <div className="odc-field-help">{msg}</div> : null}
    </div>
  );
});

// AccountTypeChip — the account type as a chip (sibling of CustodianChip) for
// the detail metadata grid. Typed DS component with a registry-fed fallback.
const AccountTypeChip = DS.AccountTypeChip || (({ type, accountType, size, showGroup = true }) => {
  const meta = accountType || (window.OdysseyData && window.OdysseyData.accountTypeById[type]);
  if (!meta) return null;
  const groupLabel = meta.group === 'asset' ? 'Asset' : meta.group === 'liability' ? 'Liability' : null;
  return (
    <span className={`odc-typechip${size === 'sm' ? ' sm' : ''}`}>
      <span className="material-icons odc-typechip-ic" style={{ color: meta.color }} aria-hidden="true">{meta.icon}</span>
      <span className="odc-typechip-name">{meta.label}</span>
      {showGroup && groupLabel ? <span className="odc-typechip-group">{groupLabel}</span> : null}
    </span>
  );
});

// AccountStatusChip — the account status as a chip (sibling of AccountTypeChip /
// CustodianChip) for the detail metadata grid. Typed DS component with a fallback.
const STATUS_DOT = { income: 'var(--finance-income)', pending: 'var(--finance-pending)', error: 'var(--finance-expense)', outline: 'var(--mud-palette-text-secondary)', neutral: 'var(--mud-palette-text-secondary)' };
const AccountStatusChip = DS.AccountStatusChip || (({ label, tone = 'neutral', detail, size }) => {
  if (!label) return null;
  return (
    <span className={`odc-typechip${size === 'sm' ? ' sm' : ''}`}>
      <span className="odc-typechip-dot" style={{ background: STATUS_DOT[tone] || STATUS_DOT.neutral }} aria-hidden="true"></span>
      <span className="odc-typechip-name">{label}</span>
      {detail ? <span className="odc-typechip-group">{detail}</span> : null}
    </span>
  );
});

// ---- Effective import file-size limits (MB), by surface ----
// The four import dialogs pre-validate a file against the limit that is ACTUALLY
// in force, never a stale compile-time constant. Mirrors the client's
// IReferenceDataCache pattern: SystemSettings publishes the effective size caps
// to window.__odysseyImportLimits on a successful save (the invalidate-and-
// refresh step), and the dialogs read them live at render / validate time.
// Falls back to the shipped seed default per surface when nothing is published.
const IMPORT_LIMIT_MB_DEFAULTS = { contacts: 500, calendar: 5, tasks: 5, journal: 5 };
const getImportLimitMb = (surface) => {
  const live = (typeof window !== 'undefined' && window.__odysseyImportLimits) || null;
  const v = live && live[surface];
  return (v == null || Number.isNaN(v)) ? IMPORT_LIMIT_MB_DEFAULTS[surface] : v;
};

Object.assign(window, {
  IMPORT_LIMIT_MB_DEFAULTS, getImportLimitMb,
  MIcon, Button, IconButton, Card, CardBody, CardHeader, Modal,
  Field, SearchField, Select, AmountField, MoneyField, CurrencySelect, NoteField, NumberField, FieldShell, FormRow, DateField, DateRangePicker, Chip, Alert, SeverityIcon, Avatar, TONE_MAP, Switch, Checkbox, StatTile, EmptyState, BrandMark,
  ContactTypeSelect,
  SettingRow,
  SettingField,
  CapacityField,
  TextInputField,
  ErrorSummary,
  AccountFileTypeSelect, AccountFileTypeMultiSelect,
  TransactionFileTypeSelect, TransactionFileTypeMultiSelect,
  TaxStatementFileTypeSelect, TaxStatementFileTypeMultiSelect,
  InsurancePolicyTypeSelect, PolicyFileTypeSelect, PolicyFileTypeMultiSelect, CoverageStatusChip, Combobox, MatchIndicator,
  BillingIntervalSelect, BillingIntervalMultiSelect, BillingIntervalChip, SubscriptionStatusChip,
  SegmentedControl,
  ODC_TONE, odcTypeRows, odcStatusRows,
  ContractTypeSelect,
  BudgetCategoryTypeSelect,
  AddRow,
  ActionMenu, SortHeader, MetaTile, InfoTile, RecordCard, InfoTileGrid, SectionDivider, RecordTable, SortSelect, SortHelpers, Collapsible, LineChart, Delta, ProblemAlert,
  BreakdownTile,
  FileUpload,
  Pager, PageSizeSelect, InlinePager, InfiniteList,
  TagMultiSelect, TagChips, AccountSmartTagsSection,
  TodoStatusChip, JournalPhotoGallery, TaskBoard, TODO_STATUSES,
  CalendarGrid, TimeField, CoordinateField, StepperField, ColorSwatchSelect, CALENDAR_SWATCHES, swatchFor, RevealPanel,
  PasswordRules, PASSWORD_POLICY, PasswordChangeForm,
  CustodianChip, CustodianSelect, AccountTypeChip, AccountStatusChip,
  usePopover,
});

// ContactChip: the DS read display of a linked/tagged contact (type
// glyph + name; archived + "Unavailable" states). Assigned to window directly
// (not via a top-level const) so it can't collide with Journal.jsx's local
// `ContactChip` adapter in the shared global lexical scope. Used by the
// Journal links row and the Photos "People" (Contacts of type Person).
window.ContactChip = DS.ContactChip;
