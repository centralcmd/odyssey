using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;

namespace Odyssey.IntegrationTests;

/// <summary>
/// A photo service over real MariaDB and a temp photo volume (issue #428): the file store's test host with the
/// production photo features bound, the photo services registered, and the volume verified. Disposing removes the
/// volume.
/// </summary>
internal sealed class PhotoStoreHost : IAsyncDisposable
{
    private readonly DirectoryInfo volume;

    private PhotoStoreHost(FileStorageTestHost host, DirectoryInfo volume)
    {
        Host = host;
        this.volume = volume;
    }

    public FileStorageTestHost Host { get; }

    public string VolumeRoot => volume.FullName;

    public static async Task<PhotoStoreHost> CreateAsync(
        string connectionString, IPhotoMetadataExtractor? extractor = null, Action<IServiceCollection>? configureServices = null)
    {
        var volume = Directory.CreateTempSubdirectory("odyssey-it-photos-");
        var host = FileStorageTestHost.Create(
            options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)),
            new Dictionary<string, string> { ["photos"] = volume.FullName },
            new Dictionary<string, string>
            {
                [PhotoOriginalStorage.FeatureKey] = "photos",
                [PhotoThumbnailStorage.FeatureKey] = "photos",
                [StoredFileAreas.FinanceFeatureKey] = "photos",
                [StoredFileAreas.JournalFeatureKey] = "photos",
                [StoredFileAreas.ContactAvatarFeatureKey] = "photos",
            },
            features: [],
            configureServices: services =>
            {
                services.AddPhotoThumbnails().AddPhotoOriginals();
                services.AddStoredFiles();
                services.AddScoped<IContactLookup, ContactLookup>();
                services.AddScoped<IFileLookup, FileLookup>();
                services.AddScoped<IFileReferenceGuard, FileReferenceGuard>();
                services.AddSingleton<IJournalLimitsLookup>(new StubJournalLimitsLookup());
                services.AddSingleton<IUploadLimitsLookup>(new FixedUploadLimits(64L * 1024 * 1024));
                services.AddSingleton(extractor ?? new FixedMetadataExtractor(PhotoMetadata.Empty));
                services.AddScoped<PhotoMetadataService>();
                services.AddScoped<PhotoService>();
                services.AddScoped<PhotoAlbumService>();
                configureServices?.Invoke(services);
            });

        // The plain context migrates (the store host's context adds the test-only tables, which no migration has).
        await using (var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)).Options))
        {
            await context.Database.MigrateAsync();
            await AttributionUsers.EnsureAsync(context, "u", "user");

            // Every host gets a fresh temp volume, so the rows an earlier host left behind name a volume that is no
            // longer configured — check V-MissingVolume would refuse to start. Clear the store's rows first, in
            // RESTRICT order: the two photo tables that reference an object, then the objects, then the volumes.
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `PhotoThumbnails`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `PhotoThumbnailJobs`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `PhotoOriginalBackfillFailures`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `PhotoOriginalBuckets`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `Photos` WHERE `StoredObjectId` IS NOT NULL");
            // Files on the store (issue #448) reference an object through RESTRICT keys too.
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM `FileMetadata` WHERE `FinanceStoredObjectId` IS NOT NULL OR `JournalStoredObjectId` IS NOT NULL OR `ContactAvatarStoredObjectId` IS NOT NULL");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `StoredFileBuckets`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `FileBlobBackfillFailures`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `StoredObjects`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `FileStorageOrphanSightings`");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `FileStorageVolumes`");
        }

        await host.VerifyAsync();
        return new PhotoStoreHost(host, volume);
    }

    /// <summary>Uploads <paramref name="bytes"/> as a photo through the service, as <c>POST /api/photos</c> does.</summary>
    public async Task<ExistingPhoto> UploadAsync(
        byte[] bytes, NewPhotoUpload? fields = null, string name = "p.jpg", bool canAutoCreateTags = false)
    {
        await using var scope = Host.Scope();
        var upload = new FileUpload(name, "image/jpeg", bytes.LongLength, () => new MemoryStream(bytes, writable: false));
        return await scope.ServiceProvider.GetRequiredService<PhotoService>()
            .UploadAsync(upload, fields ?? new NewPhotoUpload(), "user", canAutoCreateTags);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (volume.Exists)
        {
            volume.Delete(recursive: true);
        }
    }
}

/// <summary>Returns the same metadata for every image, so a test controls what extraction "finds".</summary>
internal sealed class FixedMetadataExtractor(PhotoMetadata metadata) : IPhotoMetadataExtractor
{
    public PhotoMetadata Extract(byte[] content) => metadata;
}
