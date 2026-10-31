using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// One InMemory database and one temp folder per test; <see cref="NewHostAsync"/> starts a "process" of the store
/// over them, verified as the API verifies, so two calls are a restart.
/// </summary>
public abstract class FileStorageTestBase : IAsyncLifetime
{
    /// <summary>One store root for the assembly, so EF builds one internal provider rather than one per test.</summary>
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly List<FileStorageTestHost> hosts = [];

    protected string DatabaseName { get; } = $"file-storage-{Guid.NewGuid():N}";

    protected DirectoryInfo Base { get; } = Directory.CreateTempSubdirectory("odyssey-fs-");

    protected TestClock Clock { get; } = new();

    /// <summary>A volume root that exists (the verifier never creates one).</summary>
    protected string Root(string name)
    {
        var path = Path.Combine(Base.FullName, "volumes", name);
        Directory.CreateDirectory(path);
        return path;
    }

    protected void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseInMemoryDatabase(DatabaseName, DatabaseRoot)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    /// <summary>The usual shape: one volume, <c>primary</c>, with every test feature bound to it.</summary>
    protected Task<FileStorageTestHost> NewHostAsync() =>
        NewHostAsync(new Dictionary<string, string> { ["primary"] = Root("primary") },
            TestStorageFeatures.All.ToDictionary(f => f.Key, _ => "primary"));

    protected async Task<FileStorageTestHost> NewHostAsync(
        IReadOnlyDictionary<string, string> volumes,
        IReadOnlyDictionary<string, string> bindings,
        IEnumerable<StorageFeature>? features = null,
        FileStorageForbiddenRoots? forbiddenRoots = null,
        bool verify = true)
    {
        var host = FileStorageTestHost.Create(UseDatabase, volumes, bindings, features, Clock, forbiddenRoots);
        hosts.Add(host);
        if (verify)
        {
            await host.VerifyAsync();
        }

        return host;
    }

    protected string PrimaryRoot => Path.Combine(Base.FullName, "volumes", "primary");

    protected static MemoryStream Bytes(int length, byte seed = 7)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i * 31);
        }

        return new MemoryStream(bytes);
    }

    /// <summary>Puts one object of <paramref name="feature"/> and commits it with a reference row, as a consumer does.</summary>
    protected static async Task<StoredObject> PutCommittedAsync(
        FileStorageTestHost host, StorageFeature feature, object layoutArgs, string name = "IMG_2041.jpg",
        string? suffix = null, int length = 1024, string contentType = "image/jpeg")
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await store.PutAsync(feature.Key, layoutArgs, name, suffix, contentType, Bytes(length), 64L * 1024 * 1024, default);
        AddReference(context, feature, row.Id);
        await context.SaveChangesAsync();
        return row;
    }

    protected static void AddReference(TestStorageContext context, StorageFeature feature, Guid storedObjectId)
    {
        if (feature == TestStorageFeatures.Secrets)
        {
            context.TestSecretAttachments.Add(new TestSecretAttachment { Id = Guid.NewGuid(), StoredObjectId = storedObjectId });
        }
        else if (feature == TestStorageFeatures.Dated)
        {
            context.TestDatedAttachments.Add(new TestDatedAttachment { Id = Guid.NewGuid(), StoredObjectId = storedObjectId });
        }
        else
        {
            context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = storedObjectId });
        }
    }

    protected static async Task<StoredObject?> RowAsync(FileStorageTestHost host, Guid id)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == id);
    }

    protected static async Task<T> InScopeAsync<T>(FileStorageTestHost host, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = host.Scope();
        return await action(scope.ServiceProvider);
    }

    protected static async Task InScopeAsync(FileStorageTestHost host, Func<IServiceProvider, Task> action)
    {
        await using var scope = host.Scope();
        await action(scope.ServiceProvider);
    }

    protected static Task<int> SweepAsync(FileStorageTestHost host, Func<FileStorageSweeper, Task<int>> sweep) =>
        InScopeAsync(host, sp => sweep(sp.GetRequiredService<FileStorageSweeper>()));

    protected static Task RunTickAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<FileStorageSweeper>().RunAsync(null, default));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }

        MakeWritable(Base);
        Base.Delete(recursive: true);
    }

    private static void MakeWritable(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var sub in directory.EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.LinkTarget is null))
        {
            sub.UnixFileMode |= UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.UserRead;
        }
    }
}
