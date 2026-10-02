using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>Attaches an already-uploaded file (referenced by id) to a tax statement.</summary>
public sealed record AttachTaxStatementFileRequest
{
    [Required]
    public required Guid FileId { get; set; }

    [EnumDataType(typeof(TaxStatementFileType))]
    public TaxStatementFileType FileType { get; set; } = TaxStatementFileType.Other;
}
