using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;

namespace Odyssey.Testing.FileStorage;

/// <summary>
/// The Files store's write and read path (issue #448) over a temp folder, for a test that builds its services by hand over
/// an existing context — so an upload or a contact image writes a real file, as it does in production. The store works
/// on <paramref name="context"/>'s database: reservations commit through a second context of the same options.
/// </summary>
public sealed class TestFileStore : IDisposable
{
    private readonly string root;

    private TestFileStore(OdysseyContext context)
    {
        root = Path.Combine(Path.GetTempPath(), $"odyssey-test-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, StorageNames.MarkerFileName), "{}");

        var options = (DbContextOptions<OdysseyContext>)context.GetService<IDbContextOptions>();
        var factory = new OptionsContextFactory(options);
        // A volume row, so a reservation's key to FileStorageVolumes holds on a real database too.
        var volumeId = Guid.NewGuid();
        var volumeName = $"t{volumeId:N}"[..32];
        using (var setup = factory.CreateDbContext())
        {
            setup.Set<FileStorageVolume>().Add(new FileStorageVolume { Id = volumeId, Name = volumeName, LastSeenAtUtc = DateTime.UtcNow });
            setup.SaveChanges();
        }

        var catalogue = new StorageFeatureCatalogue(StoredFileAreas.All);
        var volumes = new FileStorageVolumeRegistry(NullLogger<FileStorageVolumeRegistry>.Instance, TimeProvider.System);
        volumes.Activate(
            [(volumeId, volumeName, root)],
            StoredFileAreas.All.ToDictionary(f => f.Key, _ => volumeName, StringComparer.OrdinalIgnoreCase));
        Space = new TestSpaceProbe();
        Store = new ObjectStore(
            context, factory, catalogue, volumes, Space, Options.Create(new FileStorageOptions()), TimeProvider.System,
            NullLogger<ObjectStore>.Instance);
        Writer = new StoredFileWriter(
            Store, catalogue, volumes, Space,
            new StoredFileBucketAllocator(factory, Options.Create(new StoredFileOptions()), TimeProvider.System),
            TimeProvider.System);
        Source = new StoredFileContentSource(context, Store);
    }

    public static TestFileStore For(OdysseyContext context) => new(context);

    public IObjectStore Store { get; }

    public StoredFileWriter Writer { get; }

    public StoredFileContentSource Source { get; }

    /// <summary>The free space the volume reports; set it to exercise the upload reserve.</summary>
    public TestSpaceProbe Space { get; }

    /// <summary>The temp volume's root, for a test that inspects the files written.</summary>
    public string Root => root;

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the OS temp folder is cleaned eventually.
        }
    }

    private sealed class OptionsContextFactory(DbContextOptions<OdysseyContext> options) : IDbContextFactory<OdysseyContext>
    {
        public OdysseyContext CreateDbContext() => new(options);
    }
}

/// <summary>One <see cref="TestFileStore"/> per context, so a test's hand-built services share one temp volume.</summary>
public static class TestFileStores
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OdysseyContext, TestFileStore> Stores = new();

    public static TestFileStore For(OdysseyContext context) => Stores.GetValue(context, TestFileStore.For);
}
