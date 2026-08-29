using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewTransactionTag
{
    [StringLength(64)]
    public required string Name { get; set; }
    [StringLength(256)]
    public string? Description { get; set; }
    public required bool Archived { get; set; }
}
