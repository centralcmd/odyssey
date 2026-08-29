using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Flattened view of a contract attachment (<see cref="ExistingContractFile"/>) for the files table.
/// The download/detach routes are keyed by <see cref="FileId"/> (the file's <c>FileMetadata.Id</c>,
/// which is the route's <c>fileId</c>; see ContractController).
/// </summary>
public sealed record ContractFileItem(
    Guid FileId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    ContractFileType FileType)
{
    public static ContractFileItem From(ExistingContractFile f) => new(
        f.FileMetadata.Id, f.FileMetadata.FileName, f.FileMetadata.ContentType,
        f.FileMetadata.SizeBytes, f.FileMetadata.UploadedAtUtc, f.FileType);
}
