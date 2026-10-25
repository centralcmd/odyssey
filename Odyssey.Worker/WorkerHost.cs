using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Configuration;
using Odyssey.Core.SystemSettings;
using Odyssey.Core.Weather;
using Odyssey.Worker.Hosting;

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
            .SetApplicationName(DataProtectionApplicationName)
            .DisableAutomaticKeyGeneration();

        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
    }

    /// <summary>Must equal the API's <c>SetApplicationName</c>, or the two derive different keys from the same files.</summary>
    public const string DataProtectionApplicationName = "Odyssey";
}
