using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateInsurancePolicy
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(128)]
    public string? PolicyNumber { get; set; }

    [EnumDataType(typeof(InsurancePolicyType))]
    public InsurancePolicyType Type { get; set; } = InsurancePolicyType.Other;

    /// <summary>
    /// The complete desired set of insurers. <b><c>null</c> leaves the collection unchanged;
    /// <c>[]</c> clears it</b> (issue #27 §6) — the house idiom, and what stops a partially-constructed
    /// body from silently wiping a beneficiary designation. Scalar ids only, at any depth.
    /// </summary>
    [MaxLength(InsuranceLinkLimits.MaxLinksPerPolicy)]
    public List<Guid>? InsurerIds { get; set; }

    /// <inheritdoc cref="InsurerIds" />
    [MaxLength(InsuranceLinkLimits.MaxLinksPerPolicy)]
    public List<Guid>? InsuredAccountIds { get; set; }

    /// <inheritdoc cref="InsurerIds" />
    [MaxLength(InsuranceLinkLimits.MaxLinksPerPolicy)]
    public List<Guid>? InsuredContactIds { get; set; }

    /// <inheritdoc cref="InsurerIds" />
    [MaxLength(InsuranceLinkLimits.MaxLinksPerPolicy)]
    public List<Guid>? BeneficiaryIds { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>
    /// Archive (soft-hide) the policy when true, or unarchive when false. Archiving keeps the policy
    /// and its history but drops it from the portfolio summary; deletion (<c>DELETE</c>) is permanent.
    /// </summary>
    public bool Archived { get; set; }
}
