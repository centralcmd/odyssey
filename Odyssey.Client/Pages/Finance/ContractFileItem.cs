using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Flattened view of a contract attachment (<see cref="ExistingContractFile"/>) for the files table.
/// The download/detach/update routes are keyed by <see cref="FileId"/> (the file's
/// <c>FileMetadata.Id</c>, which is the route's <c>fileId</c>; see ContractController).
/// </summary>
/// <remarks>
/// The four validity fields (issue #146) ride on the LINK row, not the <c>FileMetadata</c>: the same
/// stored file filed against two contracts can carry a different period on each. They are null on
/// every document attached before the feature existed, which the table prints as an em dash rather
/// than guessing.
/// </remarks>
public sealed record ContractFileItem(
    Guid FileId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    ContractFileType FileType,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null,
    DateTime? IssuedAt = null,
    Guid? IssuedBy = null)
{
    public static ContractFileItem From(ExistingContractFile f) => new(
        f.FileMetadata.Id, f.FileMetadata.FileName, f.FileMetadata.ContentType,
        f.FileMetadata.SizeBytes, f.FileMetadata.UploadedAtUtc, f.FileType,
        f.ValidFrom, f.ValidTo, f.IssuedAt, f.IssuedBy);
}
