namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransactionTag
{
    public required Guid TransactionTagId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required DateTime? Archived { get; set; }
}
