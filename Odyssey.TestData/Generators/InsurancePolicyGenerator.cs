using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic insurance policies and their renewal history (issue #175). Anchored to
/// <see cref="DemoDataDefaults.AnchorDate"/> so the derived coverage status of each policy is stable
/// and the set deliberately exercises every status the read/summary endpoints can report:
/// Active, ExpiringSoon, Lapsed, Upcoming and NoCoverage. Currencies stay within the demo FX matrix
/// (USD/EUR/GBP) so the portfolio summary converts without unconverted-currency warnings.
///
/// Policies link to the existing insurer contacts and insured-asset accounts by their stable
/// deterministic ids; no contact or account is created here.
/// </summary>
public static class InsurancePolicyGenerator
{
    private sealed record RenewalSpec(
        DateTime From, DateTime To, decimal Premium, string PremiumCurrency, decimal Coverage, string CoverageCurrency);

    private sealed record PolicySpec(
        string Name,
        string? PolicyNumber,
        InsurancePolicyType Type,
        string InsurerName,
        string? InsuredAccountName,
        string? Notes,
        IReadOnlyList<RenewalSpec> Renewals);

    public static Guid IdFor(string name) => DeterministicGuid.From($"insurance-policy::{name}");

    private static Guid RenewalIdFor(string policyName, int index) =>
        DeterministicGuid.From($"policy-renewal::{policyName}#{index}");

    public static (List<InsurancePolicy> Policies, List<PolicyRenewal> Renewals) Build(DateTime anchor)
    {
        var year = anchor.Year;

        var specs = new List<PolicySpec>
        {
            // Active today, and its current renewal ends within the 30-day expiring-soon window
            // (anchor is mid-June; the term ends 30 June) → reported as ExpiringSoon.
            new(
                "Home Insurance", "HM-2025-44120", InsurancePolicyType.Home,
                Catalog.Contacts.StateFarm, "Primary Residence",
                "Buildings and contents cover for the family home.",
                [
                    new(D(year - 2, 7, 1), D(year - 1, 6, 30), 1180m, Currencies.Usd, 720000m, Currencies.Usd),
                    new(D(year - 1, 7, 1), D(year, 6, 30), 1240m, Currencies.Usd, 750000m, Currencies.Usd),
                ]),

            // The latest renewal already ended before the anchor → Lapsed (needs renewing). Premium in
            // EUR to exercise multi-currency rollups.
            new(
                "Auto Insurance", "AU-2025-90017", InsurancePolicyType.Vehicle,
                Catalog.Contacts.StateFarm, "Family Car (Volvo XC60)",
                "Comprehensive motor insurance for the household vehicle.",
                [
                    new(D(year - 1, 3, 1), D(year, 2, 28), 920m, Currencies.Eur, 32000m, Currencies.Eur),
                ]),

            // A renewal spans the anchor and ends well beyond the window → plain Active.
            new(
                "Health Insurance", "HL-2026-10093", InsurancePolicyType.Health,
                Catalog.Contacts.BlueCross, null,
                "Family private health cover.",
                [
                    new(D(year - 1, 1, 1), D(year - 1, 12, 31), 4200m, Currencies.Usd, 1000000m, Currencies.Usd),
                    new(D(year, 1, 1), D(year, 12, 31), 4380m, Currencies.Usd, 1000000m, Currencies.Usd),
                ]),

            // The only renewal starts in the future → Upcoming. Premium in GBP for currency variety.
            new(
                "Travel Insurance", "TR-2026-55210", InsurancePolicyType.Travel,
                Catalog.Contacts.StateFarm, null,
                "Annual multi-trip travel cover (renews ahead of the summer holiday).",
                [
                    new(D(year, 9, 1), D(year + 1, 8, 31), 260m, Currencies.Gbp, 50000m, Currencies.Gbp),
                ]),

            // No renewals yet → NoCoverage (exercises the empty-history state).
            new(
                "Pet Insurance", null, InsurancePolicyType.Pet,
                Catalog.Contacts.StateFarm, null,
                "Draft policy — awaiting first renewal term.",
                []),
        };

        var policies = new List<InsurancePolicy>();
        var renewals = new List<PolicyRenewal>();
        var createdAt = anchor.AddYears(-2);

        foreach (var spec in specs)
        {
            var policyId = IdFor(spec.Name);
            policies.Add(new InsurancePolicy
            {
                InsurancePolicyId = policyId,
                Name = spec.Name,
                PolicyNumber = spec.PolicyNumber,
                Type = spec.Type,
                InsurerId = Catalog.Contacts.IdFor(spec.InsurerName),
                InsuredAccountId = spec.InsuredAccountName is null ? null : Catalog.Accounts.IdFor(spec.InsuredAccountName),
                Notes = spec.Notes,
                Archived = null,
                CreatedAtUtc = createdAt,
            });

            for (var i = 0; i < spec.Renewals.Count; i++)
            {
                var renewal = spec.Renewals[i];
                renewals.Add(new PolicyRenewal
                {
                    PolicyRenewalId = RenewalIdFor(spec.Name, i),
                    InsurancePolicyId = policyId,
                    FromDate = renewal.From,
                    ToDate = renewal.To,
                    Premium = renewal.Premium,
                    PremiumCurrencyCode = renewal.PremiumCurrency,
                    CoverageAmount = renewal.Coverage,
                    CoverageCurrencyCode = renewal.CoverageCurrency,
                    Notes = null,
                    CreatedAtUtc = renewal.From,
                });
            }
        }

        return (policies, renewals);
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
