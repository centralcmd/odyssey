namespace Odyssey.Dtos.Finance;

public sealed record AttachTransactionFileRequest(
    Guid FileId,
    TransactionFileType Type = TransactionFileType.Other
);
