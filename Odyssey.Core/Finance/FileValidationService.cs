using Odyssey.Core;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Odyssey.Core.Finance;

public class FileValidationService
{
    private readonly IUploadLimitsLookup uploadLimits;
    private readonly IReadOnlySet<string> allowedMimeTypes;

    /// <summary>
    /// Production shape (issue #421 Wave 4): the size cap is read live from the settings store on every
    /// validation, so an administrator lowering it takes effect without a redeploy. It used to be
    /// captured once at startup from <c>FileStorage:MaxFileSizeBytes</c>, which now serves only as the
    /// transport ceiling bounding how far the setting can be raised.
    /// </summary>
    public FileValidationService(IUploadLimitsLookup uploadLimits,
        IEnumerable<string>? allowedMimeTypes = null)
    {
        this.uploadLimits = uploadLimits;
        this.allowedMimeTypes = new HashSet<string>(allowedMimeTypes ?? GetDefaultAllowedMimeTypes());
    }

    /// <summary>A fixed cap, for tests and any direct caller that already knows the limit.</summary>
    public FileValidationService(long maxFileSizeBytes = 64 * 1024 * 1024, // 64 MB default
        IEnumerable<string>? allowedMimeTypes = null)
        : this(new FixedUploadLimits(maxFileSizeBytes), allowedMimeTypes)
    {
    }

    public async Task ValidateFileAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var limits = await uploadLimits.GetAsync(cancellationToken);
        ValidateFile(file, limits.MaxUploadBytes);
    }

    public void ValidateFile(IFormFile file, long maxFileSizeBytes)
    {
        if (file == null)
        {
            throw new ArgumentNullException(nameof(file));
        }

        if (file.Length == 0)
        {
            throw new DomainValidationException("File cannot be empty");
        }

        if (file.Length > maxFileSizeBytes)
        {
            throw new DomainValidationException($"File size {file.Length} bytes exceeds maximum allowed size {maxFileSizeBytes} bytes");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType) || !allowedMimeTypes.Contains(file.ContentType))
        {
            throw new DomainValidationException($"Content type '{file.ContentType}' is not allowed");
        }

        // Defense-in-depth: the Content-Type above is attacker-controlled (a multipart part header), so
        // confirm the leading "magic" bytes match the declared MIME family. This stops a polyglot or a
        // deliberately mislabeled file (e.g. an executable announced as a PDF) from passing the
        // allow-list. Signature-less text formats are exempt — see HeaderMatchesContentType.
        using (var headerStream = file.OpenReadStream())
        {
            Span<byte> header = stackalloc byte[16];
            var read = headerStream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (!HeaderMatchesContentType(file.ContentType, header[..read]))
            {
                throw new DomainValidationException(
                    $"File content does not match the declared content type '{file.ContentType}'");
            }
        }

        if (string.IsNullOrWhiteSpace(file.FileName))
        {
            throw new DomainValidationException("File name cannot be empty");
        }

        // Sanitize filename - remove potentially dangerous characters
        var sanitizedFileName = SanitizeFileName(file.FileName);
        if (sanitizedFileName.Length > 260)
        {
            throw new DomainValidationException("File name is too long (maximum 260 characters)");
        }
    }

    public string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        // Remove path separators and other dangerous characters
        var invalidChars = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Remove control characters
        sanitized = Regex.Replace(sanitized, @"[\x00-\x1F\x7F-\x9F]", "");

        return sanitized.Trim();
    }

    public async Task<string> ComputeSha256HashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Returns whether the file's leading bytes match a known signature for the declared content type.
    /// Types with no reliable magic number (plain text, CSV) are trusted to the allow-list and the
    /// forced-attachment + nosniff download path, so they always pass. Active-content formats such as
    /// SVG are deliberately off the allow-list entirely (see GetDefaultAllowedMimeTypes).
    /// </summary>
    private static bool HeaderMatchesContentType(string contentType, ReadOnlySpan<byte> header)
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

    private static IEnumerable<string> GetDefaultAllowedMimeTypes()
    {
        return new[]
        {
            // Documents
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "text/plain",
            "text/csv",

            // Images. image/svg+xml is intentionally excluded: SVG is an active-content format that can
            // embed <script>/event handlers, so it is treated as an XSS vector and never accepted on upload.
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/gif",
            "image/webp",

            // Archives
            "application/zip",
            "application/x-zip-compressed",
            "application/x-rar-compressed",
            "application/x-7z-compressed"
        };
    }
}
/// <summary>
/// A constant <see cref="IUploadLimitsLookup"/>, so the fixed-cap constructor above has exactly one
/// validation path to feed rather than a parallel one that could drift from it.
/// </summary>
internal sealed class FixedUploadLimits(long maxUploadBytes) : IUploadLimitsLookup
{
    private readonly UploadLimits limits =
        new(maxUploadBytes, (int)Math.Max(1, maxUploadBytes / (1024 * 1024)), IsDegraded: false);

    public Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(limits);
}
