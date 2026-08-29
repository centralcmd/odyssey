using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Flattened view of an attached insurance document — the common shape of
/// <see cref="ExistingInsurancePolicyFile"/> and <see cref="ExistingPolicyRenewalFile"/> — so a single
/// files table renders both the policy-level and renewal-level attachments. The download/detach routes
/// are keyed by <see cref="FileId"/> (the file's <c>FileMetadata.Id</c>, which is the route's
/// <c>fileId</c>; see InsuranceController).
/// </summary>
public sealed record InsuranceFileItem(
    Guid FileId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    PolicyFileType FileType)
{
    public static InsuranceFileItem From(ExistingInsurancePolicyFile f) => new(
        f.FileMetadata.Id, f.FileMetadata.FileName, f.FileMetadata.ContentType,
        f.FileMetadata.SizeBytes, f.FileMetadata.UploadedAtUtc, f.FileType);

    public static InsuranceFileItem From(ExistingPolicyRenewalFile f) => new(
        f.FileMetadata.Id, f.FileMetadata.FileName, f.FileMetadata.ContentType,
        f.FileMetadata.SizeBytes, f.FileMetadata.UploadedAtUtc, f.FileType);
}
