using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewBudget
{
    [StringLength(64)]
    public required string Name { get; set; }
    [StringLength(256)]
    public string? Description { get; set; }
    public required DateTime StartDate { get; set; }
    public required DateTime EndDate { get; set; }
    [StringLength(3)]
    public string BaseCurrencyCode { get; set; } = "USD";
    public required bool Archived { get; set; }
}
