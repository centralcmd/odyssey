using System.Security.Cryptography;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;

namespace Odyssey.TestData;

/// <summary>
/// A demo Files-store file: its row, the area it is written to and its bytes. The row names no stored object yet — the
/// worker's demo-file seeder writes the bytes onto the object store and points the row at them, because only a host with
/// the file volumes can (the migrations job has none).
/// </summary>
public sealed record DemoFile(FileMetadata Metadata, StoredFileArea Area, byte[] Content);

/// <summary>
/// A demo library photo and its original. The photo carries its own name, type, size and hash; its stored object is
/// written by the worker's demo-file seeder. <see cref="FormerFileId"/> is the file id the same photo was seeded under
/// while photos still wrapped a Files-store file, so a dev database seeded before that keeps being recognised.
/// </summary>
public sealed record DemoPhoto(Photo Photo, byte[] Content, Guid FormerFileId);

internal static class DemoFileFactory
{
    public static DemoFile File(
        Guid id, StoredFileArea area, string? uploaderId, string fileName, string contentType, string? description,
        DateTime uploadedAtUtc, byte[] content) =>
        new(new FileMetadata
        {
            Id = id,
            UploadedByUserId = uploaderId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.LongLength,
            Sha256Hash = Hash(content),
            Description = description,
            UploadedAtUtc = uploadedAtUtc,
        }, area, content);

    /// <summary>Fills the photo's original columns from <paramref name="content"/>.</summary>
    public static DemoPhoto Photo(Photo photo, string fileName, string contentType, byte[] content, Guid formerFileId)
    {
        photo.FileName = fileName;
        photo.ContentType = contentType;
        photo.SizeBytes = content.LongLength;
        photo.Sha256Hash = Hash(content);
        return new DemoPhoto(photo, content, formerFileId);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>A seeded contact's image (issue #86): the contact it belongs to and the file the seeder points it at.</summary>
public sealed record DemoContactAvatar(Guid ContactId, DemoFile File);
