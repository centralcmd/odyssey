using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a contract (issue #174 §7), optionally
/// recording the document's validity metadata (issue #146). All four validity properties are
/// optional; a body omitting one stores <c>null</c> for it, so a pre-#146 payload keeps working.
/// </summary>
public sealed record AttachContractFileRequest
{
    [Required]
    public required Guid FileMetadataId { get; set; }

    [EnumDataType(typeof(ContractFileType))]
    public ContractFileType FileType { get; set; } = ContractFileType.Other;

    /// <summary>When the document takes effect (e.g. agreement start date). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. agreement end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id (e.g. bank, insurer). Optional.</summary>
    public Guid? IssuedBy { get; set; }
}
