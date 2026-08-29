using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic yearly tax statements (issue #173) with their role-tagged reconciliation links.
/// One statement per fiscal year from 2023 up to the anchor year, deliberately exercising every
/// <see cref="TaxStatementStatus"/> (Approved, Flagged, New), the settled / refunded / unsettled
/// settlement states, filed vs. unfiled, and fully-declared vs. draft (null) figures — so the list,
/// detail and reconciliation-report surfaces all have representative data.
///
/// Each statement links existing demo transaction tags by role (<see cref="TaxStatementTagRole"/>):
/// the income categories drive the report's "actual income" and the Taxes category drives "advance
/// tax paid", so the derived-vs-declared reconciliation produces real variances against the seeded
/// transaction stream. Tags and statements are referenced/created by stable deterministic ids.
///
/// Files are intentionally not seeded: a <see cref="TaxStatementFile"/> requires a real stored
/// <c>FileMetadata</c> blob, which the demo seeder does not provision.
/// </summary>
public static class TaxStatementGenerator
{
    private sealed record StatementSpec(
        int FiscalYear,
        decimal? DeclaredAssets,
        decimal? DeclaredLiabilities,
        decimal? DeclaredNetWorth,
        decimal? DeclaredIncome,
        decimal? AssessedTax,
        decimal? SettlementAmount,
        DateTime? SettledAtUtc,
        DateTime? FiledAtUtc,
        DateTime? TaxOfficeApprovedAtUtc,
        TaxStatementStatus Status,
        DateTime StatusChangedAt,
        string? StatusComment,
        string? Notes,
        IReadOnlyList<string> IncomeTags,
        IReadOnlyList<string> TaxTags);

    public static Guid IdFor(int fiscalYear) => DeterministicGuid.From($"tax-statement::{fiscalYear}");

    private static Guid TagLinkIdFor(int fiscalYear, string tagName, TaxStatementTagRole role) =>
        DeterministicGuid.From($"tax-statement-tag::{fiscalYear}::{tagName}::{role}");

    public static (List<TaxStatement> Statements, List<TaxStatementTag> Tags) Build()
    {
        var incomeTags = new[]
        {
            Catalog.Tags.Salary, Catalog.Tags.Bonus, Catalog.Tags.Dividends, Catalog.Tags.InterestIncome,
        };
        var taxTags = new[] { Catalog.Tags.Taxes };

        var specs = new List<StatementSpec>
        {
            // Closed, fully assessed year: filed, approved by the tax office, small balance owed and paid.
            new(2023, 820000m, 410000m, 410000m, 96000m, 21800m, 450m, D(2024, 5, 15), D(2024, 3, 20), D(2024, 5, 1),
                TaxStatementStatus.Approved, D(2024, 5, 1), null,
                "Filed and assessed; small residual tax paid in May.",
                incomeTags, taxTags),

            // Closed year ending in a refund.
            new(2024, 905000m, 388000m, 517000m, 101500m, 23400m, -620m, D(2025, 6, 10), D(2025, 3, 18), D(2025, 5, 20),
                TaxStatementStatus.Approved, D(2025, 5, 20), null,
                "Filed and assessed; modest refund received.",
                [.. incomeTags, Catalog.Tags.RentalIncome], taxTags),

            // Filed but flagged for review (variance against derived balances); not yet settled.
            new(2025, 980000m, 365000m, 615000m, 108000m, 25100m, null, null, D(2026, 3, 15), null,
                TaxStatementStatus.Flagged, D(2026, 3, 16),
                "Declared net worth diverges from derived balances — review before approval.",
                "Awaiting clarification on the brokerage valuation before final submission.",
                incomeTags, taxTags),

            // Current fiscal year in progress: draft with no declared figures yet.
            new(2026, null, null, null, null, null, null, null, null, null,
                TaxStatementStatus.New, D(2026, 1, 1), null,
                "Draft for the current fiscal year — figures to be completed at year end.",
                [Catalog.Tags.Salary, Catalog.Tags.InterestIncome], taxTags),
        };

        var statements = new List<TaxStatement>();
        var tagLinks = new List<TaxStatementTag>();

        foreach (var spec in specs)
        {
            var statementId = IdFor(spec.FiscalYear);
            statements.Add(new TaxStatement
            {
                TaxStatementId = statementId,
                Name = $"Tax Year {spec.FiscalYear}",
                FiscalYear = spec.FiscalYear,
                StartDate = D(spec.FiscalYear, 1, 1),
                EndDate = D(spec.FiscalYear, 12, 31),
                BaseCurrencyCode = Currencies.Usd,
                DeclaredTotalAssets = spec.DeclaredAssets,
                DeclaredTotalLiabilities = spec.DeclaredLiabilities,
                DeclaredNetWorth = spec.DeclaredNetWorth,
                DeclaredTotalIncome = spec.DeclaredIncome,
                AssessedTax = spec.AssessedTax,
                SettlementAmount = spec.SettlementAmount,
                SettledAtUtc = spec.SettledAtUtc,
                FiledAtUtc = spec.FiledAtUtc,
                TaxOfficeApprovedAtUtc = spec.TaxOfficeApprovedAtUtc,
                Status = spec.Status,
                StatusComment = spec.StatusComment,
                StatusChangedAt = spec.StatusChangedAt,
                Notes = spec.Notes,
                Archived = null,
                CreatedAtUtc = D(spec.FiscalYear, 1, 1),
            });

            foreach (var tagName in spec.IncomeTags)
            {
                tagLinks.Add(new TaxStatementTag
                {
                    Id = TagLinkIdFor(spec.FiscalYear, tagName, TaxStatementTagRole.Income),
                    TaxStatementId = statementId,
                    TransactionTagId = Catalog.Tags.IdFor(tagName),
                    Role = TaxStatementTagRole.Income,
                });
            }

            foreach (var tagName in spec.TaxTags)
            {
                tagLinks.Add(new TaxStatementTag
                {
                    Id = TagLinkIdFor(spec.FiscalYear, tagName, TaxStatementTagRole.TaxPayment),
                    TaxStatementId = statementId,
                    TransactionTagId = Catalog.Tags.IdFor(tagName),
                    Role = TaxStatementTagRole.TaxPayment,
                });
            }
        }

        return (statements, tagLinks);
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
