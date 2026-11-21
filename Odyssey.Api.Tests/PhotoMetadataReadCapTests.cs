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
/// Since issue #428 the prefix is read from the upload stream before the photo is stored, not back out of
/// the database. The 16 MB maximum bounds the full <c>byte[]</c> extraction materialises per upload.
/// </para>
///
/// <para>
/// The test that makes extraction <em>safe</em> is not the boundary check on the setting — it is the
/// fail-soft one: when the prefix read fails, extraction is skipped and the photo is still stored. No
/// 500, no lost upload.
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
    /// The fail-soft path: a prefix read that fails yields no metadata, not an error. The photo itself is
    /// stored by the caller regardless.
    /// </summary>
    [Fact]
    public async Task WhenThePrefixReadFails_ExtractionIsSkippedRatherThanFailing()
    {
        var service = new PhotoMetadataService(
            new StubExtractor(PhotoMetadata.Empty),
            new StubLimits(readMegabytes: 16), NullLogger<PhotoMetadataService>.Instance);

        var metadata = await service.ExtractAsync(_ => throw new InvalidOperationException("upload stream unreadable"));

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
