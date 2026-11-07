using Odyssey.Core.Journal.Originals;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Configuration;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Jobs;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.SystemSettings;
using Odyssey.Core.Weather;
using Odyssey.Worker.Hosting;
using Odyssey.Worker.Thumbnails;

namespace Odyssey.Worker;

/// <summary>
/// The worker's whole service graph, in one method so the tests build exactly what production runs.
/// </summary>
public static class WorkerHost
{
    public static HostApplicationBuilder AddOdysseyWorker(this HostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Always a real database: a job's coordination (leases, concurrency tokens) lives in the rows it
        // works on, and an in-memory database would be a private, empty copy the API never sees.
        var connectionString = builder.Configuration.GetRequiredConnectionString("OdysseyConnection");
        services.AddDbContext<OdysseyContext>(options => options.UseMySql(
            connectionString, new MariaDbServerVersion(new Version(11, 4)), mySql => mySql.EnableRetryOnFailure()));

        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        AddReadOnlyKeyRing(builder);
        services.AddSecretSettingsReading();

        // Jobs. Each is an Odyssey.Core.Jobs.IPeriodicJob, written and tested in Odyssey.Core.
        services.AddWeatherFetching();
        services.AddPeriodicJob<WeatherScheduleJob>();
        services.AddExchangeRateSync();
        services.AddPeriodicJob<ExchangeRateSyncJob>();
        // The file store (issue #346 A3): shared with the API, which serves and writes; here the maintenance job
        // erases released files, sweeps abandoned writes, scrubs and reconciles the disk. The job verifies the volumes
        // before its first pass, so a failing verdict holds it alone.
        services.AddFileStorage();
        services.AddPeriodicJob<FileStorageMaintenanceJob>();
        // Photo thumbnails (issue #362): the job and the Magick.NET decoder run here and nowhere else. A failing
        // self-test or volume holds this job alone (it reports PolicyFailed or StorageUnavailable); the others run on.
        services.AddPhotoThumbnails();
        // Photo originals (issue #428): the content source the thumbnail job reads through, and the backfill that moves
        // legacy originals out of FileBlob. The lease elects one runner per tick across replicas.
        services.AddPhotoOriginals();
        services.AddPeriodicJob<PhotoOriginalBackfillJob>();
        services.AddSingleton<IPhotoThumbnailRenderer, MagickThumbnailRenderer>();
        services.AddSingleton<IThumbnailDecoderSelfTest, MagickDecoderSelfTest>();
        services.AddPeriodicJob<PhotoThumbnailJob>();
        // The shared run log's upkeep (issue #409 §3.4, §3.6): the stale-run sweep every 5 minutes, the retention
        // prune once an hour. Infrastructure, not a catalogue job: it records no runs of its own.
        services.AddJobRunRecording();
        services.AddPeriodicJob<JobRunMaintenanceJob>();

        return builder;
    }

    /// <summary>
    /// The API's Data Protection key ring, read-only. A job reads the encrypted secret store (the Frost client
    /// id today), so this container can decrypt every stored credential — the second container that can,
    /// after the API, and a deliberate trade-off recorded in docs/deployment.md. It never writes the ring:
    /// automatic key generation is off, so the API stays the only key writer, and the same application name
    /// is what makes the API's keys the ones this host derives from.
    /// </summary>
    private static void AddReadOnlyKeyRing(HostApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(DataProtectionKeyRing.ApplicationName)
            .DisableAutomaticKeyGeneration();

        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
    }

}
