using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;
using Odyssey.Testing.FileStorage;
using Odyssey.TestData;
using Odyssey.Worker.DemoData;
using Xunit;

namespace Odyssey.Worker.Tests;

/// <summary>
/// The half of the demo seed with bytes (<see cref="DemoFileSeeder"/>): every demo file and photo lands on the store
/// with the rows that name it, a second pass — or a second replica — adds nothing, and a record the migrations job did
/// not seed gets no file. EF InMemory and a temp volume; the real-engine run is in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public sealed class DemoFileSeederTests : IAsyncLifetime
{
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"demo-files-{Guid.NewGuid():N}";
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("odyssey-demo-files-");
    private readonly List<FileStorageTestHost> hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }

        root.Delete(recursive: true);
    }

    [Fact]
    public async Task Every_demo_file_and_photo_lands_on_the_store_with_the_rows_that_name_it()
    {
        var host = await NewHostAsync();
        var expected = DemoDataSet.Build();
        await SeedPrincipalsAsync(host, expected);

        await Seeder(host).SeedAsync(DemoDataSet.Build(), default);

        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(expected.Files.Count, await context.FileMetadata.CountAsync());
        Assert.Equal(expected.Photos.Count, await context.Photos.CountAsync());
        Assert.Equal(expected.Files.Count + expected.Photos.Count, await context.StoredObjects.CountAsync(o => o.State == StoredObjectState.Committed));
        foreach (var file in expected.Files)
        {
            var row = await context.FileMetadata.SingleAsync(f => f.Id == file.Metadata.Id);
            Assert.Equal(file.Area, StoredFileAreas.AreaOf(row));
            Assert.Equal(file.Content, await ReadAsync(scope.ServiceProvider, row));
        }

        foreach (var photo in expected.Photos)
        {
            var row = await context.Photos.SingleAsync(p => p.PhotoId == photo.Photo.PhotoId);
            Assert.Equal((photo.Photo.FileName, photo.Photo.Sha256Hash), (row.FileName, row.Sha256Hash));
            var original = await scope.ServiceProvider.GetRequiredService<PhotoOriginalSource>().ReadAllAsync(row.PhotoId, long.MaxValue, default);
            Assert.Equal(photo.Content, original!.Bytes);
        }

        Assert.Equal(expected.TaxStatementFiles.Count, await context.TaxStatementFiles.CountAsync());
        Assert.Equal(expected.JournalEntryAttachments.Count, await context.JournalEntryAttachments.CountAsync());
        Assert.Equal(expected.JournalTaskAttachments.Count, await context.JournalTaskAttachments.CountAsync());
        Assert.Equal(expected.JournalEntryPhotos.Count, await context.JournalEntryPhotos.CountAsync());
        Assert.Equal(expected.PhotoTags.Count, await context.PhotoTags.CountAsync());
        Assert.Equal(expected.PhotoTagLinks.Count, await context.PhotoTagLinks.CountAsync());
        Assert.Equal(expected.PhotoPeople.Count, await context.PhotoPeople.CountAsync());
        Assert.Equal(expected.PhotoAlbums.Count, await context.PhotoAlbums.CountAsync());
        Assert.Equal(expected.PhotoAlbumItems.Count, await context.PhotoAlbumItems.CountAsync());
        foreach (var avatar in expected.ContactAvatars)
        {
            Assert.Equal(avatar.File.Metadata.Id, (await context.Contacts.SingleAsync(c => c.ContactId == avatar.ContactId)).AvatarFileId);
        }
    }

    [Fact]
    public async Task A_second_pass_and_a_second_replica_add_nothing()
    {
        var first = await NewHostAsync();
        await SeedPrincipalsAsync(first, DemoDataSet.Build());
        await Seeder(first).SeedAsync(DemoDataSet.Build(), default);
        var before = await CountsAsync(first);

        await Seeder(first).SeedAsync(DemoDataSet.Build(), default);
        var replica = await NewHostAsync();
        await Seeder(replica).SeedAsync(DemoDataSet.Build(), default);

        Assert.Equal(before, await CountsAsync(first));
    }

    /// <summary>A database seeded while photos still wrapped a file: its demo photos are recognised by that file's id.</summary>
    [Fact]
    public async Task A_demo_photo_already_moved_under_another_id_is_recognised_by_its_former_file()
    {
        var host = await NewHostAsync();
        var data = DemoDataSet.Build();
        await SeedPrincipalsAsync(host, data);
        var moved = data.Photos[0];
        await using (var scope = host.Scope())
        {
            var photo = await StoredFileFixtures.StagePhotoAsync(scope.ServiceProvider, moved.Content, moved.Photo.FileName, "image/png");
            photo.LegacyFileId = moved.FormerFileId;
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        }

        await Seeder(host).SeedAsync(DemoDataSet.Build(), default);

        await using var check = host.Scope();
        var context = check.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(data.Photos.Count, await context.Photos.CountAsync());
        Assert.False(await context.Photos.AnyAsync(p => p.PhotoId == moved.Photo.PhotoId));
    }

    /// <summary>The migrations job's seed was off, or skipped a record: nothing it would have named gets a file.</summary>
    [Fact]
    public async Task A_file_whose_record_was_never_seeded_is_not_written()
    {
        var host = await NewHostAsync();

        await Seeder(host).SeedAsync(DemoDataSet.Build(), default);

        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await context.FileMetadata.CountAsync());
        Assert.Equal(0, await context.JournalEntryPhotos.CountAsync());
        Assert.Equal(0, await context.PhotoPeople.CountAsync());
        Assert.Equal(DemoDataSet.Build().Photos.Count, await context.Photos.CountAsync());
    }

    /// <summary>
    /// Link rows an older generator wrote under today's surrogate key but a different natural key: the key is what the
    /// database enforces, so they are skipped rather than re-inserted onto a duplicate primary key.
    /// </summary>
    [Fact]
    public async Task A_link_row_whose_surrogate_key_survived_a_changed_natural_key_is_not_reinserted()
    {
        var host = await NewHostAsync();
        var data = DemoDataSet.Build();
        await SeedPrincipalsAsync(host, data);
        await Seeder(host).SeedAsync(DemoDataSet.Build(), default);
        await using (var scope = host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            foreach (var person in context.PhotoPeople)
            {
                person.ContactId = Guid.NewGuid();
            }

            foreach (var link in context.PhotoTagLinks)
            {
                link.PhotoTagId = Guid.NewGuid();
            }

            foreach (var item in context.PhotoAlbumItems)
            {
                item.PhotoId = Guid.NewGuid();
            }

            await context.SaveChangesAsync();
        }

        await Seeder(host).SeedAsync(DemoDataSet.Build(), default);

        await using var check = host.Scope();
        var after = check.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(data.PhotoPeople.Count, await after.PhotoPeople.CountAsync());
        Assert.Equal(data.PhotoTagLinks.Count, await after.PhotoTagLinks.CountAsync());
        Assert.Equal(data.PhotoAlbumItems.Count, await after.PhotoAlbumItems.CountAsync());
    }

    private async Task<FileStorageTestHost> NewHostAsync()
    {
        var primary = Path.Combine(root.FullName, "primary");
        Directory.CreateDirectory(primary);
        var host = FileStorageTestHost.Create(
            options => options.UseInMemoryDatabase(databaseName, DatabaseRoot)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
            new Dictionary<string, string> { ["primary"] = primary },
            StoredFileAreas.All.Select(f => f.Key).Append(PhotoOriginalStorage.FeatureKey).ToDictionary(k => k, _ => "primary"),
            features: [],
            configureServices: services =>
            {
                services.AddStoredFiles();
                services.AddPhotoOriginals();
                services.AddSingleton<DemoFileSeeder>();
            });
        hosts.Add(host);
        await host.VerifyAsync();
        return host;
    }

    private static DemoFileSeeder Seeder(FileStorageTestHost host) => host.Services.GetRequiredService<DemoFileSeeder>();

    /// <summary>The records the migrations job seeds and the demo files attach to (EF InMemory enforces no keys).</summary>
    private static async Task SeedPrincipalsAsync(FileStorageTestHost host, DemoDataSet data)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Contacts.AddRange(data.Contacts);
        context.TaxStatements.AddRange(data.TaxStatements);
        context.JournalEntries.AddRange(data.JournalEntries);
        context.JournalTasks.AddRange(data.JournalTasks);
        await context.SaveChangesAsync();
    }

    private static async Task<(int, int, int, int, int, int)> CountsAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await context.FileMetadata.CountAsync(), await context.Photos.CountAsync(),
            await context.StoredObjects.CountAsync(o => o.State == StoredObjectState.Committed),
            await context.TaxStatementFiles.CountAsync() + await context.JournalEntryAttachments.CountAsync() + await context.JournalTaskAttachments.CountAsync(),
            await context.JournalEntryPhotos.CountAsync() + await context.PhotoTagLinks.CountAsync() + await context.PhotoPeople.CountAsync(),
            await context.PhotoTags.CountAsync() + await context.PhotoAlbums.CountAsync() + await context.PhotoAlbumItems.CountAsync());
    }

    private static async Task<byte[]> ReadAsync(IServiceProvider scope, FileMetadata row)
    {
        var content = await scope.GetRequiredService<StoredFileContentSource>().OpenAsync(row.Id, default);
        using var buffer = new MemoryStream();
        await using (var stream = content!.Content)
        {
            await stream.CopyToAsync(buffer);
        }

        return buffer.ToArray();
    }
}
