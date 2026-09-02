using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewInsurancePolicy
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(128)]
    public string? PolicyNumber { get; set; }

    [EnumDataType(typeof(InsurancePolicyType))]
    public InsurancePolicyType Type { get; set; } = InsurancePolicyType.Other;

    /// <summary>
    /// The contacts carrying this cover. Scalar ids only, at any depth — the mass-assignment invariant
    /// (issue #27 §10 #4): a policy write can never create, rename or otherwise mutate a linked record.
    /// Optional and possibly empty; <c>null</c> and <c>[]</c> mean the same thing on create.
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
}
