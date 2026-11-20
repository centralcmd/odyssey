// EF1002 flags interpolation into ExecuteSqlRawAsync; the only value interpolated is this class's own constant database name.
#pragma warning disable EF1002

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;
using Odyssey.MigrationService;
using Odyssey.Testing.FileStorage;
using Odyssey.TestData;
using Odyssey.Worker.DemoData;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Both halves of the demo seed on real MariaDB, in production order: the migrations job's <see cref="DemoDataSeeder"/>
/// (everything without bytes), then the worker's <see cref="DemoFileSeeder"/> (every file and photo, and what names
/// them) over a temp volume. Every link is a real foreign key here, which EF InMemory does not check, and a second pass
/// — the shape of a restart or a second replica — must insert nothing.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class DemoSeedRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_demo_seed";

    [SkippableFact]
    public async Task The_two_seeders_build_the_whole_demo_set_with_every_key_held_and_a_rerun_adds_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connectionString = await fixture.CreateMigratedDatabaseAsync(Database);
        var volume = Directory.CreateTempSubdirectory("odyssey-it-demo-");

        try
        {
            await using (var provider = SeederProvider(connectionString))
            {
                await new DemoDataSeeder(provider, Configuration(), new TestingEnvironment(),
                    provider.GetRequiredService<ILogger<DemoDataSeeder>>()).ExecuteAsync(CancellationToken.None);
            }

            await using var host = FileStorageTestHost.Create(
                options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)),
                new Dictionary<string, string> { ["primary"] = volume.FullName },
                StoredFileAreas.All.Select(f => f.Key).Append(PhotoOriginalStorage.FeatureKey).ToDictionary(k => k, _ => "primary"),
                features: [],
                configureServices: services =>
                {
                    services.AddStoredFiles();
                    services.AddPhotoOriginals();
                    services.AddSingleton<DemoFileSeeder>();
                });
            await host.VerifyAsync();
            var seeder = host.Services.GetRequiredService<DemoFileSeeder>();

            await seeder.SeedAsync(DemoDataSet.Build(), CancellationToken.None);
            var first = await CountsAsync(connectionString);
            await seeder.SeedAsync(DemoDataSet.Build(), CancellationToken.None);

            var expected = DemoDataSet.Build();
            Assert.Equal(
                (expected.Files.Count, expected.Photos.Count, expected.TaxStatementFiles.Count, expected.JournalEntryAttachments.Count,
                    expected.JournalTaskAttachments.Count, expected.JournalEntryPhotos.Count, expected.PhotoAlbums.Count,
                    expected.ContactAvatars.Count, expected.Files.Count + expected.Photos.Count),
                first);
            Assert.Equal(first, await CountsAsync(connectionString));
        }
        finally
        {
            volume.Delete(recursive: true);
            await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)).Options);
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        }
    }

    private static async Task<(int, int, int, int, int, int, int, int, int)> CountsAsync(string connectionString)
    {
        await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)).Options);
        return (await context.FileMetadata.CountAsync(), await context.Photos.CountAsync(), await context.TaxStatementFiles.CountAsync(),
            await context.JournalEntryAttachments.CountAsync(), await context.JournalTaskAttachments.CountAsync(),
            await context.JournalEntryPhotos.CountAsync(), await context.PhotoAlbums.CountAsync(),
            await context.Contacts.CountAsync(c => c.AvatarFileId != null),
            await context.StoredObjects.CountAsync(o => o.State == StoredObjectState.Committed));
    }

    private static ServiceProvider SeederProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OdysseyContext>(options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OdysseyContext>();
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:DemoData"] = "true" }).Build();

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Odyssey.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
