/* ContractTerms — the "Terms" section inside an expanded contract record
   (Contracts → contract detail), between Parties and Documents.

   The contract half of the Term feature. A term is the SAME row, in the same
   table, under the same rules as an account's term — only the owner differs
   (exactly one of AccountId / ContractId). So this section reuses the account
   surface's pieces verbatim: CurrentTermsSummary (the GET …/terms/current view)
   and TermHistory (the GET …/terms list, grouped, editable per row). It reuses
   the step chart too, but not as a hero and not as a rate: an agreement has
   several named charges and no single line that stands for it, so the chart is
   a CHOOSER above the history — one series at a time, opening on the one whose
   latest entry is the most recent, with the value axis in that series' own
   unit. The section still leads with the values in force.

   Three things here are contract-specific, and each is drawn, not assumed:
     • Kind eligibility — Fee and InterestRate on every ContractType;
       ExpectedReturn is refused. Handled in the dialog.
     • Archiving does NOT lock terms — an archived contract still records and
       edits them; archival hides the contract, it does not freeze it.
     • A per-contract cap (ContractMaxTermsPerContract, default 500): at the
       cap, creating is refused (422) while editing an existing row is not.

   Props:
     contract   — the contract record (owner; drives eligibility + the guards)
     terms      — the term rows (owned by the parent, as parties/files are)
     cap        — ContractMaxTermsPerContract
     onNew / onEdit / onDelete — write handlers; the guards decide if they fire */

const CTRM_H = window.OdysseyHelpers;
const CTRM_D = window.OdysseyData;

/* A contract-shaped owner for the shared term helpers: it is what makes an
   unlabelled InterestRate read "Interest rate" here and "Interest charged" on a
   loan, from one helper rather than two copies of the wording. */
const conTermOwner = (contract) => ({ ...contract, ownerKind: 'contract' });

/* =============================================================
   Price history chart — one SERIES at a time
   =============================================================
   The account surface leads with a rate chart because an account has one rate
   and the question is where it went. A contract is the other shape: several
   named charges, each with its own history, and no single line that stands for
   the agreement. So the chart is a chooser — and a MULTI-select one, because
   the second question after "what did this change to" is "how does it compare
   to the others". It opens on the series whose LATEST entry is the most
   recent, because that is the change the reader came to look at.

   Comparing is only honest between charges measured the same way, so the
   chooser carries the constraint: same unit, and for money the same currency.
   A pick that cannot share the axis switches to itself rather than stacking,
   which keeps every series one click away and the axis never lying. Anything
   else is selectable, including a charge that has never changed \u2014 flat at 0%
   beside a rising line is an answer to "which of these is moving". Comparing
   plots INDEXED change \u2014 each charge's move from its own first entry \u2014 since
   a 2,250 rent and a 95 parking space share no useful absolute axis; the
   legend keeps the real money beside each line.

   Amounts, not rates, are the common case here, so the value axis is labelled
   in the series' own unit and the currency is stated once in the header rather
   than on every tick. The figure takes the direction's colour \u2014 money out is
   money out whether it rose or fell \u2014 which frees the delta to be neutral grey
   rather than income-green for a rent increase.

   The plot itself is the DS `StepChart` — LineChart's card, head and
   typography (the dashboard's chart vocabulary, at the dashboard's scale) with
   the two things a term history needs and a trend chart refuses: a real TIME
   axis, so eight months of hold and two changes in a quarter do not read as
   three equal steps, and a TODAY marker the line holds solid up to and dashes
   past, so a scheduled increase is visibly not yet true. */
