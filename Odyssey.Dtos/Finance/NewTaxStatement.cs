using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewTaxStatement
{
    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [Range(1900, 2200)]
    public required int FiscalYear { get; set; }

    [Required]
    public required DateTime StartDate { get; set; }

    [Required]
    public required DateTime EndDate { get; set; }

    [StringLength(3)]
    public string BaseCurrencyCode { get; set; } = "USD";

    public decimal? DeclaredTotalAssets { get; set; }
    public decimal? DeclaredTotalLiabilities { get; set; }
    public decimal? DeclaredNetWorth { get; set; }
    public decimal? DeclaredTotalIncome { get; set; }
    public decimal? AssessedTax { get; set; }

    // Positive = additional tax owed (paid by user); negative = refund returned to user.
    public decimal? SettlementAmount { get; set; }
    public DateTime? SettledAtUtc { get; set; }
    public DateTime? FiledAtUtc { get; set; }
    public DateTime? TaxOfficeApprovedAtUtc { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }
}
