using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateTaxStatement
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

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? DeclaredTotalAssets { get; set; }

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? DeclaredTotalLiabilities { get; set; }

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? DeclaredNetWorth { get; set; }

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? DeclaredTotalIncome { get; set; }

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? AssessedTax { get; set; }

    // Positive = additional tax owed (paid by user); negative = refund returned to user.
    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? SettlementAmount { get; set; }
    public DateTime? SettledAtUtc { get; set; }

    // The window the settlement tags are summed over. Both null = follow the period +1 year;
    // set both or neither.
    public DateTime? SettlementStartDate { get; set; }
    public DateTime? SettlementEndDate { get; set; }

    public DateTime? FiledAtUtc { get; set; }
    public DateTime? TaxOfficeApprovedAtUtc { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public bool Archived { get; set; }
}
