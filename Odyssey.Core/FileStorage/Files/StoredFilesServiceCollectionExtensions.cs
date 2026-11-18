using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.FileStorage.Files;

public static class StoredFilesServiceCollectionExtensions
{
    /// <summary>
    /// The Files store's shared registration on the object store (issue #448 §3.2), called by both hosts after
    /// <see cref="FileStorageServiceCollectionExtensions.AddFileStorage"/>: the three storage features, the writer and its
    /// bucket allocator, the content source every reader uses, the classifier and the move's status. The move job itself is
    /// registered by <c>Odyssey.Worker</c>.
    /// </summary>
    public static IServiceCollection AddStoredFiles(this IServiceCollection services)
    {
        foreach (var feature in StoredFileAreas.All)
        {
            services.AddSingleton(feature);
        }

        services.AddOptions<StoredFileOptions>();
        services.TryAddScoped<StoredFileBucketAllocator>();
        services.TryAddScoped<StoredFileWriter>();
        services.TryAddScoped<StoredFileContentSource>();
        services.TryAddScoped<IFileReferenceCounter, FileReferenceCounter>();
        services.TryAddScoped<FileAreaClassifier>();
        services.TryAddScoped<FileBackfillStatusService>();
        services.AddJobRunRecording();
        return services;
    }
}
