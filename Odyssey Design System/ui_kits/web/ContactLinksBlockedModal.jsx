/* ContactLinksBlockedModal — the blocked contact delete (409) and its detach path.
   ----------------------------------------------------------------------------
   A contact named as an INSURER, an INSURED CONTACT or a BENEFICIARY on any
   policy — or, since the contract party-role matrix, as a BENEFICIARY on any
   CONTRACT — cannot be deleted by the ordinary route: the delete is refused,
   because a beneficiary designation vanishing silently on contact deletion
   would lose it without trace. RESTRICT alone is not an acceptable answer for a
   person exercising erasure, so this dialog also carries the supported release
   valve: detach every blocking link and delete the contact in ONE transaction.

   Two things are claim-conditional, per BLOCKER CLASS, and the dialog is honest
   about both:
     • The payload. Policy names need `insurance.read`; contract names need
       `contracts.read`. Without the claim the caller still gets the COUNT —
       enough to act, since the detach valve never asks you to name them.
     • The action. Detaching needs `contacts.delete` plus `insurance.update`
       for policy rows and `contracts.update` for contract rows — demanded only
       for the classes actually present, so a caller whose contact has no
       contract links is not de-authorized by a rule about contracts. A caller
       missing a claim for a class present is refused (403) with the reason
       stated, never a silent downgrade to the refused delete.

   The result step reports what the request destroyed (per-kind counts + the
   affected records), because links removed wholesale in one request is the one
   operation with a blast radius the ordinary edit does not have. */

