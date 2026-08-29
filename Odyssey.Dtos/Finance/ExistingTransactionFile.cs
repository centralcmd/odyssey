namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransactionFile
{
    public required Guid Id { get; set; }

    public required Guid TransactionId { get; set; }

    // public required Guid FileMetadataId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    public TransactionFileType Type { get; set; } = TransactionFileType.Other;
}
