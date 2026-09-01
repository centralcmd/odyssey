/* ContactLinksBlockedModal — the blocked contact delete (409) and its detach path.
   ----------------------------------------------------------------------------
   A contact named as an INSURER, an INSURED CONTACT or a BENEFICIARY on any
   policy cannot be deleted by the ordinary route: the delete is refused, because
   a beneficiary designation vanishing silently on contact deletion would lose it
   without trace. RESTRICT alone is not an acceptable answer for a person
   exercising erasure, so this dialog also carries the supported release valve:
   detach every insurance link and delete the contact in ONE request.

   Two things are claim-conditional, and the dialog is honest about both:
     • The payload. A caller holding `insurance.read` is told WHICH policies
       block the delete; a caller without it gets kinds and counts only — never a
       policy name or id.
     • The action. Detaching needs `contacts.delete` AND `insurance.update`,
       composed from existing claims. Without the second the button is
       unavailable with the reason stated, never a silent downgrade to the
       refused delete.

   The result step reports what the request destroyed (per-kind counts + the
   affected policies), because links removed wholesale in one request is the one
   operation with a blast radius the ordinary edit does not have. */

const ContactLinksBlockedModal = ({ contact, blocking, canReadInsurance = true, canUpdateInsurance = true, onClose, onDetachAndDelete }) => {
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
  const totalLinks = Object.values(byKind).reduce((a, b) => a + b, 0);

  const detach = () => {
    setResult({ byKind, policies: blocking, totalLinks });
    if (onDetachAndDelete) onDetachAndDelete(contact.id);
  };

  if (result) {
    return (
      <Modal
        title="Contact deleted"
        subtitle="The insurance links and the contact were removed in one transaction."
        icon="link_off"
        onClose={onClose}
        footer={<Button variant="filled" color="primary" icon="check" onClick={onClose}>Done</Button>}>
        <div className="alert info compact">
          <SeverityIcon severity="info" size={18} className="alert-icon" />
          <div className="alert-body">
            <strong>{result.totalLinks} link{result.totalLinks === 1 ? '' : 's'} detached</strong> across{' '}
            {result.policies.length} polic{result.policies.length === 1 ? 'y' : 'ies'}, then the contact was deleted.
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
        </ul>
        {canReadInsurance ? (
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
        <p className="cpl-note">The policies themselves are untouched — each stands with one fewer member.</p>
      </Modal>
    );
  }

  return (
    <Modal
      title="Unable to delete this contact"
      subtitle="It is named on insurance policies. Detach those links to delete it."
      icon="block"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          {canUpdateInsurance ? (
            <Button variant="danger" icon="link_off" onClick={detach}>
              Detach links & delete
            </Button>
          ) : null}
        </React.Fragment>
      }>
      <div className="alert error compact">
        <SeverityIcon severity="error" size={18} className="alert-icon" />
        <div className="alert-body">
          <strong>{contact.name}</strong> holds {totalLinks} insurance link{totalLinks === 1 ? '' : 's'}
          {canReadInsurance ? <React.Fragment> across {blocking.length} polic{blocking.length === 1 ? 'y' : 'ies'}</React.Fragment> : null}.
        </div>
      </div>

      {/* Which KINDS block, always — meaning never rides on a glyph alone. */}
      <ul className="cpl-kinds">
        {kinds.map(k => (
          <li key={k}>
            <MIcon name={KIND_META[k].icon} size={17} />
            <span className="cpl-kind-name">{k}</span>
            <span className="cpl-kind-note">{KIND_META[k].note}</span>
            <span className="cpl-kind-count">{byKind[k]}</span>
          </li>
        ))}
      </ul>

      {canReadInsurance ? (
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
        <p className="cpl-note">Which policies these are is not shown, because you do not have access to insurance records. Ask someone who can edit them to detach the links.</p>
      )}

      {canUpdateInsurance ? (
        <p className="cpl-note">
          <strong>Detach links &amp; delete</strong> removes all {totalLinks} link{totalLinks === 1 ? '' : 's'} and the contact in one transaction — either all of it happens, or none of it does. The policies survive with one fewer member. This cannot be undone.
        </p>
      ) : (
        <div className="alert warning compact">
          <SeverityIcon severity="warning" size={18} className="alert-icon" />
          <div className="alert-body">Detaching needs permission to edit insurance policies, which you do not have. Ask someone who does, then delete the contact.</div>
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { ContactLinksBlockedModal });
