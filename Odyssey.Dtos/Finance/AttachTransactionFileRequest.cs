using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record AttachTransactionFileRequest(
    Guid FileId,
    [EnumDataType(typeof(TransactionFileType))] TransactionFileType Type = TransactionFileType.Other
);