const ContactLinksBlockedModal = ({
  contact,
  blocking = [],
  contractBeneficiaries = null,
  canReadInsurance = true,
  canUpdateInsurance = true,
  canReadContracts = true,
  canUpdateContracts = true,
  onClose,
  onDetachAndDelete,
}) => {
  const { useState } = React;
  const [result, setResult] = useState(null);

  const KIND_META = {
    'Insurer': { icon: 'groups', note: 'carries cover on the policy' },
    'Insured contact': { icon: 'person', note: 'insured under the policy' },
    'Beneficiary': { icon: 'volunteer_activism', note: 'receives on the policy' },
  };
  // Per-kind counts — link ROWS, the same thing every other surface counts.
  const byKind = {};
  for (const b of blocking) for (const k of b.kinds) byKind[k] = (byKind[k] || 0) + 1;
  const kinds = Object.keys(KIND_META).filter(k => byKind[k]);
  const insLinks = Object.values(byKind).reduce((a, b) => a + b, 0);

  /* The contract half of the payload. `count` is always present; `contracts`
     carries names only for a `contracts.read` holder — an empty array with a
     non-zero count is a permission boundary, not an empty result. */
  const conCount = contractBeneficiaries ? contractBeneficiaries.count : 0;
  const conRows = (contractBeneficiaries && contractBeneficiaries.contracts) || [];
  const totalLinks = insLinks + conCount;
  // Claims are demanded per class PRESENT, which is also what makes the button
  // available: an insurance-only blocker never asks for contracts.update.
  const needsInsUpdate = insLinks > 0;
  const needsConUpdate = conCount > 0;
  const missing = [];
  if (needsInsUpdate && !canUpdateInsurance) missing.push('edit insurance policies');
  if (needsConUpdate && !canUpdateContracts) missing.push('edit contracts');
  const canDetach = missing.length === 0;

  const detach = () => {
    setResult({ byKind, policies: blocking, insLinks, conCount, conRows, totalLinks });
    if (onDetachAndDelete) onDetachAndDelete(contact.id);
  };

  const ContractRows = () => (
    <div className="cpl-policies">
      {conRows.map(c => (
        <div className="cpl-policy" key={c.contractId}>
          <MIcon name="handshake" size={16} />
          <span className="cpl-policy-name">{c.contractName}</span>
          <span className="cpl-policy-kinds">Beneficiary</span>
        </div>
      ))}
    </div>
  );

  if (result) {
    return (
      <Modal
        title="Contact deleted"
        subtitle="The links and the contact were removed in one transaction."
        icon="link_off"
        onClose={onClose}
        footer={<Button variant="filled" color="primary" icon="check" onClick={onClose}>Done</Button>}>
        <div className="alert info compact">
          <SeverityIcon severity="info" size={18} className="alert-icon" />
          <div className="alert-body">
            <strong>{result.totalLinks} link{result.totalLinks === 1 ? '' : 's'} detached</strong>
            {result.insLinks ? <React.Fragment> across {result.policies.length} polic{result.policies.length === 1 ? 'y' : 'ies'}</React.Fragment> : null}
            {result.insLinks && result.conCount ? ' and' : null}
            {result.conCount ? <React.Fragment> {result.conCount} contract{result.conCount === 1 ? '' : 's'}</React.Fragment> : null}
            , then the contact was deleted.
          </div>
        </div>
        <ul className="cpl-kinds">
          {kinds.map(k => (
            <li key={k}>
              <MIcon name={KIND_META[k].icon} size={17} />
              <span className="cpl-kind-name">{k}</span>
              <span className="cpl-kind-count">{byKind[k]}</span>
            </li>
          ))}
          {conCount ? (
            <li>
              <MIcon name="handshake" size={17} />
              <span className="cpl-kind-name">Contract beneficiary</span>
              <span className="cpl-kind-count">{conCount}</span>
            </li>
          ) : null}
        </ul>
        {canReadInsurance && result.policies.length ? (
          <div className="cpl-policies">
            {result.policies.map(b => (
              <div className="cpl-policy" key={b.policyId}>
                <MIcon name="shield" size={16} />
                <span className="cpl-policy-name">{b.policyName}</span>
                <span className="cpl-policy-kinds">{b.kinds.join(' · ')}</span>
              </div>
            ))}
          </div>
        ) : null}
        {canReadContracts && conRows.length ? <ContractRows /> : null}
        <p className="cpl-note">The policies and contracts themselves are untouched — each stands with one fewer party.</p>
      </Modal>
    );
  }

  return (
    <Modal
      title="Unable to delete this contact"
      subtitle={insLinks && conCount
        ? 'It is named on insurance policies and on contracts. Detach those links to delete it.'
        : conCount
          ? 'It is named as a beneficiary on contracts. Detach those links to delete it.'
          : 'It is named on insurance policies. Detach those links to delete it.'}
      icon="block"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          {canDetach ? (
            <Button variant="danger" icon="link_off" onClick={detach}>
              Detach links &amp; delete
            </Button>
          ) : null}
        </React.Fragment>
      }>
      <div className="alert error compact">
        <SeverityIcon severity="error" size={18} className="alert-icon" />
        <div className="alert-body">
          <strong>{contact.name}</strong> holds {totalLinks} link{totalLinks === 1 ? '' : 's'} that must be detached first.
        </div>
      </div>

      {/* Which KINDS block, always — meaning never rides on a glyph alone, and
          the counts are the part no claim withholds. */}
      <ul className="cpl-kinds">
        {kinds.map(k => (
          <li key={k}>
            <MIcon name={KIND_META[k].icon} size={17} />
            <span className="cpl-kind-name">{k}</span>
            <span className="cpl-kind-note">{KIND_META[k].note}</span>
            <span className="cpl-kind-count">{byKind[k]}</span>
          </li>
        ))}
        {conCount ? (
          <li>
            <MIcon name="handshake" size={17} />
            <span className="cpl-kind-name">Contract beneficiary</span>
            <span className="cpl-kind-note">receives under the agreement</span>
            <span className="cpl-kind-count">{conCount}</span>
          </li>
        ) : null}
      </ul>

      {insLinks ? (
        canReadInsurance ? (
          <React.Fragment>
            <SectionDivider label="Blocking policies" meta={`${blocking.length} record${blocking.length === 1 ? '' : 's'}`} />
            <div className="cpl-policies">
              {blocking.map(b => (
                <div className="cpl-policy" key={b.policyId}>
                  <MIcon name="shield" size={16} />
                  <span className="cpl-policy-name">{b.policyName}</span>
                  <span className="cpl-policy-kinds">{b.kinds.join(' · ')}</span>
                </div>
              ))}
            </div>
          </React.Fragment>
        ) : (
          /* No insurance.read: kinds and counts only — no policy name, no id. */
          <p className="cpl-note">Which policies these are is not shown, because you do not have access to insurance records.</p>
        )
      ) : null}

      {conCount ? (
        canReadContracts ? (
          <React.Fragment>
            <SectionDivider label="Blocking contracts" meta={`${conCount} record${conCount === 1 ? '' : 's'}`} />
            <ContractRows />
          </React.Fragment>
        ) : (
          /* No contracts.read: the count still says how many must be detached,
             and the valve never asks the caller to name them. */
          <p className="cpl-note">Which contracts these are is not shown, because you do not have access to contract records. The count is what the detach needs.</p>
        )
      ) : null}

      {canDetach ? (
        <p className="cpl-note">
          <strong>Detach links &amp; delete</strong> removes all {totalLinks} link{totalLinks === 1 ? '' : 's'} and the contact in one transaction — either all of it happens, or none of it does. The records survive with one fewer party. This cannot be undone.
        </p>
      ) : (
        <div className="alert warning compact">
          <SeverityIcon severity="warning" size={18} className="alert-icon" />
          <div className="alert-body">Detaching needs permission to {missing.join(' and ')}, which you do not have. Ask someone who does, then delete the contact.</div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ContactLinksBlockedModal });
