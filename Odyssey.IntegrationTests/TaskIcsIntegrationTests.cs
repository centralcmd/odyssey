using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine regression coverage for the PR #403 review fix, on the tasks .ics export surface
/// specifically. See
/// <see cref="ContactVCardIntegrationTests.Export_FilteredContacts_AcrossMultipleChunks_SucceedsAgainstRealEngineWithRetryOnFailure"/>
/// for the full rationale — a fix applied identically across four services still needs its own proof
/// per surface (a PR #403 test-review follow-up finding): <c>TaskIcsService</c> chains
/// <c>Position</c>+<c>JournalTaskId</c> as its sort key and two extra <c>.Include()</c>s
/// (<c>ItemTags</c>/<c>Attachments</c>) plus a tag-name lookup per chunk, none of which Contacts has,
/// so structural similarity to Contacts' proven fix isn't itself proof this surface's fix actually runs
/// against a real retrying engine. <see cref="TaskIcsService.ExportStreamingAsync"/> originally held one
/// explicit <c>RepeatableRead</c> transaction across the whole chunked fetch, which throws
/// <c>InvalidOperationException</c> against any <c>OdysseyContext</c> configured with
/// <c>EnableRetryOnFailure()</c> — exactly how production is configured
/// (<c>Odyssey.Api/DatabaseExtension.cs</c>). EF InMemory never configures a retrying execution
/// strategy, so no InMemory-backed tier could ever have caught this.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class TaskIcsIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Export_FilteredTasks_AcrossMultipleChunks_SucceedsAgainstRealEngineWithRetryOnFailure()
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
            for (var i = 0; i < rowCount; i++)
            {
                seeding.JournalTasks.Add(new JournalTask
                {
                    ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                    Title = $"Chunked {suffix} {i}",
                    Position = i,
                    CreatedByUserId = "tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await seeding.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(options);
        var service = new TaskIcsService(
            context, new NoopFileLookup(), new UnlimitedImportExportLimitsLookup(), new StubJournalLimitsLookup(),
            NullLogger<TaskIcsService>.Instance);

        using var output = new MemoryStream();
        var query = new JournalTasksQueryParams { Search = suffix }; // Search alone still hits the same chunked loop
        string? fileName = null;
        var reportedCount = 0;
        await service.ExportStreamingAsync(query, output, (name, count) =>
        {
            fileName = name;
            reportedCount = count;
        });

        Assert.Equal(rowCount, reportedCount);
        Assert.NotNull(fileName);
        var body = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(rowCount, CountOccurrences(body, "BEGIN:VTODO"));
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
