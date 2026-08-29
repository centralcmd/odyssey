namespace Odyssey.Dtos.Finance;

public sealed record ExistingInsurancePolicy
{
    public required Guid InsurancePolicyId { get; set; }

    public required string Name { get; set; }

    public string? PolicyNumber { get; set; }

    public InsurancePolicyType Type { get; set; }

    public required InsurerReference Insurer { get; set; }

    public InsuredAccountReference? InsuredAccount { get; set; }

    public string? Notes { get; set; }

    /// <summary>Derived, never stored (see issue #175 §5).</summary>
    public CoverageStatus CoverageStatus { get; set; }

    /// <summary>The renewal whose window contains today, or null when none does.</summary>
    public ExistingPolicyRenewal? CurrentRenewal { get; set; }

    public List<ExistingPolicyRenewal> Renewals { get; set; } = new();

    public List<ExistingInsurancePolicyFile> Files { get; set; } = new();

    public DateTime? Archived { get; set; }

    public required DateTime CreatedAtUtc { get; set; }
}
