/* AddPropertyFileModal — attach documents to a property (*Property Documents —
   Backend, Draft v2* §3, §5.1) through the shared AttachDocumentsModal. A
   document is a FileMetadata in the one Files store, linked here by id. Every file
   carries a PropertyFileType and the optional validity fields. */

const AddPropertyFileModal = ({ property, attached = [], onClose, onAttach }) => {
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const noun = property.type === 'Vehicle' ? 'the registration, an inspection, the insurance certificate or a warranty' : 'the deed, the purchase agreement, a valuation or a warranty';
  const nowIso = () => new Date().toISOString();
  return (
    <AttachDocumentsModal
      subtitle={`Keep ${noun} with ${property.name}. Files stay in Files; attaching links them here.`}
      kinds={D.propertyFileTypes}
      guessKind={H.propGuessFileType}
      validity
      attachedIds={attached.map(a => a.fileMetadataId)}
      onClose={onClose}
      onSubmit={(items) => onAttach && onAttach(items.map((f, i) => ({
        id: `pf-new-${Date.now()}-${i}`, propertyId: property.id, fileMetadataId: f.fileMetadataId, kind: f.kind,
        attachedByUserId: 'u-owner', attachedByName: 'Owner Demo', attachedAtUtc: nowIso(),
        validFrom: f.validFrom, validTo: f.validTo, issuedAt: f.issuedAt, issuedBy: f.issuedBy,
      })))}
    />
  );
};

Object.assign(window, { AddPropertyFileModal });
