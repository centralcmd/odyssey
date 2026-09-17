namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransactionFile
{
    public required Guid Id { get; set; }

    public required Guid TransactionId { get; set; }

    // public required Guid FileMetadataId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    /// <summary>
    /// The display label for <see cref="AttachedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims (issue #106). It accompanies the id rather than replacing it — a
    /// read-modify-write round trip still needs the identifier — and it is <c>null</c> on any
    /// surface that does not resolve it, never a fallback rendered from the id.
    /// </summary>
    public string? AttachedByName { get; set; }

    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    public TransactionFileType Type { get; set; } = TransactionFileType.Other;
}
