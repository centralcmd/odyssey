using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// One fiscal year's declared figures, as plotted by the year-over-year overview charts. A lean
/// projection: no notes, tags or file metadata — only what the charts read.
/// </summary>
public sealed record TaxStatementYearFigures
{
    public int FiscalYear { get; set; }

    [StringLength(3)]
    public string BaseCurrencyCode { get; set; } = "USD";

    public decimal? DeclaredTotalAssets { get; set; }

    public decimal? DeclaredTotalLiabilities { get; set; }

    public decimal? DeclaredNetWorth { get; set; }

    public decimal? DeclaredTotalIncome { get; set; }

    public decimal? AssessedTax { get; set; }

    public decimal? SettlementAmount { get; set; }
}

/// <summary>
/// Summary rollup for the tax-statements page header (issue #372): the years-on-file count, the
/// first/latest assessed year, and the per-year declared series behind the overview charts. Replaces
/// the page's former whole-table fetch.
/// </summary>
/// <remarks>
/// Archived statements are counted in <see cref="TotalStatements"/> only — <see cref="ActiveCount"/>,
/// the year bounds and <see cref="Years"/> all cover the live set, mirroring the page's rule that an
/// archived year drops out of the header and the charts.
/// </remarks>
public sealed record TaxStatementSummary
{
    public int TotalStatements { get; set; }

    /// <summary>Non-archived statements — the "N years on file" figure.</summary>
    public int ActiveCount { get; set; }

    /// <summary>The most recent live fiscal year, or <c>null</c> when nothing is on file.</summary>
    public int? LatestFiscalYear { get; set; }

    /// <summary>The earliest live fiscal year — the baseline the charts compare against.</summary>
    public int? FirstFiscalYear { get; set; }

    /// <summary>Live statements' declared figures, oldest fiscal year first.</summary>
    public List<TaxStatementYearFigures> Years { get; set; } = new();
}
