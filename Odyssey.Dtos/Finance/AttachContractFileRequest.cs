using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a contract (issue #174 §7).
/// </summary>
public sealed record AttachContractFileRequest
{
    [Required]
    public required Guid FileMetadataId { get; set; }

    [EnumDataType(typeof(ContractFileType))]
    public ContractFileType FileType { get; set; } = ContractFileType.Other;
}
