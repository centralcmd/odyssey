using System.Reflection;
using Odyssey.Context;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> for permission-based API tests.
/// Boots the API in the Testing environment with the EF context swapped to an isolated
/// in-memory database and the <see cref="TestAuthHandler"/> wired in. Feature-specific tests
/// derive a one-line subclass supplying their actor id, feature-flag <paramref name="configuration"/>,
/// and any extra service overrides via <paramref name="configureServices"/>.
///
/// <para>
/// <b>Prefer one host per test class.</b> Booting a host costs far more than a test body, so a new test class
/// derives from <see cref="SharedApiHostTests{THost}"/> and sets each test's caller with
/// <see cref="SharedApiHostClients.ForCaller{T}"/> or <see cref="SharedApiHostClients.ClientFor"/> instead of
/// building a factory per test. <see cref="SharedApiHostClients"/> describes the pattern and when a test still
/// needs a host of its own (its own configuration or services, or host-wide state the reset does not restore).
/// </para>
/// </summary>
public class OdysseyApiFactory(
    IReadOnlyCollection<string>? permissions = null,
    string actorUserId = TestAuthHandler.DefaultActorUserId,
    IReadOnlyDictionary<string, string?>? configuration = null,
    Action<IServiceCollection>? configureServices = null,
    OdysseyApiFactory? sharingStoreWith = null)
    : WebApplicationFactory<Program>
{
    private readonly string domainDatabaseName = sharingStoreWith?.domainDatabaseName ?? $"domain-{Guid.NewGuid()}";

    /// <summary>The caller every request without a permissions header is authenticated as.</summary>
    private readonly TestClaimsProvider claimsProvider = new(permissions, actorUserId);

    /// <summary>
    /// Sets the default caller's permissions — what <c>CreateClient()</c> clients, and helpers that call it, are
    /// authenticated with — until the next <see cref="ResetStoreAsync"/>, which restores the constructor's set. This is
    /// how a shared host stands in for a <c>new Factory(permissions)</c> per test; returns the factory for chaining.
    /// </summary>
    public OdysseyApiFactory UsePermissions(IReadOnlyCollection<string>? permissions)
    {
        claimsProvider.Permissions = permissions;
        return this;
    }

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

    /// <summary>
    /// A per-factory file volume (issues #428 §3.7, #448). Photo originals and every Files-store upload live on the
    /// object store with no database fallback, so every API host binds <c>PhotoOriginals</c>, <c>PhotoThumbnails</c>
    /// the four file areas (<c>FinanceFiles</c>, <c>JournalFiles</c>, <c>ContactAvatars</c>, <c>LibraryFiles</c>) and <c>NewsArticles</c> to one temp volume,
    /// unless the factory configures its own volumes (<c>FileStorageApiFactory</c>). Deleted on dispose.
    /// </summary>
    private readonly string fileVolumePath = sharingStoreWith?.fileVolumePath
        ?? Path.Combine(Path.GetTempPath(), $"odyssey-tests-files-{Guid.NewGuid():N}");

    /// <summary>A factory sharing another's store shares its volume too (files live there since issue #448); the owner deletes it.</summary>
    private readonly bool sharesFileVolume = sharingStoreWith is not null;

    /// <summary>The free space the file volume reports; a test sets it to exercise the upload reserve.</summary>
    public TestSpaceProbe FileVolumeSpace { get; } = new();

    /// <summary>The temp file volume's root, for a test that inspects the files written.</summary>
    public string FileVolumeRoot => fileVolumePath;

    /// <summary>Set to <c>"true"</c> in a factory's configuration for a host with no volume at all (the no-op store).</summary>
    public const string NoFileVolumeKey = "Testing:NoFileVolume";

    /// <summary>The permit count every <c>RateLimiting:*</c> section gets on a host shared across tests.</summary>
    private const string SharedHostPermitLimit = "1000000";

    /// <summary>Every <c>RateLimiting:*</c> section the API binds, found from its <c>SectionName</c> constants.</summary>
    private static readonly Lazy<string[]> RateLimitingSections = new(() => typeof(Program).Assembly.GetTypes()
        .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        .Where(field => field is { IsLiteral: true, Name: "SectionName" } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Where(name => name.StartsWith("RateLimiting:", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToArray());

    private bool sharedAcrossTests;

    /// <summary>
    /// Marks this factory as one host serving a whole test class (<see cref="SharedApiHostTests{THost}"/> calls it
    /// before the host boots): every rate limit is raised out of reach, since one actor id makes every request of
    /// every test count against one window. Has no effect once the host is running.
    /// </summary>
    internal void MarkSharedAcrossTests() => sharedAcrossTests = true;

    /// <summary>Whether the host boots with any volume (its own temp one or configured ones) — and so verifies, and seeds.</summary>
    private bool HasFileVolumes => configuration?.GetValueOrDefault(NoFileVolumeKey) != "true";

    private bool OwnsFileVolume =>
        configuration?.Keys.Any(k => k.StartsWith("FileStorage:Volumes:", StringComparison.Ordinal)) != true
        && configuration?.GetValueOrDefault(NoFileVolumeKey) != "true";

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
            if (OwnsFileVolume)
            {
                Directory.CreateDirectory(fileVolumePath);
                settings["FileStorage:Volumes:photos:RootPath"] = fileVolumePath;
                settings["FileStorage:Bindings:PhotoOriginals"] = "photos";
                settings["FileStorage:Bindings:PhotoThumbnails"] = "photos";
                // Files on the store (issue #448): every upload writes a real file, so every host binds the three areas.
                settings["FileStorage:Bindings:FinanceFiles"] = "photos";
                settings["FileStorage:Bindings:JournalFiles"] = "photos";
                settings["FileStorage:Bindings:ContactAvatars"] = "photos";
                settings["FileStorage:Bindings:LibraryFiles"] = "photos";
                // News reader (issue #470): the NewsArticles feature is registered by every API host.
                settings["FileStorage:Bindings:NewsArticles"] = "photos";
            }

            if (sharedAcrossTests)
            {
                // A shared host serves a whole class from one actor id, so a per-user window would fill across tests
                // that each stay well inside it. Explicit configuration below still wins, and a test that exercises a
                // limit keeps a fresh host (it would fail loudly here, never pass by accident).
                foreach (var section in RateLimitingSections.Value)
                {
                    settings[$"{section}:PermitLimit"] = SharedHostPermitLimit;
                }
            }

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
            services.RemoveBuiltInLoggingProviders();
            services.DisposeRateLimitersOnStop();
            // OdysseyContext — finance, journal, photos, calendars and contacts in one model — is
            // registered on a fixed in-memory name by DatabaseExtension, so isolate it per factory to
            // avoid cross-test pollution.
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options =>
                options.UseInMemoryDatabase(domainDatabaseName, DatabaseRoot)
                    // EF Core InMemory doesn't support real transactions and throws by default rather
                    // than silently no-opping — ContactVCardService's import path and
                    // UserAdministrationService's delete (issue #354 §6) each open one for atomicity,
                    // which is a genuine no-op here. The actual
                    // transactional guarantee is verified by Odyssey.IntegrationTests against real
                    // MariaDB; this just lets that code path run in the fast InMemory-backed API tests.
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            services.AddSingleton(claimsProvider);
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            if (OwnsFileVolume)
            {
                // The temp volume lives under the temp directory, which check C4 forbids in a deployment, and its real
                // free space is whatever the CI disk has — the reserve check reads the probe instead.
                services.Replace(ServiceDescriptor.Singleton(new FileStorageForbiddenRoots([])));
                services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(FileVolumeSpace));
            }

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

            if (!sharesFileVolume && Directory.Exists(fileVolumePath))
            {
                Directory.Delete(fileVolumePath, recursive: true);
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
    /// Returns a SHARED host to the state a freshly built one starts in, so a test class can boot one host for
    /// all its tests (see <see cref="SharedApiHostClients"/>) and still give every test an empty store. It:
    /// <list type="bullet">
    /// <item><description>restores the default caller's permissions (<see cref="UsePermissions"/>);</description></item>
    /// <item><description>empties the InMemory database and, on a host with a file volume, re-applies <c>HasData</c>
    /// (currencies, roles, settings) the way its startup verification did, keeping only the <c>FileStorageVolumes</c>
    /// rows that verification wrote — the volume registry the host resolved at boot names them. A host without a
    /// volume never ran <c>EnsureCreated</c>, so its store is left empty, as a fresh one's is;</description></item>
    /// <item><description>deletes every file on the temp volume except the volume marker and the store's own
    /// folders, so a file name a previous test used cannot turn the next one's into <c>name (2)</c>;</description></item>
    /// <item><description>clears the host's <see cref="IMemoryCache"/> (settings lookups cache for 30 s), restores
    /// <see cref="FileVolumeSpace"/> and marks every volume available again.</description></item>
    /// </list>
    /// Any other singleton state — a rate-limit partition, a fake clock, a hand-held cache outside
    /// <see cref="IMemoryCache"/>, a captured log, a stub's call counter — survives unless the factory overrides this
    /// method to reset it too, which is why a test that changes such state otherwise keeps a fresh host of its own.
    /// </summary>
    public virtual async Task ResetStoreAsync()
    {
        claimsProvider.Permissions = permissions;

        using (var scope = Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            if (HasFileVolumes)
            {
                // A host with volumes ran EnsureCreated in its startup verification, so it starts seeded.
                var volumes = await context.FileStorageVolumes.AsNoTracking().ToListAsync();
                await context.Database.EnsureDeletedAsync();
                await context.Database.EnsureCreatedAsync();
                context.FileStorageVolumes.AddRange(volumes);
                await context.SaveChangesAsync();
            }
            else
            {
                // One without starts with a store nothing has created: no HasData rows until a test asks for them.
                await context.Database.EnsureDeletedAsync();
            }
        }

        if (Services.GetService<IMemoryCache>() is MemoryCache cache)
        {
            cache.Clear();
        }

        FileVolumeSpace.Override = new TestSpaceProbe().Override;

        // A volume a test drove Degraded (a refused reserve, a vanished file) would otherwise stay so for the next
        // test until the 15-second re-probe; a fresh host starts with every volume available.
        if (Services.GetService<FileStorageVolumeRegistry>() is { } registry)
        {
            foreach (var volume in registry.Volumes)
            {
                registry.MarkAvailable(volume);
            }
        }

        if (OwnsFileVolume)
        {
            ClearVolume(fileVolumePath);
        }
    }

    /// <summary>
    /// Deletes every file on a test volume except the volume marker, <c>lost+found</c> and the store's own folders
    /// (whose contents go), so a file name a previous test used cannot turn the next one's into <c>name (2)</c>. For a
    /// factory with volumes of its own to call from its <see cref="ResetStoreAsync"/> override.
    /// </summary>
    protected static void ClearVolume(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (name is StorageNames.TempFolder or StorageNames.QuarantineFolder)
            {
                foreach (var inner in Directory.EnumerateFileSystemEntries(entry))
                {
                    DeleteEntry(inner);
                }
            }
            else if (name is not (StorageNames.MarkerFileName or StorageNames.LostAndFound))
            {
                DeleteEntry(entry);
            }
        }

        static void DeleteEntry(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
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
