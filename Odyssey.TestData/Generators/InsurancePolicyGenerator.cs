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
/// Policies link to the existing contacts and accounts by their stable deterministic ids through the
/// four link collections (issue #27) — insurers, insured accounts, insured contacts and
/// beneficiaries; no contact or account is created here. The set deliberately covers a policy with two
/// insurers, one with two insured accounts, one with NO insurer, and one with an ARCHIVED beneficiary,
/// so every state the read path can report has a seeded example.
/// </summary>
public static class InsurancePolicyGenerator
{
    private sealed record RenewalSpec(
        DateTime From, DateTime To, decimal Premium, string PremiumCurrency, decimal Coverage, string CoverageCurrency);

    private sealed record PolicySpec(
        string Name,
        string? PolicyNumber,
        InsurancePolicyType Type,
        IReadOnlyList<string> InsurerNames,
        IReadOnlyList<string> InsuredAccountNames,
        IReadOnlyList<string> InsuredContactNames,
        IReadOnlyList<string> BeneficiaryNames,
        string? Notes,
        IReadOnlyList<RenewalSpec> Renewals);

    public static Guid IdFor(string name) => DeterministicGuid.From($"insurance-policy::{name}");

    private static Guid RenewalIdFor(string policyName, int index) =>
        DeterministicGuid.From($"policy-renewal::{policyName}#{index}");

    private static Guid LinkIdFor(string policyName, string kind, string target) =>
        DeterministicGuid.From($"policy-link::{policyName}#{kind}#{target}");

