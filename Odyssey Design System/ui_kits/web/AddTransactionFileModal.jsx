/* AddTransactionFileModal — attach documents to a transaction through the
   shared AttachDocumentsModal. TransactionFileType per file; no validity.
   Output rows use the AccountFile shape the transaction's FilesTable reads. */

const AddTransactionFileModal = ({ transaction, onClose, onAttach }) => {
  const H = window.OdysseyHelpers;
  const today = new Date().toISOString().slice(0, 10);
  return (
    <AttachDocumentsModal
      subtitle={`Keep the receipt, invoice or payment confirmation with ${transaction.desc}. Files stay in Files; attaching links them here.`}
      kinds={window.OdysseyData.transactionFileTypes}
      guessKind={(name) => window.afmGuessKind(name, 'transaction')}
      attachedIds={H.filesForTransaction(transaction).map(f => f.fileMetadataId)}
      onClose={onClose}
      onSubmit={(items) => onAttach && onAttach(items.map((f, i) => ({
        id: `tf-${Date.now()}-${i}`, fileMetadataId: f.fileMetadataId, name: f.name, kind: f.kind, size: f.size, uploaded: today,
      })))}
    />
  );
};

Object.assign(window, { AddTransactionFileModal });
