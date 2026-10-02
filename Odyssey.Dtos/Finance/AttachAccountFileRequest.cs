using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to an account, optionally recording the
/// document's validity metadata. All four validity properties are optional; a body omitting one stores
/// <c>null</c> for it.
/// </summary>
public sealed record AttachAccountFileRequest
{
    [Required]
    public required Guid FileId { get; set; }

    [EnumDataType(typeof(AccountFileType))]
    public AccountFileType FileType { get; set; } = AccountFileType.Other;

    /// <summary>When the document takes effect. Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires. Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id. Optional.</summary>
    public Guid? IssuedBy { get; set; }
}