const conTermSeriesList = (terms) => {
  const today = trmToday();
  const by = {};
  for (const t of terms) {
    const key = trmKey(t);
    const s = by[key] || (by[key] = { key, labelKey: t.labelKey || CTRM_H.termLabelKey(t.label) || null, latest: t, inForce: null, earliest: t, count: 0 });
    s.count += 1;
    if (t.effectiveFrom > s.latest.effectiveFrom) s.latest = t;
    if (t.effectiveFrom < s.earliest.effectiveFrom) s.earliest = t;
    /* `latest` orders the list — "most recently changed" legitimately counts a
       scheduled entry. `inForce` is what every READOUT shows: the newest entry
       that has actually taken effect. A series still entirely in the future
       falls back to its earliest, which is the only figure it has. */
    if (t.effectiveFrom <= today && (!s.inForce || t.effectiveFrom > s.inForce.effectiveFrom)) s.inForce = t;
  }
  return Object.values(by)
    .map(s => ({ ...s, inForce: s.inForce || s.earliest }))
    .sort((a, b) => (a.latest.effectiveFrom < b.latest.effectiveFrom ? 1 : a.latest.effectiveFrom > b.latest.effectiveFrom ? -1 : 0));
};

const conAxisFmt = (latest) => (latest.unit === 'Percentage'
  ? (v) => (v < 0 ? '−' : '') + CTRM_H.pctStr(Math.abs(v))
  // No currency code on the tick: it is stated once, in the value. Whole units
  // above 10 — a rent axis reading "2,438.75" is noise.
  : (v) => (v < 0 ? '−' : '') + Math.abs(v).toLocaleString('en-US', { maximumFractionDigits: Math.abs(v) >= 10 ? 0 : 2 }));

/* Two series may share an axis only if they are the same UNIT and, for money,
   the same CURRENCY — handed to the DS chart as each series' `group`. */
const conCompatKey = (t) => (t.unit === 'Percentage' ? 'pct' : `amt:${t.currency || CTRM_H.defaultCurrency()}`);

/* The card itself is the DS `TermHistoryChart`: this only resolves contract
   terms into its plain series. Everything each series states comes from the
   entry IN FORCE, so the picker, the legend and the tiles above agree. */
const ContractTermChart = ({ terms, owner }) => {
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const DSTermHistoryChart = DS.TermHistoryChart;
  if (!DSTermHistoryChart) return null;
  const series = conTermSeriesList(terms).map((x) => {
    const t = x.inForce;
    const dir = CTRM_H.termDirectionApplies(t, owner) ? CTRM_H.termDirectionInfo(t) : null;
    const pct = t.unit === 'Percentage';
    return {
      key: x.key,
      label: CTRM_H.termDisplayName(t, owner),
      value: CTRM_H.fmtTermValueFor(t, owner),
      tone: dir ? { label: dir.label, color: dir.color } : undefined,
      color: dir ? dir.color : trmKindInfo(t).color,
      group: conCompatKey(t),
      points: trmSeriesFromList(terms, x.key).map(p => ({ id: p.id, date: p.date, value: p.value })),
      format: (v) => (pct ? CTRM_H.pctStr(v) : CTRM_H.money(v, t.currency || CTRM_H.defaultCurrency())),
      axisFormat: conAxisFmt(t),
    };
  });
  return <DSTermHistoryChart className="trm-seriesplot" series={series} />;
};

const ContractTermsNotice = ({ block }) => (
  <div className="odc-recordsection-notice">
    <span className="material-icons" aria-hidden="true">production_quantity_limits</span>
    <div className="odc-recordsection-notice-body">{block.text}</div>
  </div>
);

