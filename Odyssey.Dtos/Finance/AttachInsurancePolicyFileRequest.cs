using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Attaches an already-uploaded file (referenced by id) to a policy or a renewal. Used by both the
/// policy-level and renewal-level attach endpoints (issue #175 §7).
/// </summary>
public sealed record AttachInsurancePolicyFileRequest
{
    [Required]
    public required Guid FileId { get; set; }

    [EnumDataType(typeof(PolicyFileType))]
    public PolicyFileType FileType { get; set; } = PolicyFileType.Other;

    public DateTime? EffectiveDate { get; set; }
}
