/* ContractTerms — the "Terms" section inside an expanded contract record
   (Contracts → contract detail), between Parties and Documents.

   The contract half of the Term feature. A term is the SAME row, in the same
   table, under the same rules as an account's term — only the owner differs
   (exactly one of AccountId / ContractId). So this section reuses the account
   surface's pieces verbatim: CurrentTermsSummary (the GET …/terms/current view)
   and TermHistory (the GET …/terms list, grouped, editable per row). What it
   does NOT reuse is the hero rate chart: an agreement's price is usually an
   amount, not a rate, so the section leads with the values in force.

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
        <SectionDivider label="Current terms" meta={current.length ? `${current.length} ${current.length === 1 ? 'value' : 'values'} in force · ${CTRM_H.dateLong(trmToday())}` : 'none in force'} />
        {block ? <ContractTermsNotice block={block} /> : null}
        {current.length ? (
          <InfoTileGrid>
            {current.map(t => {
              const info = trmKindInfo(t.kind);
              // The cadence is what separates a $2,150 monthly rent from a
              // $2,150 one-off, so it rides in the foot beside the date.
              const period = CTRM_H.cadenceTextFor(t);
              const labelled = !!CTRM_H.termLabelNormalize(t.label);
              return (
                <InfoTile key={trmKey(t)} icon={info.icon} iconColor={info.color} iconSoft={info.soft}
                  label={CTRM_H.termDisplayName(t, owner)}
                  value={<span style={{ color: info.color }}>{CTRM_H.fmtTermValueFor(t, owner)}</span>}
                  foot={`${labelled ? `${CTRM_H.termKindLabelFor(t, owner)} · ` : ''}since ${CTRM_H.dateLong(t.effectiveFrom)}${period ? ` · ${period}` : ''}`} />
              );
            })}
          </InfoTileGrid>
        ) : (
          <EmptyLine>Nothing in force today — every entry on this contract is scheduled for a later date.</EmptyLine>
        )}
      </div>

      <div className="con-section">
        <SectionDivider label="Term history" meta={`${terms.length} ${terms.length === 1 ? 'entry' : 'entries'}${terms.length >= limit ? ` · limit ${limit}` : ''}`} />
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

Object.assign(window, { ContractTerms, conTermOwner });
