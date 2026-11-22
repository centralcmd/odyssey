namespace Odyssey.Core.Imaging;

/// <summary>
/// The magic-byte table: whether a file's leading bytes match a known signature for its declared
/// content type. Types with no reliable magic number (plain text, CSV) are trusted to their
/// allow-list and to the forced-attachment + <c>nosniff</c> download path, so they always pass.
///
/// <para>
/// It lives here, in <c>Odyssey.Core.Imaging</c>, because the shared still-image pipeline is
/// <b>storage-agnostic</b> and would otherwise have to reach into <c>Odyssey.Core.Finance</c> to
/// learn what a JPEG is (issue #94 §5). <c>FileValidationService.HeaderMatchesContentType</c>
/// forwards here rather than keeping its own copy: there is exactly <b>one</b> signature table in
/// the solution, so a signature corrected in one place cannot stay wrong in another. The table
/// covers the general upload path's types as well as the three image ones for that reason — moving
/// only the image rows would have created the second copy this exists to prevent.
/// </para>
/// </summary>
public static class FileHeaderSignatures
{
    /// <summary>
    /// How many leading bytes any signature below needs. A caller holding a whole buffer should slice
    /// to this rather than passing megabytes of it.
    /// </summary>
    /// <para>64 since EPUB (#527): its check reads 58 bytes. A shorter header simply never matches EPUB, and every
    /// other signature still matches on its first bytes.</para>
    public const int HeaderLength = 64;

    /// <summary>The EPUB OCF container: a ZIP whose first entry is the uncompressed file <c>mimetype</c>.</summary>
    private static ReadOnlySpan<byte> EpubMimetypeName => "mimetype"u8;

    private static ReadOnlySpan<byte> EpubMediaType => "application/epub+zip"u8;

    /// <summary>
    /// Whether <paramref name="header"/> matches a known signature for <paramref name="contentType"/>.
    /// An unrecognised type returns <c>true</c> — the allow-list, not this table, decides what may be
    /// uploaded at all.
    /// </summary>
    public static bool Matches(string contentType, ReadOnlySpan<byte> header)
    {
        static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature) =>
            bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);

        switch (contentType)
        {
            case "application/pdf":
                return StartsWith(header, "%PDF"u8);
            case "image/jpeg":
            case "image/jpg":
                return StartsWith(header, [0xFF, 0xD8, 0xFF]);
            case "image/png":
                return StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            case "image/gif":
                return StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8);
            case "image/webp":
                // RIFF container with a "WEBP" form type at offset 8.
                return StartsWith(header, "RIFF"u8)
                    && header.Length >= 12 && header.Slice(8, 4).SequenceEqual("WEBP"u8);
            case "application/epub+zip":
                // ZIP local-file header, then (OCF 3.3) the first entry is the stored file "mimetype" whose
                // name starts at offset 30 and whose content, "application/epub+zip", follows at offset 38.
                // A plain ZIP renamed .epub has a different first entry, so it fails here.
                return StartsWith(header, [0x50, 0x4B, 0x03, 0x04])
                    && header.Length >= 38 + EpubMediaType.Length
                    && header.Slice(30, EpubMimetypeName.Length).SequenceEqual(EpubMimetypeName)
                    && header.Slice(38, EpubMediaType.Length).SequenceEqual(EpubMediaType);
            case "application/zip":
            case "application/x-zip-compressed":
            case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
            case "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet":
            case "application/vnd.openxmlformats-officedocument.presentationml.presentation":
                // ZIP local-file-header "PK" — also the container for OOXML (docx/xlsx/pptx).
                return StartsWith(header, [0x50, 0x4B]);
            case "application/msword":
            case "application/vnd.ms-excel":
            case "application/vnd.ms-powerpoint":
                // OLE2 compound-file header — the legacy Office binary format.
                return StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
            case "application/x-7z-compressed":
                return StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]);
            case "application/x-rar-compressed":
                return StartsWith(header, "Rar!"u8);
            default:
                return true;
        }
    }
}
