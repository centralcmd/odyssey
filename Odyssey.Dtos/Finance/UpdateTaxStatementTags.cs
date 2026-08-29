namespace Odyssey.Dtos.Finance;

public sealed record UpdateTaxStatementTags
{
    public List<Guid> TaxTagIds { get; set; } = new();
    public List<Guid> IncomeTagIds { get; set; } = new();
}
