using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The Files-store upload's bucket allocation on real MariaDB (issue #448, deferred by #456), where EF InMemory has no row
/// locks: a busy <c>StoredFileBuckets</c> row is a <c>503</c> with <c>Retry-After</c> that writes nothing, and parallel
/// uploads each get their own object while the bucket count stays consistent.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class StoredFileUploadRelationalTests(MariaDbFixture fixture)
{
    private static readonly byte[] Pdf = "%PDF-1.7 relational upload"u8.ToArray();

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)).Options);

    /// <summary>
    /// AC 9: while another transaction holds the bucket row, the upload waits out the bounded lock wait and is refused as
    /// <c>file-storage-unavailable</c>, which the exception handler answers with <c>503</c> and <c>Retry-After</c> — and no
    /// slot, object, row or file is left behind.
    /// </summary>
    [SkippableFact]
    public async Task A_busy_bucket_row_is_a_503_with_retry_after_and_writes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        // Without the retrying strategy: a lock-wait timeout is transient to it, so with it the same 503 arrives only after
        // every retry has also timed out (about 1 m 45 s here) — the mapping under test is the same either way.
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var year = photos.Host.Clock.Now.UtcDateTime.Year;

        await using var holder = NewContext();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `StoredFileBuckets` (`FeatureKey`, `Year`, `CurrentBucket`, `ObjectsWritten`, `UpdatedAtUtc`) VALUES ({StoredFileAreas.FinanceFeatureKey}, {year}, 1, 0, {DateTime.UtcNow})");
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT * FROM `StoredFileBuckets` WHERE `FeatureKey` = {StoredFileAreas.FinanceFeatureKey} AND `Year` = {year} FOR UPDATE");

            await using var scope = photos.Host.Scope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var refused = await Assert.ThrowsAsync<FileStorageUnavailableException>(() =>
                scope.ServiceProvider.GetRequiredService<StoredFileWriter>().StageNewFileAsync(
                    context, StoredFileArea.Finance, new MemoryStream(Pdf), "busy.pdf", "application/pdf", null, null, 1024, default));
            Assert.Empty(context.ChangeTracker.Entries<FileMetadata>());

            var http = await HandleAsync(refused);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
            Assert.Equal(FileStorageUnavailableException.RetryAfterDelaySeconds.ToString(), http.Response.Headers.RetryAfter.ToString());

            await transaction.RollbackAsync();
        }

        await using var check = NewContext();
        var bucket = await check.StoredFileBuckets.AsNoTracking()
            .SingleAsync(b => b.FeatureKey == StoredFileAreas.FinanceFeatureKey && b.Year == year);
        Assert.Equal((1, 0), (bucket.CurrentBucket, bucket.ObjectsWritten));
        Assert.False(await check.StoredObjects.AnyAsync(o => o.FeatureKey == StoredFileAreas.FinanceFeatureKey));
        Assert.False(await check.FileMetadata.AnyAsync(f => f.FinanceStoredObjectId != null));
        Assert.False(Directory.Exists(Path.Combine(photos.VolumeRoot, "finance-files"))
            && Directory.EnumerateFiles(Path.Combine(photos.VolumeRoot, "finance-files"), "*", SearchOption.AllDirectories).Any());
    }

    /// <summary>
    /// AC 7/10: ten uploads at once all succeed, each with its own object and path, and the bucket row's count matches what
    /// was written — with a (test-sized) capacity of 3, so the row lock also serializes the roll-overs.
    /// </summary>
    [SkippableFact]
    public async Task Ten_parallel_uploads_each_get_a_distinct_object_and_a_consistent_bucket_count()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const int uploads = 10;
        const int capacity = 3;
        // With the retrying strategy production runs: concurrent first slots can deadlock (InnoDB error 1213), which the
        // strategy retries. Without it, this test fails on that deadlock.
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString,
            configureServices: services => services.Configure<StoredFileOptions>(o => o.BucketCapacity = capacity), retryOnFailure: true);
        var year = photos.Host.Clock.Now.UtcDateTime.Year;

        var ids = await Task.WhenAll(Enumerable.Range(0, uploads).Select(i => Task.Run(async () =>
        {
            await using var scope = photos.Host.Scope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var file = await scope.ServiceProvider.GetRequiredService<StoredFileWriter>().StageNewFileAsync(
                context, StoredFileArea.Finance, new MemoryStream([.. Pdf, (byte)i]), "Lønnsslipp.pdf", "application/pdf",
                null, null, 1024, default);
            await context.SaveChangesAsync();
            return file.Id;
        })));

        await using var check = NewContext();
        var files = await check.FileMetadata.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync();
        Assert.Equal(uploads, files.Count);
        var objectIds = files.Select(f => f.FinanceStoredObjectId!.Value).ToList();
        Assert.Equal(uploads, objectIds.Distinct().Count());
        var objects = await check.StoredObjects.AsNoTracking().Where(o => objectIds.Contains(o.Id)).ToListAsync();
        Assert.All(objects, o => Assert.Equal(StoredObjectState.Committed, o.State));
        Assert.Equal(uploads, objects.Select(o => o.RelativePath).Distinct().Count());
        Assert.All(objects, o => Assert.True(File.Exists(Path.Combine(photos.VolumeRoot, o.RelativePath))));

        // Every bucket but the last is exactly full, and the row's count is what the last one holds.
        var perBucket = objects
            .Select(o => o.RelativePath.Split('/'))
            .Select(parts => (Year: int.Parse(parts[1]), Bucket: int.Parse(parts[2])))
            .GroupBy(p => p.Bucket)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, Count: g.Count(), Years: g.Select(p => p.Year).Distinct().ToList()))
            .ToList();
        Assert.Equal([1, 2, 3, 4], perBucket.Select(b => b.Key));
        Assert.Equal([3, 3, 3, 1], perBucket.Select(b => b.Count));
        Assert.All(perBucket, b => Assert.Equal([year], b.Years));
        var bucket = await check.StoredFileBuckets.AsNoTracking()
            .SingleAsync(b => b.FeatureKey == StoredFileAreas.FinanceFeatureKey && b.Year == year);
        Assert.Equal((4, 1), (bucket.CurrentBucket, bucket.ObjectsWritten));
    }

    /// <summary>Runs the API's exception handler over <paramref name="error"/>, as the pipeline does for an upload request.</summary>
    private static async Task<HttpContext> HandleAsync(Exception error)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/files";
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = error, Path = "/api/files" });
        await GlobalExceptionHandler.HandleAsync(context);
        return context;
    }
}
