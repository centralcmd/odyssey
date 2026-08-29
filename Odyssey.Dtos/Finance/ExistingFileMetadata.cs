namespace Odyssey.Dtos.Finance;

public sealed record ExistingFileMetadata
{
    public required Guid Id { get; set; }

    public string? UploadedByUserId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public required long SizeBytes { get; set; }

    // public required string Sha256Hash { get; set; }

    public required Guid FileBlobId { get; set; }

    public string? Description { get; set; }

    public required DateTime UploadedAtUtc { get; set; }
}
