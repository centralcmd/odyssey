using System.Security.Cryptography;
using Odyssey.Context;

namespace Odyssey.Testing.FileStorage;

/// <summary>
/// Photo fixtures for issue #428: distinct image bytes that pass the magic-byte check, and a <b>legacy</b> photo — one
/// created before #428, still wrapping a Files-store file — which no production path creates any more and which the
/// backfill and the legacy read/delete paths still have to handle.
/// </summary>
public static class PhotoTestData
{
    /// <summary>A JPEG header followed by <paramref name="seed"/>, so different seeds never collide on the duplicate check.</summary>
    public static byte[] Jpeg(int seed = 0) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, .. BitConverter.GetBytes(seed), 0xFF, 0xD9];

    // Starts well above the small literal seeds tests pass explicitly (Jpeg(1), Jpeg(5) …), so the two never collide.
    private static int seed = 1_000_000;

    /// <summary>
    /// A seed no earlier call in this process returned: fresh bytes for a fixture that must not collide on the duplicate
    /// check, without the flake (and the irreproducible failure) a random seed would bring.
    /// </summary>
    public static int NextSeed() => Interlocked.Increment(ref seed);

    /// <summary>The creation time a fixture gets when the test does not name one: fixed, so a year-bucket path is stable.</summary>
    public static readonly DateTime DefaultCreatedAt = new(2024, 3, 15, 10, 0, 0, DateTimeKind.Utc);

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Stages a legacy photo and the file it wraps; the caller saves.</summary>
    public static (Photo Photo, FileMetadata File) StageLegacyPhoto(
        OdysseyContext context, byte[]? bytes = null, string fileName = "p.jpg", string? userId = null,
        DateTime? takenAt = null, DateTime? createdAt = null)
    {
        bytes ??= Jpeg(NextSeed());
        var now = createdAt ?? DefaultCreatedAt;
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = userId,
            FileName = fileName,
            ContentType = "image/jpeg",
            SizeBytes = bytes.LongLength,
            Sha256Hash = Sha256(bytes),
            FileBlobId = blob.Id,
            UploadedAtUtc = now,
        };
        var photo = new Photo
        {
            PhotoId = Guid.NewGuid(),
            FileId = file.Id,
            TakenAt = takenAt,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(file);
        context.Photos.Add(photo);
        return (photo, file);
    }
}
