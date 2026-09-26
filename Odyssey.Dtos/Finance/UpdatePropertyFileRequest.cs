using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Replaces an attached property document's type and validity metadata (issue #210 §5.3). The body is
/// a <b>full replacement</b>: an omitted date or issuer is <c>null</c> and <b>clears</b> the stored
/// value. <see cref="FileType"/> is the exception and may not be omitted.
/// </summary>
public sealed record UpdatePropertyFileRequest
{
    /// <summary>
    /// The document's type. Carries the C# <c>required</c> modifier for the reason
    /// <see cref="UpdateContractFileRequest.FileType"/> records: this verb is a full replacement with no
    /// "leave unchanged" value, and <c>[Required]</c> is a no-op on a non-nullable value type, so only
    /// System.Text.Json's required-member check can turn an omission into a <c>400</c> rather than a
    /// silent reset to <see cref="PropertyFileType.Other"/>.
    /// </summary>
    [EnumDataType(typeof(PropertyFileType))]
    public required PropertyFileType FileType { get; set; }

    /// <summary>When the document takes effect. Optional; null clears.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires. Optional; null clears.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional; null clears.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id. Optional; null clears.</summary>
    public Guid? IssuedBy { get; set; }
}
