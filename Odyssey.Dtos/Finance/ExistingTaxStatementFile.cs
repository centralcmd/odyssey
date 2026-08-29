namespace Odyssey.Dtos.Finance;

public sealed record ExistingTaxStatementFile
{
    public required Guid Id { get; set; }

    public required Guid TaxStatementId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    public required DateTime AttachedAtUtc { get; set; }

    public TaxStatementFileType FileType { get; set; } = TaxStatementFileType.Other;
}
