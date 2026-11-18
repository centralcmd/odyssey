using System.Globalization;
using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// The three areas a Files-store file can live in on disk (issue #448 §3). Placement only: no read, link or claim
/// depends on it. <see cref="ContactAvatars"/> is reachable only through the avatar path and the move, never through
/// <see cref="FileUploadArea"/>.
/// </summary>
public enum StoredFileArea
{
    Finance = 1,
    Journal = 2,
    ContactAvatars = 3,
}

/// <summary>The upload year (UTC, of <c>FileMetadata.UploadedAtUtc</c>) and the numbered bucket inside it.</summary>
public sealed record StoredFileLayout(int Year, int Bucket);

/// <summary>
/// The three storage features of the Files store (issue #448 §3.2), declared in the module-neutral
/// <c>Odyssey.Core.FileStorage.Files</c> namespace so Finance code never names a Journal type to write a file. Layout
/// <c>&lt;root&gt;/&lt;yyyy&gt;/&lt;bucket&gt;/&lt;stem&gt;.&lt;ext&gt;</c>, original names, released after the deletion grace.
/// </summary>
public static class StoredFileAreas
{
    public const string FinanceFeatureKey = "FinanceFiles";
    public const string JournalFeatureKey = "JournalFiles";
    public const string ContactAvatarFeatureKey = "ContactAvatars";

    /// <summary>The store's per-folder limit (§3.3 of #346), the same capacity photos use.</summary>
    public const int BucketCapacity = 10_000;

    public static readonly StorageFeature Finance = Declare(
        FinanceFeatureKey, "finance-files", StorageFeatureReference.To<FileMetadata>(f => f.FinanceStoredObjectId));

    public static readonly StorageFeature Journal = Declare(
        JournalFeatureKey, "journal-files", StorageFeatureReference.To<FileMetadata>(f => f.JournalStoredObjectId));

    public static readonly StorageFeature ContactAvatars = Declare(
        ContactAvatarFeatureKey, "contact-avatars", StorageFeatureReference.To<FileMetadata>(f => f.ContactAvatarStoredObjectId));

    public static readonly IReadOnlyList<StorageFeature> All = [Finance, Journal, ContactAvatars];

    private static StorageFeature Declare(string key, string root, StorageFeatureReference reference) =>
        StorageFeature.Create<StoredFileLayout>(
            key,
            root,
            args => [args.Year.ToString("D4", CultureInfo.InvariantCulture), args.Bucket.ToString(CultureInfo.InvariantCulture)],
            StorageReleaseMode.Graced,
            reference);

    /// <summary>The one closed mapping from the upload enum to an area (§8.5).</summary>
    public static StoredFileArea For(FileUploadArea area) => area switch
    {
        FileUploadArea.Finance => StoredFileArea.Finance,
        FileUploadArea.Journal => StoredFileArea.Journal,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    public static string FeatureKey(StoredFileArea area) => area switch
    {
        StoredFileArea.Finance => FinanceFeatureKey,
        StoredFileArea.Journal => JournalFeatureKey,
        StoredFileArea.ContactAvatars => ContactAvatarFeatureKey,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    /// <summary>The area a file already sits in, from whichever stored-object column is set; null for a legacy file.</summary>
    public static StoredFileArea? AreaOf(FileMetadata file) =>
        file.FinanceStoredObjectId is not null ? StoredFileArea.Finance
        : file.JournalStoredObjectId is not null ? StoredFileArea.Journal
        : file.ContactAvatarStoredObjectId is not null ? StoredFileArea.ContactAvatars
        : null;

    /// <summary>The stored object a file names, whichever column holds it; null for a legacy file.</summary>
    public static Guid? StoredObjectIdOf(FileMetadata file) =>
        file.FinanceStoredObjectId ?? file.JournalStoredObjectId ?? file.ContactAvatarStoredObjectId;

    /// <summary>Points the file at <paramref name="storedObjectId"/> in <paramref name="area"/>'s column.</summary>
    public static void Assign(FileMetadata file, StoredFileArea area, Guid storedObjectId)
    {
        switch (area)
        {
            case StoredFileArea.Finance:
                file.FinanceStoredObjectId = storedObjectId;
                break;
            case StoredFileArea.Journal:
                file.JournalStoredObjectId = storedObjectId;
                break;
            case StoredFileArea.ContactAvatars:
                file.ContactAvatarStoredObjectId = storedObjectId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(area));
        }
    }
}

/// <summary>The bucket capacity, as an options class so a test can make it small.</summary>
public sealed class StoredFileOptions
{
    public int BucketCapacity { get; set; } = StoredFileAreas.BucketCapacity;
}
