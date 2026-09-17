using Odyssey.Core;
using Odyssey.Core.Imaging;
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
    ///
    /// <para>
    /// <b>Public rather than private</b> (issue #86 §4.3): the image paths apply a narrower allow-list
    /// on top of this one and need the same check over a byte span they already hold (the vCard import
    /// path decodes base64 and has no <c>IFormFile</c> at all).
    /// </para>
    ///
    /// <para>
    /// <b>The table itself moved to <see cref="FileHeaderSignatures"/></b> (issue #94 §5) and this
    /// forwards to it. The shared still-image pipeline is storage-agnostic and cannot reach into
    /// <c>Odyssey.Core.Finance</c> to learn what a JPEG is; keeping the table here and copying the
    /// image rows over there would have produced exactly the two-parsers-that-drift outcome the
    /// extraction exists to prevent. This signature is kept so no caller had to change.
    /// </para>
    /// </summary>
    public static bool HeaderMatchesContentType(string contentType, ReadOnlySpan<byte> header) =>
        FileHeaderSignatures.Matches(contentType, header);

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
/// <remarks>
/// Public rather than internal since issue #86: <c>ContactAvatarService</c> takes the lookup directly
/// (it resolves <c>min(global cap, avatar cap)</c> rather than delegating to the validation service), so
/// every direct caller that already knows its limit — tests included — needs the same one-line
/// constant instead of hand-rolling a parallel stub per test project.
/// </remarks>
public sealed class FixedUploadLimits(long maxUploadBytes) : IUploadLimitsLookup
{
    private readonly UploadLimits limits =
        new(maxUploadBytes, (int)Math.Max(1, maxUploadBytes / (1024 * 1024)), IsDegraded: false);

    public Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(limits);
}
