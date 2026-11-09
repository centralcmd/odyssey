using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>
/// Which body renders a run's detail (issue #410 §3.6, F4): one per <c>$kind</c>, and the base detail for any kind this
/// client does not know. The DTO's converter already reads <c>$kind</c> through its own allow-list into the matching
/// subtype (an unknown or missing kind into the base <see cref="JobRunDetail"/>), so this keys on that type — one
/// entry per <see cref="JobRunDetailJsonConverter.Kinds"/> member, which a test pins.
/// </summary>
public static class JobRunDetailRegistry
{
    public static readonly IReadOnlyDictionary<Type, Type> Bodies = new Dictionary<Type, Type>
    {
        [typeof(ExchangeRateSyncJobRunDetail)] = typeof(JobRunDetailExchangeRateSync),
        [typeof(WeatherJobRunDetail)] = typeof(JobRunDetailWeather),
        [typeof(FileStorageMaintenanceJobRunDetail)] = typeof(JobRunDetailFileStorageMaintenance),
        [typeof(PhotoThumbnailJobRunDetail)] = typeof(JobRunDetailPhotoThumbnails),
        [typeof(PhotoOriginalBackfillJobRunDetail)] = typeof(JobRunDetailPhotoOriginalBackfill),
        [typeof(FileAnalysisJobRunDetail)] = typeof(JobRunDetailFileAnalysis),
        [typeof(FileBlobBackfillJobRunDetail)] = typeof(JobRunDetailFileBlobBackfill),
    };

    public static Type BodyFor(JobRunDetail detail) => Bodies.GetValueOrDefault(detail.GetType(), typeof(JobRunDetailBase));
}
