using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The photo metadata read cap (issue #434 key 4) and its fail-soft path.
///
/// <para>
/// The 16 MB maximum is a <strong>compiled assumption</strong> about MariaDB's default
/// <c>max_allowed_packet</c>, which this repository pins nowhere — not in <c>docker-compose.yml</c>, not
/// in the MariaDB init script. That is what makes 16 the honest bound rather than a larger round number:
/// beyond it the <c>SELECT SUBSTRING(...)</c> prefix read simply returns less, or fails.
/// </para>
///
/// <para>
/// So the test that makes the bound <em>safe</em> is not the boundary check on the setting — it is this
/// one: with the cap at its maximum and a server that cannot serve that much, extraction is skipped and
/// the photo is still stored. No 500, no lost upload.
/// </para>
/// </summary>
public class PhotoMetadataReadCapTests
{
    [Fact]
    public async Task TheConfiguredReadSize_IsWhatIsRequested_InBytes()
    {
        var reader = new RecordingReader(new byte[16]);
        var service = new PhotoMetadataService(
            new StubExtractor(new PhotoMetadata { Title = "Canon shot" }),
            new StubLimits(readMegabytes: 12), NullLogger<PhotoMetadataService>.Instance);

        await service.ExtractAsync(reader.ReadPrefix);

        // Megabytes on the setting, bytes at the reader — converted once, at the lookup boundary.
        Assert.Equal(12 * 1024 * 1024, reader.RequestedLength);
    }

    [Fact]
    public async Task AtTheSixteenMegabyteMaximum_ThatIsWhatIsRequested()
    {
        var reader = new RecordingReader(new byte[16]);
        var service = new PhotoMetadataService(
            new StubExtractor(PhotoMetadata.Empty),
            new StubLimits(readMegabytes: 16), NullLogger<PhotoMetadataService>.Instance);

        await service.ExtractAsync(reader.ReadPrefix);

        Assert.Equal(16 * 1024 * 1024, reader.RequestedLength);
    }

    /// <summary>
    /// The fail-soft path, and the reason the 16 MB bound is safe rather than merely smaller than a
    /// larger one: a server that cannot serve the requested prefix yields no metadata, not an error. The
    /// photo itself is stored by the caller regardless.
    /// </summary>
    [Fact]
    public async Task WhenTheServerCannotServeThePrefix_ExtractionIsSkippedRatherThanFailing()
    {
        var service = new PhotoMetadataService(
            new StubExtractor(PhotoMetadata.Empty),
            new StubLimits(readMegabytes: 16), NullLogger<PhotoMetadataService>.Instance);

        var metadata = await service.ExtractAsync(_ => throw new InvalidOperationException("max_allowed_packet exceeded"));

        Assert.Equal(PhotoMetadata.Empty, metadata);
    }

    [Fact]
    public async Task WhenThePrefixComesBackEmpty_ExtractionIsSkipped()
    {
        var service = new PhotoMetadataService(
            new StubExtractor(new PhotoMetadata { Title = "Canon shot" }),
            new StubLimits(readMegabytes: 8), NullLogger<PhotoMetadataService>.Instance);

        Assert.Equal(PhotoMetadata.Empty, await service.ExtractAsync(new RecordingReader([]).ReadPrefix));
    }

    /// <summary>
    /// The timeout keeps its <c>Math.Max(1, …)</c> floor. The setting's <c>[Range]</c> minimum is 1, but a
    /// row planted outside the HTTP path could be 0, and a zero timeout would fail every extraction.
    /// </summary>
    [Fact]
    public async Task AZeroTimeoutPlantedOutsideTheHttpPath_StillExtracts()
    {
        var expected = new PhotoMetadata { Title = "Nikon shot" };
        var service = new PhotoMetadataService(
            new StubExtractor(expected),
            new StubLimits(readMegabytes: 8, timeoutSeconds: 0), NullLogger<PhotoMetadataService>.Instance);

        Assert.Equal(expected.Title, (await service.ExtractAsync(new RecordingReader(new byte[8]).ReadPrefix)).Title);
    }

    /// <summary>A prefix reader that records how many bytes it was asked for.</summary>
    private sealed class RecordingReader(byte[] prefix)
    {
        public int RequestedLength { get; private set; }

        public Task<byte[]?> ReadPrefix(int maxBytes)
        {
            RequestedLength = maxBytes;
            return Task.FromResult<byte[]?>(prefix);
        }
    }

    private sealed class StubExtractor(PhotoMetadata metadata) : IPhotoMetadataExtractor
    {
        public PhotoMetadata Extract(byte[] content) => metadata;
    }

    private sealed class StubLimits(int readMegabytes, int timeoutSeconds = 5) : IJournalLimitsLookup
    {
        public Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalLimits(
                PhotoMaxLinksPerKind: SystemSettingsDefaults.PhotoMaxLinksPerKind,
                PhotoMaxAlbumMembers: SystemSettingsDefaults.PhotoMaxAlbumMembers,
                JournalEntryMaxLinksPerKind: SystemSettingsDefaults.JournalEntryMaxLinksPerKind,
                JournalTaskMaxLinksPerKind: SystemSettingsDefaults.JournalTaskMaxLinksPerKind,
                PhotoMetadataReadBytes: readMegabytes * 1024L * 1024,
                PhotoMetadataExtractionTimeoutSeconds: timeoutSeconds,
                CalendarMaxWindowDays: SystemSettingsDefaults.CalendarMaxWindowDays,
                CalendarMaxEventDurationDays: SystemSettingsDefaults.CalendarMaxEventDurationDays,
                RecurrenceMaxGeneratedOccurrences: SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
                IsDegraded: false));
    }
}
