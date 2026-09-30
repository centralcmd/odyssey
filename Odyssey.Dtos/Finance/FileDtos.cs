using System.ComponentModel.DataAnnotations;

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

/// <summary>
/// Both bounds match the <c>FileMetadata</c> columns (issue #247). A <c>FileName</c> that passes the bound
/// is still sanitized server-side exactly as an upload's is.
/// </summary>
public sealed record UpdateFileMetadataRequest(
    [StringLength(256)] string? Description,
    [StringLength(256)] string? FileName = null);