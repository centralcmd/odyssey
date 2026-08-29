using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine regression coverage for the PR #403 review fix, on the journal-entries .ics export
/// surface specifically. See
/// <see cref="ContactVCardIntegrationTests.Export_FilteredContacts_AcrossMultipleChunks_SucceedsAgainstRealEngineWithRetryOnFailure"/>
/// for the full rationale — a fix applied identically across four services still needs its own proof
/// per surface (a PR #403 test-review follow-up finding): <c>JournalEntryIcsService</c> chains
/// <c>EntryDate</c>+<c>JournalEntryId</c> as its sort key and four <c>.Include()</c>s
/// (<c>EntryTags</c>/<c>Contacts</c>/<c>Photos</c>/<c>Attachments</c>) plus a row-count cap that throws
/// on the exact count rather than a bounded probe, none of which Contacts has, so structural similarity
/// to Contacts' proven fix isn't itself proof this surface's fix actually runs against a real retrying
/// engine. <see cref="JournalEntryIcsService.ExportStreamingAsync"/> originally held one explicit
/// <c>RepeatableRead</c> transaction across the whole chunked fetch, which throws
/// <c>InvalidOperationException</c> against any <c>OdysseyContext</c> configured with
/// <c>EnableRetryOnFailure()</c> — exactly how production is configured
/// (<c>Odyssey.Api/DatabaseExtension.cs</c>). EF InMemory never configures a retrying execution
/// strategy, so no InMemory-backed tier could ever have caught this.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class JournalEntryIcsIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Export_FilteredJournalEntries_AcrossMultipleChunks_SucceedsAgainstRealEngineWithRetryOnFailure()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = fixture.RelationalConnectionString;
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), o => o.EnableRetryOnFailure())
            .Options;

        await using (var migrating = new OdysseyContext(options))
        {
            await migrating.Database.MigrateAsync();
            await AttributionUsers.EnsureAsync(migrating, "tester");
        }

        var suffix = Guid.NewGuid().ToString("N");
        // One row past a full chunk forces exactly 2 chunks (ChunkSize + 1), proving the fetch really
        // spans more than the trivial single-chunk case, without seeding an unnecessarily large batch.
        const int rowCount = ExportChunking.ChunkSize + 1;

        await using (var seeding = new OdysseyContext(options))
        {
            var now = DateTime.UtcNow;
            var entryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < rowCount; i++)
            {
                seeding.JournalEntries.Add(new JournalEntry
                {
                    ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                    Title = $"Chunked {suffix} {i}",
                    Content = "Body",
                    EntryDate = entryDate.AddDays(i),
                    CreatedByUserId = "tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await seeding.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(options);
        var service = new JournalEntryIcsService(
            context, new NoopContactLookup(), new NoopFileLookup(), new NoopPhotoLookup(),
            new UnlimitedImportExportLimitsLookup(), new StubJournalLimitsLookup(),
            NullLogger<JournalEntryIcsService>.Instance);

        using var output = new MemoryStream();
        var query = new JournalEntriesQueryParams { Search = suffix }; // Search alone still hits the same chunked loop
        string? fileName = null;
        var reportedCount = 0;
        await service.ExportStreamingAsync(query, includeContacts: false, output, (name, count) =>
        {
            fileName = name;
            reportedCount = count;
        });

        Assert.Equal(rowCount, reportedCount);
        Assert.NotNull(fileName);
        var body = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(rowCount, CountOccurrences(body, "BEGIN:VJOURNAL"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class NoopFileLookup : IFileLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> ExistingImageIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    // Never exercised: our seeded entries link no contacts and includeContacts:false, so
    // JournalEntryIcsService.BuildCalendarAsync never calls this (its contactIds list is always empty).
    private sealed class NoopContactLookup : IContactLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ContactRef>>(new Dictionary<Guid, ContactRef>());

        public Task<IReadOnlyDictionary<Guid, ExistingContact>> ResolveContactsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ExistingContact>>(new Dictionary<Guid, ExistingContact>());

        public Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string term, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<ContactRef>> ListActiveContactRefsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContactRef>>([]);

        public Task<IReadOnlySet<Guid>> ExistingPersonIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyDictionary<Guid, string>> ResolveExternalUidsAsync(IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyDictionary<string, Guid>> ResolveIdsByExternalUidAsync(IReadOnlyCollection<string> externalUids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>());
    }

    // Never exercised: our seeded entries link no photos, so BuildCalendarAsync's photoIds list is
    // always empty and this is never called.
    private sealed class NoopPhotoLookup : IPhotoLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyDictionary<Guid, Guid>> ResolveFileIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());

        public Task<Guid> FindOrCreatePhotoIdForFileAsync(Guid fileId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by export.");

        public Task<IReadOnlyDictionary<Guid, Guid>> FindOrCreatePhotoIdsForFilesAsync(
            IReadOnlyCollection<Guid> fileIds, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by export.");
    }

    // Every cap unlimited / a generous 64 MB — this test is about the transaction fix, not the caps.
    private sealed class UnlimitedImportExportLimitsLookup : IImportExportLimitsLookup
    {
        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportExportLimits(
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                20_000, 5_000, 92, 200, 100,
            IsDegraded: false));
    }
}
