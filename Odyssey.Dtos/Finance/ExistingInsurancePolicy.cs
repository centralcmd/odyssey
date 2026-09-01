using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// Premium accrued through the current period — every period starting on or before the current
    /// one ends — converted into <see cref="AccruedPremiumCurrencyCode"/>. Null when the policy has no
    /// current period, because there is then nothing to accrue "through".
    ///
    /// <para>
    /// Derived here rather than in each client because the conversion needs exchange rates, which
    /// only the server has. A period whose currency has no rate to the current one is <b>left out</b>
    /// rather than added at face value — a silently mixed-currency sum is worse than a smaller one —
    /// and <see cref="AccruedPremiumPeriods"/> counts what was actually summed, so the figure and its
    /// period count can never disagree.
    /// </para>
    /// </summary>
    public decimal? AccruedPremium { get; set; }

    /// <summary>The currency <see cref="AccruedPremium"/> is expressed in — the current period's.</summary>
    [StringLength(3)]
    public string? AccruedPremiumCurrencyCode { get; set; }

    /// <summary>How many periods <see cref="AccruedPremium"/> actually includes (unconvertible ones excluded).</summary>
    public int AccruedPremiumPeriods { get; set; }

    public DateTime? Archived { get; set; }

    public required DateTime CreatedAtUtc { get; set; }
}
