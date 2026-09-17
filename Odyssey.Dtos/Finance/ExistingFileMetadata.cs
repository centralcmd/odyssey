namespace Odyssey.Dtos.Finance;

public sealed record ExistingFileMetadata
{
    public required Guid Id { get; set; }

    public string? UploadedByUserId { get; set; }

    /// <summary>
    /// The display label for <see cref="UploadedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims (issue #106). It accompanies the id rather than replacing it — a
    /// read-modify-write round trip still needs the identifier — and it is <c>null</c> on any
    /// surface that does not resolve it, never a fallback rendered from the id.
    /// </summary>
    public string? UploadedByName { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public required long SizeBytes { get; set; }

    // public required string Sha256Hash { get; set; }

    public required Guid FileBlobId { get; set; }

    public string? Description { get; set; }

    public required DateTime UploadedAtUtc { get; set; }
}
