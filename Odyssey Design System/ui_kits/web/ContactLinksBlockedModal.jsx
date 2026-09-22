/* ContactLinksBlockedModal — the blocked contact delete (409) and its detach path.
   ----------------------------------------------------------------------------
   A contact named as a BENEFICIARY on any CONTRACT cannot be deleted by the
   ordinary route: the delete is refused, because a beneficiary designation
   vanishing silently on contact deletion would lose it without trace. RESTRICT
   alone is not an acceptable answer for a person exercising erasure, so this
   dialog also carries the supported release valve: detach every blocking link
   and delete the contact in ONE transaction.

   Two things are claim-conditional, and the dialog is honest about both:
     • The payload. Contract names need `contracts.read`. Without the claim the
       caller still gets the COUNT — enough to act, since the detach valve never
       asks you to name them.
     • The action. Detaching needs `contacts.delete` plus `contracts.update`.
       A caller missing it is refused (403) with the reason stated, never a
       silent downgrade to the refused delete.

   The result step reports what the request destroyed (count + the affected
   records), because links removed wholesale in one request is the one operation
   with a blast radius the ordinary edit does not have. */

const ContactLinksBlockedModal = ({
  contact,
  contractBeneficiaries = null,
  canReadContracts = true,
  canUpdateContracts = true,
  onClose,
  onDetachAndDelete,
}) => {
  const { useState } = React;
  const [result, setResult] = useState(null);

  /* `count` is always present; `contracts` carries names only for a
     `contracts.read` holder — an empty array with a non-zero count is a
     permission boundary, not an empty result. */
  const conCount = contractBeneficiaries ? contractBeneficiaries.count : 0;
  const conRows = (contractBeneficiaries && contractBeneficiaries.contracts) || [];
  const canDetach = conCount === 0 || canUpdateContracts;

  const detach = () => {
    setResult({ conCount, conRows });
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
            <strong>{result.conCount} link{result.conCount === 1 ? '' : 's'} detached</strong> across {result.conCount} contract{result.conCount === 1 ? '' : 's'}, then the contact was deleted.
          </div>
        </div>
        <ul className="cpl-kinds">
          <li>
            <MIcon name="handshake" size={17} />
            <span className="cpl-kind-name">Contract beneficiary</span>
            <span className="cpl-kind-count">{result.conCount}</span>
          </li>
        </ul>
        {canReadContracts && conRows.length ? <ContractRows /> : null}
        <p className="cpl-note">The contracts themselves are untouched — each stands with one fewer party.</p>
      </Modal>
    );
  }

  return (
    <Modal
      title="Unable to delete this contact"
      subtitle="It is named as a beneficiary on contracts. Detach those links to delete it."
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
          <strong>{contact.name}</strong> holds {conCount} link{conCount === 1 ? '' : 's'} that must be detached first.
        </div>
      </div>

      {/* Which KIND blocks, always — meaning never rides on a glyph alone, and
          the count is the part no claim withholds. */}
      <ul className="cpl-kinds">
        <li>
          <MIcon name="handshake" size={17} />
          <span className="cpl-kind-name">Contract beneficiary</span>
          <span className="cpl-kind-note">receives under the agreement</span>
          <span className="cpl-kind-count">{conCount}</span>
        </li>
      </ul>

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
          <strong>Detach links &amp; delete</strong> removes all {conCount} link{conCount === 1 ? '' : 's'} and the contact in one transaction — either all of it happens, or none of it does. The contracts survive with one fewer party. This cannot be undone.
        </p>
      ) : (
        <div className="alert warning compact">
          <SeverityIcon severity="warning" size={18} className="alert-icon" />
          <div className="alert-body">Detaching needs permission to edit contracts, which you do not have. Ask someone who does, then delete the contact.</div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ContactLinksBlockedModal });
