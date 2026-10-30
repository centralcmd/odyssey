using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The file store's registration, shared by <c>Odyssey.Api</c> and <c>Odyssey.Worker</c> (issue #346 A3.1): both
/// hosts mount and verify the volumes, the API serves and writes, the worker writes and maintains. Call it after
/// the host has registered <see cref="OdysseyContext"/>, <see cref="TimeProvider"/> and the memory cache.
/// </summary>
public static class FileStorageServiceCollectionExtensions
{
    public static IServiceCollection AddFileStorage(this IServiceCollection services)
    {
        services.AddOptions<FileStorageVolumesOptions>().BindConfiguration(FileStorageVolumesOptions.SectionName);
        services.AddOptions<FileStorageOptions>().BindConfiguration(FileStorageOptions.SectionName);

        // Reservations commit through a context of their own (§3.5 step 2). Scoped, and with no options action of
        // its own, so it consumes the very DbContextOptions<OdysseyContext> the scoped context uses — in both
        // provider branches, and in a test host that swaps them.
        services.AddDbContextFactory<OdysseyContext>(lifetime: ServiceLifetime.Scoped);

        services.TryAddSingleton(sp => new StorageFeatureCatalogue(sp.GetServices<StorageFeature>()));
        services.TryAddSingleton<FileStorageVolumeRegistry>();
        services.TryAddSingleton<IFileStorageSpaceProbe, DriveInfoSpaceProbe>();
        services.TryAddSingleton<FileStorageSweepMemory>();
        services.TryAddSingleton(DefaultForbiddenRoots);
        services.TryAddSingleton<FileStorageWorkerVerification>();

        services.TryAddScoped<IObjectStore, ObjectStore>();
        services.TryAddScoped<FileStorageVerifier>();
        services.TryAddScoped<FileStorageSweeper>();
        services.TryAddScoped<FileStorageStatusService>();
        return services;
    }

    /// <summary>
    /// Runs the startup verifier in a scope of its own (§3.6). Throws <see cref="FileStorageMisconfiguredException"/>
    /// on any failed check, so the host does not start.
    /// </summary>
    public static async Task<FileStorageVerification> VerifyFileStorageAsync(
        this IServiceProvider services, FileStorageVerifyMode mode, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FileStorageVerifier>().VerifyAsync(mode, cancellationToken);
    }

    /// <summary>
    /// The worker's checks (A3.2): verify-only, and a volume the API has yet to initialise or adopt is waited for —
    /// re-checked every <paramref name="pollInterval"/> for up to <paramref name="timeout"/> — before giving up naming
    /// it. Every other failing verdict throws at once. <see cref="FileStorageWorkerVerification"/> is the caller.
    /// </summary>
    public static async Task VerifyFileStorageForWorkerAsync(
        this IServiceProvider services, TimeSpan pollInterval, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FileStorageVerifier));
        var started = time.GetTimestamp();
        while (true)
        {
            var result = await services.VerifyFileStorageAsync(FileStorageVerifyMode.VerifyOnly, cancellationToken);
            if (result.PendingVolumes.Count == 0)
            {
                return;
            }

            if (time.GetElapsedTime(started) >= timeout)
            {
                var names = string.Join(", ", result.PendingVolumes);
                logger.LogCritical("File volumes {Volumes} were not initialised by the API in time.", names);
                throw new FileStorageMisconfiguredException("NotInitialised",
                    $"Volumes {names} have not been initialised. Only the API writes a volume's marker: start the api service "
                    + "(it initialises an empty volume, or adopts one after a database reset), then restart the worker.");
            }

            logger.LogInformation("Waiting for the API to initialise file volumes {Volumes}.", string.Join(", ", result.PendingVolumes));
            await Task.Delay(pollInterval, time, cancellationToken);
        }
    }

    private static FileStorageForbiddenRoots DefaultForbiddenRoots(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetService<IHostEnvironment>();
        return new FileStorageForbiddenRoots(
        [
            ("Data Protection key ring", configuration["DataProtection:KeysPath"] ?? string.Empty),
            ("application directory", AppContext.BaseDirectory),
            ("content root", environment?.ContentRootPath ?? string.Empty),
            ("temp directory", Path.GetTempPath()),
        ]);
    }
}
