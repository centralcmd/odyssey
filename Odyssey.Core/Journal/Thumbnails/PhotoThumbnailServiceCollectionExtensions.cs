using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.Journal.Thumbnails;

public static class PhotoThumbnailServiceCollectionExtensions
{
    /// <summary>
    /// The thumbnail feature's shared registration (issue #362 §3.1), called by both hosts after
    /// <see cref="FileStorageServiceCollectionExtensions.AddFileStorage"/>: the storage feature, the service the photo
    /// paths use, the rename observer and the job's collaborators. The job itself, the renderer and the self-test
    /// are registered by <c>Odyssey.Worker</c> alone — nothing decodes in the API.
    /// </summary>
    public static IServiceCollection AddPhotoThumbnails(this IServiceCollection services)
    {
        services.AddSingleton(PhotoThumbnailStorage.Feature);
        services.AddOptions<PhotoThumbnailOptions>();
        services.TryAddSingleton(_ => PhotoThumbnailWorkerIdentity.FromMachine());
        services.TryAddSingleton<PhotoThumbnailProcessHooks>();
        services.TryAddSingleton<IPhotoMetadataExtractor, PhotoMetadataExtractor>();
        services.TryAddScoped<Odyssey.Core.FileStorage.Files.StoredFileContentSource>();
        services.TryAddScoped<IImageContentReader, ImageContentReader>();
        services.TryAddScoped<PhotoThumbnailService>();
        services.AddScoped<IFileRenameObserver, PhotoThumbnailRenameObserver>();
        services.AddJobRunRecording();
        return services;
    }
}
