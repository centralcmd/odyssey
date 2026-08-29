using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Journal;

/// <summary>
/// Runs metadata extraction for a Files-store image: reads a bounded blob prefix (§5.5), then runs the
/// pure extractor under a wall-clock timeout. Best-effort and never fatal — any failure (unreadable
/// stream, timeout, corrupt data) is logged at Warning and yields <see cref="PhotoMetadata.Empty"/>.
/// </summary>
/// <remarks>
/// The read size and the timeout were <c>PhotoLibraryOptions</c> defaults on a bound section with no
/// <c>appsettings.json</c> entry and no environment plumbing at all — nobody could change them without
/// a code edit. They are database-backed since issue #434 (keys 4 and 5), so this service gains the
/// journal limits lookup it did not hold before: one cached read per extraction, on a path that
/// already reads a blob.
///
/// <para>
/// <c>PhotoLibraryOptions</c> is <strong>deleted</strong> rather than kept as a compiled fallback,
/// because both of its members moved and nothing else read it. The fallback for a degraded read is
/// <c>SystemSettingsDefaults</c>, resolved inside the lookup — one number, one place. Leaving a bound
/// options class nobody reads is exactly the "tuning key that can silently disagree with the database"
/// this feature exists to remove.
/// </para>
///
/// <para>
/// Note the unit change: configuration held <strong>bytes</strong>, the setting holds
/// <strong>megabytes</strong> (matching the nine existing size settings), and the lookup converts once
/// at its own boundary so no consumer repeats the arithmetic.
/// </para>
/// </remarks>
public sealed class PhotoMetadataService(
    IImageContentReader reader,
    IPhotoMetadataExtractor extractor,
    IJournalLimitsLookup limits,
    ILogger<PhotoMetadataService> logger)
{
    public async Task<PhotoMetadata> ExtractAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveLimits = await limits.GetAsync(cancellationToken);
            var readBytes = (int)Math.Min(effectiveLimits.PhotoMetadataReadBytes, int.MaxValue);

            var prefix = await reader.ReadPrefixAsync(fileId, readBytes, cancellationToken);
            if (prefix is null || prefix.Length == 0)
            {
                return PhotoMetadata.Empty;
            }

            // Math.Max(1, ...) is kept: the setting's [Range] floor is 1, but a row planted outside the
            // HTTP path could still be 0, and a zero timeout would fail every extraction outright.
            var timeout = TimeSpan.FromSeconds(
                Math.Max(1, effectiveLimits.PhotoMetadataExtractionTimeoutSeconds));
            return await Task.Run(() => extractor.Extract(prefix), cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Photo metadata extraction failed for file {FileId}; proceeding with no metadata.", fileId);
            return PhotoMetadata.Empty;
        }
    }
}
