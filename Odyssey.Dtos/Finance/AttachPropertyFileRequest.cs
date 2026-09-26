using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a property (issue #210 §5.1), optionally
/// recording the document's validity metadata. All four validity properties are
/// optional; a body omitting one stores <c>null</c> for it. The owning property comes from the route,
/// never the body, and every relationship is a scalar id — no nested object can be over-posted.
/// </summary>
public sealed record AttachPropertyFileRequest
{
    [Required]
    public required Guid FileMetadataId { get; set; }

    [EnumDataType(typeof(PropertyFileType))]
    public PropertyFileType FileType { get; set; } = PropertyFileType.Other;

    /// <summary>When the document takes effect (e.g. a warranty's start). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. insurance certificate, warranty). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id (e.g. land registry, insurer). Optional.</summary>
    public Guid? IssuedBy { get; set; }
}
