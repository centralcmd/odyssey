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
    /// bucket allocator, and the content source every reader uses.
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
        services.AddJobRunRecording();
        return services;
    }
}
