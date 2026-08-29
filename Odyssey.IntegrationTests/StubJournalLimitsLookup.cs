using Odyssey.Core.Journal;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Defaults-only <see cref="IJournalLimitsLookup"/> for the relational fixtures. Shared rather than
/// re-declared per test class, because issue #434 gave four more services this dependency and a
/// per-file stub had already been copied twice.
/// </summary>
internal sealed class StubJournalLimitsLookup : IJournalLimitsLookup
{
    /// <summary>Every value at its shipped default, so a fixture that does not care reads production behaviour.</summary>
    public JournalLimits Limits { get; set; } = new(
        PhotoMaxLinksPerKind: 50,
        PhotoMaxAlbumMembers: 1000,
        JournalEntryMaxLinksPerKind: 50,
        JournalTaskMaxLinksPerKind: 50,
        PhotoMetadataReadBytes: 8L * 1024 * 1024,
        PhotoMetadataExtractionTimeoutSeconds: 5,
        CalendarMaxWindowDays: 92,
        CalendarMaxEventDurationDays: 366,
        RecurrenceMaxGeneratedOccurrences: 1000,
        IsDegraded: false);

    public Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Limits);
}
