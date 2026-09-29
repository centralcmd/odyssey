namespace Odyssey.Dtos.Finance;

public sealed record UpdateTaxStatementTags
{
    public List<Guid> TaxTagIds { get; set; } = new();
    public List<Guid> IncomeTagIds { get; set; } = new();

    // Null leaves the stored settlement tags unchanged, so a client written before this role existed
    // cannot wipe them by omission; an empty list clears them.
    public List<Guid>? SettlementTagIds { get; set; }
}
