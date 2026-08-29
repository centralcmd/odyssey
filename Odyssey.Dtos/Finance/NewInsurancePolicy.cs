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

    [Required]
    public required Guid InsurerId { get; set; }

    public Guid? InsuredAccountId { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }
}
