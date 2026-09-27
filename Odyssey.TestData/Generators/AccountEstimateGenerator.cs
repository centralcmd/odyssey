using Odyssey.Context;
using Odyssey.TestData.Catalog;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic time-versioned value estimates (issue #182) for the account whose worth is not
/// derived from transactions — the collectibles asset. The house and the car that used to sit here are
/// property records since issue #218, and their histories moved to <see cref="PropertyGenerator"/>.
/// The account gets a short history so the "current estimate" resolution (greatest <c>EffectiveFrom</c> on or
/// before today) and the history listing both have something to show, and so the net-worth
/// REPLACE policy (estimate over derived balance) is exercised against real seeded data.
///
/// Estimates are always recorded in the account currency (the demo asset account is USD),
/// matching the API rule. Accounts are referenced by their stable deterministic ids; none is created
/// here.
/// </summary>
public static class AccountEstimateGenerator
{
    private sealed record EstimateSpec(DateTime EffectiveFrom, decimal Value, string? Note);

    private sealed record AccountEstimates(string AccountName, string Currency, IReadOnlyList<EstimateSpec> Estimates);

    public static Guid IdFor(string accountName, DateTime effectiveFrom) =>
        DeterministicGuid.From($"account-estimate::{accountName}@{effectiveFrom:yyyy-MM-dd}");

    public static List<AccountEstimate> Build()
    {
        var portfolios = new List<AccountEstimates>
        {
            // Collectibles & art: irregular, generally rising appraisals.
            new(Catalog.Accounts.CollectiblesAndArt, DemoDataDefaults.Currencies.Usd,
            [
                new(D(2020, 11, 1), 22000m, "Initial appraisal."),
                new(D(2023, 5, 1), 28500m, "Re-appraisal after new acquisitions."),
                new(D(2025, 9, 1), 34000m, "Latest insured valuation."),
            ]),
        };

        var estimates = new List<AccountEstimate>();

        foreach (var portfolio in portfolios)
        {
            var accountId = Catalog.Accounts.IdFor(portfolio.AccountName);
            foreach (var spec in portfolio.Estimates)
            {
                estimates.Add(new AccountEstimate
                {
                    AccountEstimateId = IdFor(portfolio.AccountName, spec.EffectiveFrom),
                    AccountId = accountId,
                    Value = spec.Value,
                    CurrencyCode = portfolio.Currency,
                    EffectiveFrom = spec.EffectiveFrom,
                    Note = spec.Note,
                    CreatedAtUtc = spec.EffectiveFrom,
                });
            }
        }

        return estimates;
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
