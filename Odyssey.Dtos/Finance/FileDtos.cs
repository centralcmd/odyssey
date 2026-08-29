namespace Odyssey.Dtos.Finance;

public sealed record FileMetadataResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hash,
    DateTime UploadedAtUtc,
    string? Description);

public sealed record FileUploadResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hash,
    DateTime UploadedAtUtc,
    string? Description);

public sealed record FileListItem(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    string? Description);

public sealed record UpdateFileMetadataRequest(string? Description, string? FileName = null);