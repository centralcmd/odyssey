using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Flattened view of an attached insurance document — the shape
/// <see cref="ExistingPolicyRenewalFile"/> renders as in the files table. Since issue #26 a document
/// belongs to a renewal period and nowhere else, so there is one source shape rather than two. The
/// download/detach routes are keyed by <see cref="FileId"/> (the file's <c>FileMetadata.Id</c>, which
/// is the route's <c>fileId</c>; see InsuranceController).
/// </summary>
public sealed record InsuranceFileItem(
    Guid FileId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    PolicyFileType FileType)
{
    public static InsuranceFileItem From(ExistingPolicyRenewalFile f) => new(
        f.FileMetadata.Id, f.FileMetadata.FileName, f.FileMetadata.ContentType,
        f.FileMetadata.SizeBytes, f.FileMetadata.UploadedAtUtc, f.FileType);
}
