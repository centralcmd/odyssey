using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingInsurancePolicy
{
    public required Guid InsurancePolicyId { get; set; }

    public required string Name { get; set; }

    public string? PolicyNumber { get; set; }

    public InsurancePolicyType Type { get; set; }

    /// <summary>
    /// The contacts carrying this cover. Optional and possibly empty (issue #27 Goal 2): a policy
    /// drafted before the insurer is known is a valid, healthy record. Ordered by resolved display
    /// name ascending, server-side; an unnamed member (archived / unresolvable) sorts last.
    /// </summary>
    public List<PolicyContactReference> Insurers { get; set; } = new();

    /// <summary>The accounts representing the insured assets.</summary>
    public List<InsuredAccountReference> InsuredAccounts { get; set; } = new();

    /// <summary>The people and organisations insured under this policy.</summary>
    public List<PolicyContactReference> InsuredContacts { get; set; } = new();

    /// <summary>Who receives on this policy — a person, or an organisation such as a trust or estate.</summary>
    public List<PolicyContactReference> Beneficiaries { get; set; } = new();

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