    public static InsurancePolicyData Build(DateTime anchor)
    {
        var year = anchor.Year;

        var specs = new List<PolicySpec>
        {
            // Active today, and its current renewal ends within the 30-day expiring-soon window
            // (anchor is mid-June; the term ends 30 June) → reported as ExpiringSoon.
            // TWO INSURERS — cover placed across co-insurers, the case the former scalar column could
            // not express at all.
            new(
                "Home Insurance", "HM-2025-44120", InsurancePolicyType.Home,
                [Catalog.Contacts.StateFarm, Catalog.Contacts.Allstate],
                ["Primary Residence"],
                [Catalog.Contacts.PolicyHolder, Catalog.Contacts.Spouse],
                [],
                "Buildings and contents cover for the family home, placed across two insurers.",
                [
                    new(D(year - 2, 7, 1), D(year - 1, 6, 30), 1180m, Currencies.Usd, 720000m, Currencies.Usd),
                    new(D(year - 1, 7, 1), D(year, 6, 30), 1240m, Currencies.Usd, 750000m, Currencies.Usd),
                ]),

            // The latest renewal already ended before the anchor → Lapsed (needs renewing). Premium in
            // EUR to exercise multi-currency rollups. TWO INSURED ACCOUNTS — a household covering two
            // vehicles on one policy.
            new(
                "Auto Insurance", "AU-2025-90017", InsurancePolicyType.Vehicle,
                [Catalog.Contacts.StateFarm],
                ["Family Car (Volvo XC60)", "Primary Residence"],
                [Catalog.Contacts.PolicyHolder, Catalog.Contacts.Spouse],
                [],
                "Comprehensive motor insurance for the household vehicle, with the garage included.",
                [
                    new(D(year - 1, 3, 1), D(year, 2, 28), 920m, Currencies.Eur, 32000m, Currencies.Eur),
                ]),

            // A renewal spans the anchor and ends well beyond the window → plain Active.
            new(
                "Health Insurance", "HL-2026-10093", InsurancePolicyType.Health,
                [Catalog.Contacts.BlueCross],
                [],
                [Catalog.Contacts.PolicyHolder, Catalog.Contacts.Spouse],
                [],
                "Family private health cover.",
                [
                    new(D(year - 1, 1, 1), D(year - 1, 12, 31), 4200m, Currencies.Usd, 1000000m, Currencies.Usd),
                    new(D(year, 1, 1), D(year, 12, 31), 4380m, Currencies.Usd, 1000000m, Currencies.Usd),
                ]),

            // The only renewal starts in the future → Upcoming. Premium in GBP for currency variety.
            new(
                "Travel Insurance", "TR-2026-55210", InsurancePolicyType.Travel,
                [Catalog.Contacts.StateFarm],
                [],
                [Catalog.Contacts.PolicyHolder],
                [],
                "Annual multi-trip travel cover (renews ahead of the summer holiday).",
                [
                    new(D(year, 9, 1), D(year + 1, 8, 31), 260m, Currencies.Gbp, 50000m, Currencies.Gbp),
                ]),

            // No renewals yet → NoCoverage (exercises the empty-history state), and NO INSURER: a
            // policy drafted before the insurer is known is a valid, healthy record now.
            new(
                "Pet Insurance", null, InsurancePolicyType.Pet,
                [],
                [],
                [],
                [],
                "Draft policy — quote received, insurer not chosen yet.",
                []),

            // Both people-shaped collections at once, INCLUDING AN ARCHIVED BENEFICIARY: the read path
            // returns that link with its id and type but no name, the dialog renders it with no remove
            // control, and an ordinary write can neither remove it nor silently delete it.
            new(
                "Term Life Insurance", "LF-2026-30078", InsurancePolicyType.Life,
                [Catalog.Contacts.Allstate],
                [],
                [Catalog.Contacts.PolicyHolder],
                [Catalog.Contacts.Spouse, Catalog.Contacts.FormerBeneficiary],
                "Level term life cover. One beneficiary's contact has since been archived.",
                [
                    new(D(year - 1, 5, 1), D(year + 4, 4, 30), 540m, Currencies.Usd, 750000m, Currencies.Usd),
                ]),
        };

        var policies = new List<InsurancePolicy>();
        var renewals = new List<PolicyRenewal>();
        var insurers = new List<InsurancePolicyInsurer>();
        var insuredAccounts = new List<InsurancePolicyInsuredAccount>();
        var insuredContacts = new List<InsurancePolicyInsuredContact>();
        var beneficiaries = new List<InsurancePolicyBeneficiary>();
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
                Notes = spec.Notes,
                Archived = null,
                CreatedAtUtc = createdAt,
            });

            insurers.AddRange(spec.InsurerNames.Select(name => new InsurancePolicyInsurer
            {
                Id = LinkIdFor(spec.Name, "insurer", name),
                InsurancePolicyId = policyId,
                ContactId = Catalog.Contacts.IdFor(name),
            }));

            insuredAccounts.AddRange(spec.InsuredAccountNames.Select(name => new InsurancePolicyInsuredAccount
            {
                Id = LinkIdFor(spec.Name, "insured-account", name),
                InsurancePolicyId = policyId,
                AccountId = Catalog.Accounts.IdFor(name),
            }));

            insuredContacts.AddRange(spec.InsuredContactNames.Select(name => new InsurancePolicyInsuredContact
            {
                Id = LinkIdFor(spec.Name, "insured-contact", name),
                InsurancePolicyId = policyId,
                ContactId = Catalog.Contacts.IdFor(name),
            }));

            beneficiaries.AddRange(spec.BeneficiaryNames.Select(name => new InsurancePolicyBeneficiary
            {
                Id = LinkIdFor(spec.Name, "beneficiary", name),
                InsurancePolicyId = policyId,
                ContactId = Catalog.Contacts.IdFor(name),
                // The seeder is not a user, so the attribution is null — the same state a row reaches
                // after its author's account is deleted (SET NULL), which the read path already handles.
                CreatedByUserId = null,
                CreatedAtUtc = createdAt,
            }));

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

        return new InsurancePolicyData(policies, renewals, insurers, insuredAccounts, insuredContacts, beneficiaries);
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>
/// The generated policies with their renewal history and their four link collections (issue #27). A
/// named record rather than a tuple, because six members past two is where a positional result stops
/// being readable at the call site.
/// </summary>
public sealed record InsurancePolicyData(
    List<InsurancePolicy> Policies,
    List<PolicyRenewal> Renewals,
    List<InsurancePolicyInsurer> Insurers,
    List<InsurancePolicyInsuredAccount> InsuredAccounts,
    List<InsurancePolicyInsuredContact> InsuredContacts,
    List<InsurancePolicyBeneficiary> Beneficiaries);
