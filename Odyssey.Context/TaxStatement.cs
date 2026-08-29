using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

[Index(nameof(FiscalYear))]
[Index(nameof(Archived))]
public class TaxStatement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid TaxStatementId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [Required]
    public required int FiscalYear { get; set; }

    [Required]
    public required DateTime StartDate { get; set; }

    [Required]
    public required DateTime EndDate { get; set; }

    [StringLength(3)]
    [Required]
    public string BaseCurrencyCode { get; set; } = "USD";

    [Precision(18, 6)]
    public decimal? DeclaredTotalAssets { get; set; }

    [Precision(18, 6)]
    public decimal? DeclaredTotalLiabilities { get; set; }

    [Precision(18, 6)]
    public decimal? DeclaredNetWorth { get; set; }

    [Precision(18, 6)]
    public decimal? DeclaredTotalIncome { get; set; }

    [Precision(18, 6)]
    public decimal? AssessedTax { get; set; }

    // Declared post-assessment balance. Positive = additional tax owed (paid by user);
    // negative = refund returned to user.
    [Precision(18, 6)]
    public decimal? SettlementAmount { get; set; }

    // When the settlement was paid/received (often the year after the income year).
    public DateTime? SettledAtUtc { get; set; }

    // When the user filed the statement to the tax office.
    public DateTime? FiledAtUtc { get; set; }

    // When the tax office approved/assessed the statement (distinct from the user's Status=Approved).
    public DateTime? TaxOfficeApprovedAtUtc { get; set; }

    [Required]
    public TaxStatementStatus Status { get; set; } = TaxStatementStatus.New;

    [StringLength(256)]
    public string? StatusComment { get; set; }

    [Required]
    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<TaxStatementTag> TaxStatementTags { get; set; } = new List<TaxStatementTag>();
    public ICollection<TaxStatementFile> TaxStatementFiles { get; set; } = new List<TaxStatementFile>();
}