const ContractTerms = ({ contract, terms = [], cap, onNew, onEdit, onDelete }) => {
  const { useMemo } = React;
  const owner = useMemo(() => conTermOwner(contract), [contract]);
  const current = useMemo(() => trmCurrentFromList(terms), [terms]);
  const currentIds = useMemo(() => new Set(current.map(t => t.id)), [current]);
  const block = CTRM_H.conTermWriteBlock(contract, terms.length, cap);
  const limit = cap != null ? cap : CTRM_D.CONTRACT_MAX_TERMS_PER_CONTRACT;
  /* The two sides of the in-force set. A contract is not one-directional — an
     employment agreement pays a salary IN and deducts dues OUT — so the
     section says how many of each rather than one count of "values". */
  const incoming = current.filter(t => CTRM_H.termDirectionApplies(t, owner) && CTRM_H.termIsIncoming(t));

  if (terms.length === 0) {
    return (
      <div className="con-section">
        <SectionDivider label="Terms" meta="0 entries" />
        <EmptyLine>
          No terms yet — record what this agreement costs: a rent, a service fee, an interest rate. Each keeps its own dated history.
          {block ? <div className="con-trm-inline-block">{block.text}</div> : null}
        </EmptyLine>
      </div>
    );
  }

  /* Two sections, not one with sub-headings: what the contract costs TODAY and
     how it got there are separate questions, and they sit at the same level as
     Parties and Documents. The in-force values render as the record's own
     InfoTiles — the same treatment the account record gives its Current
     section — so a term tile reads as a sibling of the detail tiles above it. */
  return (
    <React.Fragment>
      <div className="con-section">
        <SectionDivider label="Current terms" meta={current.length ? `${current.length} ${current.length === 1 ? 'value' : 'values'} in force${incoming.length ? ` · ${incoming.length} incoming, ${current.length - incoming.length} outgoing` : ''} · ${CTRM_H.dateLong(trmToday())}` : 'none in force'} />
        {block ? <ContractTermsNotice block={block} /> : null}
        {current.length ? (
          <InfoTileGrid>
            {current.map(t => {
              const info = trmKindInfo(t);
              // The cadence is what separates a 2,150 USD monthly rent from a
              // 2,150 USD one-off, so it rides in the foot beside the date.
              const period = CTRM_H.cadenceTextFor(t);
              const labelled = !!CTRM_H.termLabelNormalize(t.label);
              // Every contract fee states its direction, both ways: the fact
              // belongs to the term, not to the set of terms around it.
              const tagged = CTRM_H.termDirectionApplies(t, owner);
              const dir = CTRM_H.termDirectionInfo(t);
              /* No chip: the FIGURE carries the direction in its own finance
                 hue, and the word sits on its own line under the tile's title —
                 the slot a party tile gives its from–to dates. There is room
                 there for the whole word, so it reads "Incoming", not "in". */
              const value = tagged ? dir.color : info.color;
              return (
                <InfoTile key={trmKey(t)} icon={info.icon} iconColor={value}
                  iconSoft={tagged ? (dir.soft || `color-mix(in srgb, ${dir.color} 16%, transparent)`) : info.soft}
                  className={tagged ? 'trm-dir-tile' : undefined}
                  label={tagged
                    ? <React.Fragment>
                        <span className="trm-dir-name"><span>{CTRM_H.termDisplayName(t, owner)}</span></span>
                        <span className="trm-dir-sub">{dir.label}</span>
                      </React.Fragment>
                    : CTRM_H.termDisplayName(t, owner)}
                  value={<span style={{ color: value }}>{CTRM_H.fmtTermValueFor(t, owner)}</span>}
                  foot={`since ${CTRM_H.dateLong(t.effectiveFrom)}${period ? ` · ${period}` : ''}`} />
              );
            })}
          </InfoTileGrid>
        ) : (
          <EmptyLine>Nothing in force today — every entry on this contract is scheduled for a later date.</EmptyLine>
        )}
      </div>

      <div className="con-section">
        <SectionDivider label="Term history" meta={`${terms.length} ${terms.length === 1 ? 'entry' : 'entries'}${terms.length >= limit ? ` · limit ${limit}` : ''}`} />
        <ContractTermChart terms={terms} owner={owner} />
        <TermHistory
          terms={terms}
          currentIds={currentIds}
          historyStyle="table"
          account={owner}
          onEdit={onEdit}
          onDelete={onDelete}
        />
      </div>
    </React.Fragment>
  );
};

Object.assign(window, { ContractTerms, ContractTermChart, conTermOwner });
