using Odyssey.Context;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> for permission-based API tests.
/// Boots the API in the Testing environment with the EF context swapped to an isolated
/// in-memory database and the <see cref="TestAuthHandler"/> wired in. Feature-specific tests
/// derive a one-line subclass supplying their actor id, feature-flag <paramref name="configuration"/>,
/// and any extra service overrides via <paramref name="configureServices"/>.
/// </summary>
public class OdysseyApiFactory(
    IReadOnlyCollection<string>? permissions = null,
    string actorUserId = TestAuthHandler.DefaultActorUserId,
    IReadOnlyDictionary<string, string?>? configuration = null,
    Action<IServiceCollection>? configureServices = null,
    OdysseyApiFactory? sharingStoreWith = null)
    : WebApplicationFactory<Program>
{
    private readonly string applicationDatabaseName = sharingStoreWith?.applicationDatabaseName ?? $"app-{Guid.NewGuid()}";
    private readonly string domainDatabaseName = sharingStoreWith?.domainDatabaseName ?? $"domain-{Guid.NewGuid()}";

    /// <summary>
    /// ONE store root for the whole assembly, deliberately: a database NAME alone is scoped to EF's
    /// internal service provider, which each host builds its own of, so two factories naming the same
    /// database would silently get two separate stores. Isolation still comes from the per-factory
    /// GUID names above — the root only makes a shared name actually shared.
    ///
    /// <para>
    /// It is static rather than per-factory because a fresh root per factory makes every options
    /// instance distinct, which makes EF build a new internal service provider per factory and trips
    /// <c>ManyServiceProvidersCreatedWarning</c> — an exception by default — once a run passes twenty.
    /// </para>
    ///
    /// <para>
    /// Why share a store at all: a claim-conditional response and a composed permission gate can only
    /// be exercised by a SECOND principal against the first one's data (issue #27 §16 #34). Nothing
    /// else should reach for <c>sharingStoreWith</c> — an isolated store per factory is the default
    /// for good reason.
    /// </para>
    /// </summary>
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    /// <summary>
    /// A per-factory Data Protection keys directory (issue #444 §5). Without it this host runs on an
    /// EPHEMERAL key ring — <c>KeyStorageDirectories.Default</c> returns <c>null</c> with no usable
    /// home directory, which is the normal CI-container case — and the secret-settings write path
    /// refuses every write with <c>503</c>. That would take out the entire reason the test-only secret
    /// key exists, and it would do it asymmetrically: green on a developer laptop that has a home
    /// directory, red in CI, reading as flakiness.
    ///
    /// <para>
    /// Per factory rather than shared, so parallel factories never write to one another's key ring.
    /// The deliberately-ephemeral case has its own host in <c>SecretSettingsKeyRingTests</c> instead of
    /// relying on this default.
    /// </para>
    /// </summary>
    private readonly string dataProtectionKeysPath =
        Path.Combine(Path.GetTempPath(), $"odyssey-tests-dp-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: the latter is applied when the host is BUILT, so
        // Program's top-level statements — which read this key to register the key ring before
        // builder.Build() — would not see it. UseSetting seeds the WebApplicationBuilder's own
        // configuration, which they do.
        builder.UseSetting("DataProtection:KeysPath", dataProtectionKeysPath);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?> { ["UseInMemoryDatabase"] = "true" };
            if (configuration is not null)
            {
                foreach (var entry in configuration)
                {
                    settings[entry.Key] = entry.Value;
                }
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase(applicationDatabaseName, DatabaseRoot)
                    // UserAdministrationService's delete opens a transaction so the acceptance-row
                    // pseudonymization commits with the deletion (issue #354 §6) — a genuine no-op on
                    // InMemory, which would otherwise throw rather than ignore it.
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            // OdysseyContext — finance, journal, photos, calendars and contacts in one model — is
            // registered on a fixed in-memory name by DatabaseExtension, so isolate it per factory to
            // avoid cross-test pollution.
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase(domainDatabaseName, DatabaseRoot)
                    // EF Core InMemory doesn't support real transactions and throws by default rather
                    // than silently no-opping — ContactVCardService's import path opens one for
                    // atomicity (issue #338 review), which is a genuine no-op here. The actual
                    // transactional guarantee is verified by Odyssey.IntegrationTests against real
                    // MariaDB; this just lets that code path run in the fast InMemory-backed API tests.
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            services.AddSingleton(new TestClaimsProvider(permissions, actorUserId));
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(dataProtectionKeysPath))
            {
                Directory.Delete(dataProtectionKeysPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes a <see cref="SystemSetting"/> row directly, <strong>bypassing the settings API</strong>
    /// — so no claim check, no shape validation and, crucially, <em>no cache eviction</em>.
    ///
    /// <para>
    /// That last part is the point for issue #439's kill-switch tests: driving the change through the
    /// API evicts <c>FileAnalysisSettingsLookup</c>'s entry synchronously on the writing instance, so a
    /// test that used the API would pass even if the switch were served from the 30-second cached
    /// snapshot — which is exactly the design the live read rejects. It is also the only way to plant a
    /// value the write validator would refuse (an <c>http://</c> base URL, a credential-bearing one),
    /// which is what the read-path re-validation exists to catch.
    /// </para>
    /// </summary>
    public async Task SetSystemSettingAsync(string key, string value)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var row = await context.SystemSettings.FirstOrDefaultAsync(setting => setting.Key == key);
        if (row is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>Removes a settings row outright — the "absent is healthy" case, distinct from a bad value.</summary>
    public async Task RemoveSystemSettingAsync(string key)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var row = await context.SystemSettings.FirstOrDefaultAsync(setting => setting.Key == key);
        if (row is not null)
        {
            context.SystemSettings.Remove(row);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Turns the file-analysis kill switch on (issue #439). It ships <c>false</c>, so every test that
    /// exercises what analysis <em>does</em> has to flip it first.
    /// </summary>
    public Task EnableFileAnalysisAsync() =>
        SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled, "true");

    /// <summary>
    /// Seed the acting user as a resolvable identity row so author-name enrichment resolves to a known
    /// display name. The InMemory <see cref="OdysseyContext"/> is otherwise empty and the synthetic
    /// actor id (<see cref="TestAuthHandler.DefaultActorUserId"/>) matches no user, so without this the
    /// enrichment always yields null. Returns the seeded username.
    /// </summary>
    public async Task<string> SeedActorUserAsync(string userName = "actor@example.com", string? displayName = null)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Users.Add(new ApplicationUser
        {
            Id = actorUserId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = userName,
            NormalizedEmail = userName.ToUpperInvariant(),
        });

        // Optionally attach a profile (issue #316) so the claim-aware resolver yields a display name
        // even to a caller without users.read (the point of the data-minimisation fix).
        if (displayName is not null)
        {
            context.UserProfiles.Add(new UserProfile { UserId = actorUserId, DisplayName = displayName });
        }

        await context.SaveChangesAsync();
        return userName;
    }
}
