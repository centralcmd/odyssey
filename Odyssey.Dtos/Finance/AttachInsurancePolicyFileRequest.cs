using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a renewal period — the only place an
/// insurance document lives since issue #26. The name is kept because it is the surviving endpoint's
/// body and was always shared between the two attach routes.
/// </summary>
public sealed record AttachInsurancePolicyFileRequest
{
    [Required]
    public required Guid FileId { get; set; }

    [EnumDataType(typeof(PolicyFileType))]
    public PolicyFileType FileType { get; set; } = PolicyFileType.Other;

    public DateTime? EffectiveDate { get; set; }
}
