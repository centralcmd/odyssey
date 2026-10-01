namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransactionTag
{
    public required Guid TransactionTagId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required DateTime? Archived { get; set; }

    /// <summary>
    /// The tag's <see cref="TransactionTagIcons"/> key, or <c>null</c> for the default (issue #279). A
    /// stored key the catalogue no longer has is projected as <c>null</c>, never echoed.
    /// </summary>
    public string? Icon { get; set; }
}
