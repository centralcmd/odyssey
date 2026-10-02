using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a transaction. The file-type property is
/// named <c>Type</c>, not <c>FileType</c> as on the sibling requests: it is the wire name clients
/// already send, so it stays.
/// </summary>
public sealed record AttachTransactionFileRequest
{
    [Required]
    public required Guid FileId { get; set; }

    [EnumDataType(typeof(TransactionFileType))]
    public TransactionFileType Type { get; set; } = TransactionFileType.Other;
}
