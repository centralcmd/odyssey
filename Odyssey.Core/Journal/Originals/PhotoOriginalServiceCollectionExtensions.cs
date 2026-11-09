using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.Journal.Originals;

public static class PhotoOriginalServiceCollectionExtensions
{
    /// <summary>
    /// The photo-original feature's shared registration (issue #428 §3.1), called by both hosts after
    /// <see cref="FileStorageServiceCollectionExtensions.AddFileStorage"/>: the storage feature, the content source
    /// every reader uses, the writer and its bucket allocator. The backfill job itself is registered by
    /// <c>Odyssey.Worker</c>.
    /// </summary>
    public static IServiceCollection AddPhotoOriginals(this IServiceCollection services)
    {
        services.AddSingleton(PhotoOriginalStorage.Feature);
        services.AddOptions<PhotoOriginalOptions>();
        services.TryAddScoped<Odyssey.Core.FileStorage.Files.StoredFileContentSource>();
        services.TryAddScoped<IImageContentReader, ImageContentReader>();
        services.TryAddScoped<PhotoOriginalSource>();
        services.TryAddScoped<PhotoOriginalBucketAllocator>();
        services.TryAddScoped<PhotoOriginalWriter>();
        services.TryAddScoped<PhotoBackfillStatusService>();
        services.TryAddScoped<IFileReferenceCounter, FileReferenceCounter>();
        services.AddJobRunRecording();
        return services;
    }
}
