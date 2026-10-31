using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #362 on MariaDB: the migration's seed, CHECKs and keys (AC 20), and the rename's one atomic save — a
/// <c>409 rename-busy</c> commits nothing, name included (AC 17), which EF InMemory cannot show.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PhotoThumbnailRelationalTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task The_migration_seeds_the_single_bucket()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await BuildProviderAsync();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var bucket = await context.PhotoThumbnailBuckets.AsNoTracking().SingleAsync();

        Assert.Equal((PhotoThumbnailBucket.SingletonId, 1), (bucket.Id, bucket.CurrentBucket));
    }

    [SkippableTheory]
    [InlineData("UPDATE PhotoThumbnailJobs SET State = 9 WHERE PhotoId = {0}")]
    [InlineData("UPDATE PhotoThumbnailJobs SET Priority = 7 WHERE PhotoId = {0}")]
    [InlineData("UPDATE PhotoThumbnailJobs SET Attempts = -1 WHERE PhotoId = {0}")]
    [InlineData("UPDATE PhotoThumbnailJobs SET RunId = UUID() WHERE PhotoId = {0}")]
    [InlineData("UPDATE PhotoThumbnailJobs SET RerunRequested = 1 WHERE PhotoId = {0}")]
    [InlineData("UPDATE PhotoThumbnailJobs SET State = 1, LeaseUntilUtc = NULL, RunId = UUID() WHERE PhotoId = {0}")]
    public async Task The_job_checks_refuse_an_impossible_row(string sql)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await BuildProviderAsync();
        var photoId = await CreatePhotoAsync(provider);
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(sql, photoId));
    }

    [SkippableTheory]
    [InlineData("UPDATE PhotoThumbnailBuckets SET PhotosWritten = 10001")]
    [InlineData("UPDATE PhotoThumbnailBuckets SET CurrentBucket = 0")]
    [InlineData("INSERT INTO PhotoThumbnailBuckets (Id, CurrentBucket, PhotosWritten, UpdatedAtUtc) VALUES (2, 1, 0, NOW())")]
    [InlineData("INSERT INTO PhotoThumbnailIncidents (Kind, OccurredAtUtc) VALUES (9, NOW())")]
    [InlineData("INSERT INTO PhotoThumbnailWorkerHeartbeats (WorkerName, State, UpdatedAtUtc) VALUES ('w', 9, NOW())")]
    public async Task The_bucket_incident_and_heartbeat_checks_hold(string sql)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await BuildProviderAsync();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(sql));
    }

    [SkippableFact]
    public async Task A_photo_delete_cascades_its_job_and_thumbnails_and_a_stored_object_cannot_go_first()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await BuildProviderAsync();
        var photoId = await CreatePhotoAsync(provider);
        var objectId = await AddThumbnailRowAsync(provider, photoId);

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.True(await context.PhotoThumbnailJobs.AnyAsync(j => j.PhotoId == photoId));
            // RESTRICT (#346 consumer rule): the object leaves only once nothing references it.
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM StoredObjects WHERE Id = {0}", objectId));

            await context.Database.ExecuteSqlRawAsync("DELETE FROM Photos WHERE PhotoId = {0}", photoId);

            Assert.False(await context.PhotoThumbnailJobs.AnyAsync(j => j.PhotoId == photoId));
            Assert.False(await context.PhotoThumbnails.AnyAsync(t => t.PhotoId == photoId));
            Assert.True(await context.StoredObjects.AnyAsync(o => o.Id == objectId));
        }
    }

    [SkippableFact]
    public async Task A_busy_rename_commits_nothing_and_a_clean_one_commits_name_and_job_together()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var provider = await BuildProviderAsync();
        var photoId = await CreatePhotoAsync(provider);
        var fileId = await FileIdAsync(provider, photoId);
        var before = await JobAsync(provider, photoId);

        using (var scope = provider.CreateScope())
        {
            var interfering = new InterferingObserver(provider, photoId, times: int.MaxValue);
            var busy = await Assert.ThrowsAsync<FileRenameBusyException>(() => Files(scope, interfering)
                .UpdateFileMetadataAsync(fileId, new UpdateFileMetadataRequest { FileName = "Never.jpg" }));
            Assert.Equal(409, busy.StatusCode);
            Assert.Equal(FileService.RenameAttempts, interfering.Calls);
        }

        Assert.Equal("p.jpg", await FileNameAsync(provider, fileId));
        var afterBusy = await JobAsync(provider, photoId);
        // Only the interfering writes landed: the rename's own Generation bump, priority and name never did.
        Assert.Equal(before.Generation + FileService.RenameAttempts, afterBusy.Generation);
        Assert.Equal(before.Priority, afterBusy.Priority);

        using (var scope = provider.CreateScope())
        {
            var interfering = new InterferingObserver(provider, photoId, times: 1);
            await Files(scope, interfering).UpdateFileMetadataAsync(fileId, new UpdateFileMetadataRequest { FileName = "Once.jpg" });
        }

        Assert.Equal("Once.jpg", await FileNameAsync(provider, fileId));
        var afterRename = await JobAsync(provider, photoId);
        Assert.Equal(afterBusy.Generation + 2, afterRename.Generation);
        Assert.Equal(PhotoThumbnailJobPriority.Background, afterRename.Priority);
    }

    private sealed class EmptyMetadataExtractor : IPhotoMetadataExtractor
    {
        public PhotoMetadata Extract(byte[] content) => PhotoMetadata.Empty;
    }

    private static FileService Files(IServiceScope scope, IFileRenameObserver interfering) => new(
        scope.ServiceProvider.GetRequiredService<OdysseyContext>(),
        new FileValidationService(),
        TimeProvider.System,
        // The interference runs after the thumbnail observer has read the job, so it lands between read and save.
        [new PhotoThumbnailRenameObserver(scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>()), interfering]);

    /// <summary>Bumps the job's token from another connection before each save, like a worker's concurrent claim.</summary>
    private sealed class InterferingObserver(ServiceProvider provider, Guid photoId, int times) : IFileRenameObserver
    {
        public int Calls { get; private set; }

        public async Task OnFileRenamingAsync(Guid fileId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls > times)
            {
                return;
            }

            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE PhotoThumbnailJobs SET Generation = Generation + 1 WHERE PhotoId = {0}", [photoId], cancellationToken);
        }

        public bool Owns(Type entityType) => false;
    }

    private static async Task<PhotoThumbnailJobEntry> JobAsync(ServiceProvider provider, Guid photoId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs.AsNoTracking().SingleAsync(j => j.PhotoId == photoId);
    }

    private static async Task<Guid> FileIdAsync(ServiceProvider provider, Guid photoId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos.Where(p => p.PhotoId == photoId).Select(p => p.FileId).SingleAsync();
    }

    private static async Task<string> FileNameAsync(ServiceProvider provider, Guid fileId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileMetadata.Where(f => f.Id == fileId).Select(f => f.FileName).SingleAsync();
    }

    private static async Task<Guid> AddThumbnailRowAsync(ServiceProvider provider, Guid photoId)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var volume = new FileStorageVolume { Id = Guid.NewGuid(), Name = $"v{Guid.NewGuid():N}"[..20], LastSeenAtUtc = DateTime.UtcNow };
        var stored = new StoredObject
        {
            Id = Guid.NewGuid(),
            VolumeId = volume.Id,
            FeatureKey = PhotoThumbnailStorage.FeatureKey,
            RelativePath = $"photo-thumbnails/200/1/{Guid.NewGuid():N}.webp",
            SizeBytes = 1,
            Sha256Hash = Convert.ToHexString(SHA256.HashData([1])).ToLowerInvariant(),
            State = StoredObjectState.Committed,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.FileStorageVolumes.Add(volume);
        context.StoredObjects.Add(stored);
        context.PhotoThumbnails.Add(new PhotoThumbnail
        {
            PhotoThumbnailId = Guid.NewGuid(),
            PhotoId = photoId,
            Size = ThumbnailSize.Small,
            StoredObjectId = stored.Id,
            PixelWidth = 200,
            PixelHeight = 150,
            RecipeVersion = PhotoThumbnailRecipe.Version,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return stored.Id;
    }

    private static async Task<Guid> CreatePhotoAsync(ServiceProvider provider)
    {
        Guid fileId;
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
            fileId = Guid.NewGuid();
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                UploadedByUserId = "user",
                FileName = "p.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 3,
                Sha256Hash = Convert.ToHexString(SHA256.HashData(blob.Content)),
                FileBlobId = blob.Id,
                FileBlob = blob,
                UploadedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using (var scope = provider.CreateScope())
        {
            var photo = await scope.ServiceProvider.GetRequiredService<PhotoService>()
                .Create(new NewPhoto { FileId = fileId }, "user", canAutoCreateTags: false);
            return photo.PhotoId;
        }
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<OdysseyContext>(options =>
            options.UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)));
        services.AddScoped<IContactLookup, ContactLookup>();
        services.AddScoped<IFileLookup, FileLookup>();
        services.AddScoped<IFileReferenceGuard, FileReferenceGuard>();
        services.AddScoped<IImageContentReader, ImageContentReader>();
        services.AddSingleton<IJournalLimitsLookup>(new StubJournalLimitsLookup());
        services.AddSingleton<IPhotoMetadataExtractor>(new EmptyMetadataExtractor());
        services.AddScoped<PhotoMetadataService>();
        // No test here stages a release, so the object store is never reached.
        services.AddScoped(sp => new PhotoThumbnailService(sp.GetRequiredService<OdysseyContext>(), null!, TimeProvider.System));
        services.AddScoped<PhotoService>();

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.MigrateAsync();
            await AttributionUsers.EnsureAsync(context, "u", "user");
        }

        return provider;
    }
}
