using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// <c>DemoDataSeeder</c>'s one settings write (issue #8): pointing <c>EmailClientBaseUrl</c> at the
/// address this stack actually serves the client on, so the dev and Aspire stacks produce working
/// confirmation and reset links with no environment variable.
///
/// <para>
/// <strong>This is the only place configuration still reaches a settings row</strong>, and the whole
/// reason that is acceptable is the gate around it: <c>IsEnabled</c> confines it to Development and
/// Testing, so no Production deployment can reach it. Each branch below is a way that gate or the
/// ownership rule could be undone by a later edit without anything else noticing.
/// </para>
/// </summary>
public class DemoClientBaseUrlSeedTests
{
    private const string AspireClientUrl = "https://client.aspire.test";
    private const string ComposeFallback = "http://localhost:5199";

    /// <summary>
    /// The Aspire path. <c>AppHost.cs</c> forwards the client's address to the MIGRATIONS resource
    /// precisely so this read succeeds — without that plumbing the seeder silently takes the Compose
    /// fallback below and an Aspire stack mails links to the wrong port.
    /// </summary>
    [Fact]
    public async Task AConfiguredClientUrl_IsSeeded()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(AspireClientUrl, await StoredValueAsync(provider));
    }

    /// <summary>
    /// The Compose path. The literal is correct only because that stack's port is pinned in
    /// <c>docker-compose.yml</c>; it is a fallback, never the primary source.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithNoConfiguredClientUrl_TheComposeFallbackIsSeeded(string? configured)
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: configured);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(ComposeFallback, await StoredValueAsync(provider));
    }

    /// <summary>
    /// A misconfigured value is SKIPPED and warned about, not stored. An unusable row fails every send
    /// closed, which is a worse dev experience than the empty row it would have replaced — and unlike
    /// the write path, nothing validates what the seeder writes except the seeder.
    /// </summary>
    [Theory]
    [InlineData("http://public.example.test")]
    [InlineData("not a url")]
    [InlineData("https://token@client.aspire.test")]
    public async Task AnUnusableConfiguredClientUrl_IsSkippedAndWarnedAbout(string configured)
    {
        var warnings = new List<string>();
        using var provider = BuildProvider(out var seeder, clientBaseUrl: configured, warnings: warnings);

        await seeder.ExecuteAsync(CancellationToken.None);

        // The seeded row is left at whatever the migration put there — empty — not at the bad value.
        Assert.Equal(string.Empty, await StoredValueAsync(provider));
        Assert.Contains(warnings, warning =>
            warning.Contains("EmailClientBaseUrl", StringComparison.Ordinal));

        // The warning names no value: the configured string is operator input and this log is not the
        // place to echo it back.
        Assert.DoesNotContain(warnings, warning => warning.Contains(configured, StringComparison.Ordinal));
    }

    /// <summary>
    /// The canonical form is stored, so a trailing slash does not make the seeded value differ from
    /// what the same input would produce through the settings page.
    /// </summary>
    [Fact]
    public async Task TheStoredValueIsCanonical()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: "  https://client.aspire.test/  ");

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(AspireClientUrl, await StoredValueAsync(provider));
    }

    /// <summary>
    /// <strong>Ownership, never a value comparison.</strong> A row an administrator has edited carries
    /// a non-null <c>UpdatedBy</c> and is left alone — otherwise every restart of a dev stack would
    /// stamp over a value someone deliberately set. Comparing values instead cannot tell "never
    /// touched" from "deliberately set back to the seeded value".
    /// </summary>
    [Fact]
    public async Task ARowAnAdministratorHasEdited_IsLeftAlone()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl);
        await SetRowAsync(provider, "https://chosen.example.test", updatedBy: "admin-user-id");

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal("https://chosen.example.test", await StoredValueAsync(provider));
    }

    /// <summary>
    /// The counterpart: an untouched row IS updated. Without this the test above would still pass if
    /// the seeder had stopped writing altogether.
    /// </summary>
    [Fact]
    public async Task AnUntouchedRow_IsUpdated()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl);
        await SetRowAsync(provider, "http://stale.example.test", updatedBy: null);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(AspireClientUrl, await StoredValueAsync(provider));
    }

    /// <summary>
    /// The row is CREATED when absent. It should exist post-migration, but the fast test tiers build
    /// their schema with <c>EnsureCreated</c> and a database that predates the migration has no row —
    /// creating one is what keeps those stacks producing working links.
    /// </summary>
    [Fact]
    public async Task AnAbsentRow_IsCreated()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl);
        await RemoveRowAsync(provider);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(AspireClientUrl, await StoredValueAsync(provider));
    }

    /// <summary>
    /// <strong>The gate.</strong> Outside Development and Testing the seeder refuses wholesale, so this
    /// write never happens in Production — which is the entire reason a configured value reaching a
    /// settings row is acceptable here at all. Issue #8 N1 rules out configuration adoption; this is
    /// seed data, and the environment gate is what makes the distinction real rather than asserted.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopmentAndTesting_NothingIsSeeded(string environmentName)
    {
        using var provider = BuildProvider(
            out var seeder, clientBaseUrl: AspireClientUrl, environmentName: environmentName);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(string.Empty, await StoredValueAsync(provider));
    }

    /// <summary>Turning demo seeding off inside an allowed environment also skips it.</summary>
    [Fact]
    public async Task WithDemoSeedingDisabled_NothingIsSeeded()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl, seedEnabled: false);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(string.Empty, await StoredValueAsync(provider));
    }

    /// <summary>Running twice changes nothing — the seeder as a whole is idempotent.</summary>
    [Fact]
    public async Task RunningTwice_IsIdempotent()
    {
        using var provider = BuildProvider(out var seeder, clientBaseUrl: AspireClientUrl);

        await seeder.ExecuteAsync(CancellationToken.None);
        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Equal(AspireClientUrl, await StoredValueAsync(provider));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Single(
            await context.SystemSettings.AsNoTracking()
                .Where(setting => setting.Key == SystemSettingsKeys.EmailClientBaseUrl)
                .ToListAsync());
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider(
        out DemoDataSeeder seeder,
        string? clientBaseUrl,
        string environmentName = "Testing",
        bool seedEnabled = true,
        List<string>? warnings = null)
    {
        var provider = MigrationServiceTestHost.Build();

        var settings = new Dictionary<string, string?>
        {
            ["Seed:DemoData"] = seedEnabled ? "true" : "false",
        };

        // Absent is not the same as empty — the seeder's fallback has to cover both.
        if (clientBaseUrl is not null)
        {
            settings["Email:ClientBaseUrl"] = clientBaseUrl;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ILogger<DemoDataSeeder> logger = warnings is null
            ? provider.GetRequiredService<ILogger<DemoDataSeeder>>()
            : new WarningCollector(warnings);

        seeder = new DemoDataSeeder(
            provider, configuration, new TestHostEnvironment { EnvironmentName = environmentName }, logger);
        return provider;
    }

    private static async Task<string?> StoredValueAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == SystemSettingsKeys.EmailClientBaseUrl)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync();
    }

    private static async Task SetRowAsync(IServiceProvider provider, string value, string? updatedBy)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.SystemSettings
            .SingleAsync(setting => setting.Key == SystemSettingsKeys.EmailClientBaseUrl);

        row.Value = value;
        row.UpdatedBy = updatedBy;
        await context.SaveChangesAsync();
    }

    private static async Task RemoveRowAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.SystemSettings
            .SingleOrDefaultAsync(setting => setting.Key == SystemSettingsKeys.EmailClientBaseUrl);

        if (row is not null)
        {
            context.SystemSettings.Remove(row);
            await context.SaveChangesAsync();
        }
    }

    private sealed class WarningCollector(List<string> warnings) : ILogger<DemoDataSeeder>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Odyssey.MigrationService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
