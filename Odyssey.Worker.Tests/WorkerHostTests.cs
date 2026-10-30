using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Odyssey.Context.Secrets;
using Odyssey.Core.SystemSettings;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Weather;
using Odyssey.Worker.Hosting;
using Xunit;

namespace Odyssey.Worker.Tests;

/// <summary>
/// The worker's service graph as production builds it (<see cref="WorkerHost.AddOdysseyWorker"/>): it
/// resolves, it runs the weather job, and its key ring reads the API's keys without ever writing one.
/// Nothing here starts the host, so no test sends a request to MET or opens a database connection.
/// </summary>
public sealed class WorkerHostTests : IDisposable
{
    private readonly DirectoryInfo keys = Directory.CreateTempSubdirectory("odyssey-worker-keys-");

    public void Dispose() => keys.Delete(recursive: true);

    private ServiceProvider Build(string? keysPath = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OdysseyConnection"] = "server=localhost;port=3306;database=odyssey;user=u;password=p;",
            ["DataProtection:KeysPath"] = keysPath ?? keys.FullName,
        });
        builder.AddOdysseyWorker();
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_graph_resolves_and_hosts_the_weather_job()
    {
        using var provider = Build();

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is PeriodicJobService<WeatherScheduleJob>);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WeatherFetchService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>());
    }

    [Fact]
    public void The_graph_resolves_and_hosts_the_exchange_rate_sync_job()
    {
        using var provider = Build();

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is PeriodicJobService<ExchangeRateSyncJob>);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>());
    }

    /// <summary>A3-3: the file store's maintenance job is hosted here, on the shared registration.</summary>
    [Fact]
    public void The_graph_resolves_and_hosts_the_file_storage_maintenance_job()
    {
        using var provider = Build();

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is PeriodicJobService<FileStorageMaintenanceJob>);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FileStorageSweeper>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IObjectStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FileStorageVerifier>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Odyssey.Context.OdysseyContext>>());
    }

    /// <summary>
    /// The maintenance job verifies the volumes itself, so a failing verdict holds that job alone; the host verifies
    /// nothing before it runs, which used to stop weather and exchange-rate sync along with it (A3.2 as amended).
    /// </summary>
    [Fact]
    public void File_storage_is_verified_by_its_job_not_by_the_host()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "Odyssey.Worker", "Program.cs"));
        Assert.DoesNotContain("VerifyFileStorage", program, StringComparison.Ordinal);

        using var provider = Build();
        Assert.NotNull(provider.GetRequiredService<FileStorageWorkerVerification>());
    }

    [Fact]
    public void A_missing_connection_string_fails_at_startup()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        Assert.ThrowsAny<InvalidOperationException>(() => builder.AddOdysseyWorker());
    }

    [Fact]
    public void The_key_ring_never_generates_a_key()
    {
        using var provider = Build();

        Assert.False(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys);
    }

    /// <summary>
    /// The read-only half of the trade-off in WorkerHost: a secret the API protected is readable here, and
    /// reading it leaves the key directory exactly as the API wrote it.
    /// </summary>
    [Fact]
    public void A_secret_the_api_protected_is_readable_and_nothing_is_written_to_the_ring()
    {
        // The API's configuration (Odyssey.Api/Program.cs): same application name, keys persisted to the volume.
        var api = new ServiceCollection();
        api.AddDataProtection().SetApplicationName(DataProtectionKeyRing.ApplicationName).PersistKeysToFileSystem(keys);
        api.AddSingleton<ISecretProtector, SecretProtector>();
        using var apiProvider = api.BuildServiceProvider();
        var ciphertext = apiProvider.GetRequiredService<ISecretProtector>().Protect("WeatherFrostClientId", "frost-client-id");
        var written = keys.GetFiles().Select(file => (file.Name, file.Length)).ToList();
        Assert.NotEmpty(written);

        using var provider = Build();
        var plaintext = provider.GetRequiredService<ISecretProtector>().Unprotect("WeatherFrostClientId", ciphertext);

        Assert.Equal("frost-client-id", plaintext);
        Assert.Equal(written, keys.GetFiles().Select(file => (file.Name, file.Length)).ToList());
    }

    /// <summary>
    /// Protecting is what makes Data Protection create a key; with none on the volume, the worker must refuse
    /// rather than write one the API never made.
    /// </summary>
    [Fact]
    public void An_empty_key_ring_creates_no_key_even_when_asked_to_protect()
    {
        using var provider = Build();
        var protector = provider.GetRequiredService<ISecretProtector>();

        Assert.Null(protector.Unprotect("WeatherFrostClientId", "not-a-real-payload"));
        Assert.ThrowsAny<Exception>(() => protector.Protect("WeatherFrostClientId", "frost-client-id"));
        Assert.Empty(keys.GetFiles());
    }

    /// <summary>Both hosts name the one shared constant; a literal in either would let the two drift and derive different keys.</summary>
    [Fact]
    public void Both_hosts_name_the_shared_data_protection_application_name()
    {
        foreach (var host in new[] { Path.Combine("Odyssey.Api", "Program.cs"), Path.Combine("Odyssey.Worker", "WorkerHost.cs") })
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), host));
            Assert.Contains($".SetApplicationName({nameof(DataProtectionKeyRing)}.{nameof(DataProtectionKeyRing.ApplicationName)})", source.Replace("Odyssey.Core.SystemSettings.", ""), StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Odyssey.sln not found above the test output.");
    }
}
