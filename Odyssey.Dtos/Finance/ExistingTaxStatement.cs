using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingTaxStatement
{
    public required Guid TaxStatementId { get; set; }
    public required string Name { get; set; }
    public required int FiscalYear { get; set; }
    public required DateTime StartDate { get; set; }
    public required DateTime EndDate { get; set; }

    [StringLength(3)]
    public string BaseCurrencyCode { get; set; } = "USD";

    public decimal? DeclaredTotalAssets { get; set; }
    public decimal? DeclaredTotalLiabilities { get; set; }
    public decimal? DeclaredNetWorth { get; set; }
    public decimal? DeclaredTotalIncome { get; set; }
    public decimal? AssessedTax { get; set; }
    public decimal? SettlementAmount { get; set; }
    public DateTime? SettledAtUtc { get; set; }
    public DateTime? FiledAtUtc { get; set; }
    public DateTime? TaxOfficeApprovedAtUtc { get; set; }

    public TaxStatementStatus Status { get; set; } = TaxStatementStatus.New;
    public string? StatusComment { get; set; }
    public DateTime StatusChangedAt { get; set; }

    public string? Notes { get; set; }
    public DateTime? Archived { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<Guid> TaxTagIds { get; set; } = new();
    public List<Guid> IncomeTagIds { get; set; } = new();
    public List<ExistingTaxStatementFile> Files { get; set; } = new();
}
