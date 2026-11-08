using Odyssey.Core.Journal.Originals;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Tests.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// One store "process" with the thumbnail feature bound to <c>primary</c>, the test features beside it, a fake
/// renderer and self-test, and the real extractor, reader and object store (issue #362 test tiers).
/// </summary>
public abstract class PhotoThumbnailTestBase : FileStorageTestBase
{
    /// <summary>True where the scenarios rerun on real MariaDB, so a scenario can assert what only a real save shows.</summary>
    protected virtual bool RelationalDatabase => false;

    /// <summary>Creates the uploader where the database enforces the user key; a no-op on EF InMemory.</summary>
    protected virtual Task EnsureUserAsync(OdysseyContext context, string userId) => Task.CompletedTask;

    protected FakeRenderer Renderer { get; } = new();

    protected FakeSelfTest SelfTest { get; } = new();

    protected FakeLifetime Lifetime { get; } = new();

    protected List<int> Exits { get; } = [];

    protected Task<FileStorageTestHost> NewThumbnailHostAsync(
        int bucketCapacity = PhotoThumbnailRecipe.BucketCapacity,
        string workerName = "w1",
        Action<IServiceCollection>? configure = null) =>
        NewHostAsync(
            new Dictionary<string, string> { ["primary"] = Root("primary") },
            TestStorageFeatures.All.Select(f => f.Key).Append(PhotoThumbnailStorage.FeatureKey).Append(PhotoOriginalStorage.FeatureKey)
                .ToDictionary(k => k, _ => "primary"),
            TestStorageFeatures.All, // the thumbnail feature comes from AddPhotoThumbnails, as in production
            configureServices: services =>
            {
                services.AddPhotoThumbnails();
                services.AddPhotoOriginals();
                services.Configure<PhotoThumbnailOptions>(o => o.BucketCapacity = bucketCapacity);
                services.Replace(ServiceDescriptor.Singleton(new PhotoThumbnailWorkerIdentity(workerName)));
                services.Replace(ServiceDescriptor.Singleton(new PhotoThumbnailProcessHooks { Exit = Exits.Add }));
                services.AddSingleton<IPhotoThumbnailRenderer>(Renderer);
                services.AddSingleton<IThumbnailDecoderSelfTest>(SelfTest);
                services.AddSingleton<IHostApplicationLifetime>(Lifetime);
                services.AddSingleton<PhotoThumbnailJob>();
                configure?.Invoke(services);
            });

    protected static PhotoThumbnailJob Job(FileStorageTestHost host) => host.Services.GetRequiredService<PhotoThumbnailJob>();

    /// <summary>A photo wrapping a file with these bytes, plus its <c>Pending</c> job as <c>PhotoService.Create</c> stages it.</summary>
    protected async Task<Guid> AddPhotoAsync(
        FileStorageTestHost host, byte[] bytes, string contentType = "image/jpeg", string fileName = "IMG_2041.jpg",
        string uploader = "u1", bool archived = false, bool withJob = true)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await EnsureUserAsync(context, uploader);
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(), FileName = fileName, ContentType = contentType, SizeBytes = bytes.LongLength,
            Sha256Hash = "h", FileBlobId = blob.Id, UploadedAtUtc = Clock.GetUtcNow().UtcDateTime,
        };
        var photo = new Photo
        {
            PhotoId = Guid.NewGuid(), FileId = file.Id, CreatedByUserId = uploader,
            CreatedAt = Clock.GetUtcNow().UtcDateTime, UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            Archived = archived ? Clock.GetUtcNow().UtcDateTime : null,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(file);
        context.Photos.Add(photo);
        if (withJob)
        {
            await scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>().StageEnqueueAsync(photo, default);
        }

        await context.SaveChangesAsync();
        return photo.PhotoId;
    }

    protected static async Task<PhotoThumbnailJobEntry> JobRowAsync(FileStorageTestHost host, Guid photoId)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs.AsNoTracking().SingleAsync(j => j.PhotoId == photoId);
    }

    protected static async Task<List<PhotoThumbnail>> ThumbnailsAsync(FileStorageTestHost host, Guid photoId)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnails.AsNoTracking()
            .Where(t => t.PhotoId == photoId).OrderBy(t => t.Size).ToListAsync();
    }

    protected static async Task<StoredObject> StoredAsync(FileStorageTestHost host, Guid storedObjectId)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.AsNoTracking().SingleAsync(o => o.Id == storedObjectId);
    }

    protected static async Task<T> WithContextAsync<T>(FileStorageTestHost host, Func<OdysseyContext, Task<T>> action)
    {
        await using var scope = host.Scope();
        return await action(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    protected static async Task WithContextAsync(FileStorageTestHost host, Func<OdysseyContext, Task> action)
    {
        await using var scope = host.Scope();
        await action(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    /// <summary>Waits for a condition the job reaches on another thread, in real time, bounded.</summary>
    protected static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>A renderer that records each request and answers as told: rendered, a failure code, a throw or a hang.</summary>
    public sealed class FakeRenderer : IPhotoThumbnailRenderer
    {
        public ConcurrentQueue<ThumbnailRenderRequest> Calls { get; } = new();

        public Func<ThumbnailRenderRequest, ThumbnailRenderResult>? Behaviour { get; set; }

        public ManualResetEventSlim? Hang { get; set; }

        public ManualResetEventSlim Entered { get; } = new();

        public ThumbnailRenderResult Render(ThumbnailRenderRequest request)
        {
            Calls.Enqueue(request);
            Entered.Set();
            Hang?.Wait();
            if (Behaviour is not null)
            {
                return Behaviour(request);
            }

            // Medium and Small, portrait for orientations 5–8 (the real renderer's contract).
            var portrait = request.Orientation is >= 5 and <= 8;
            return ThumbnailRenderResult.Rendered(
                new ThumbnailRendition([0x52, 0x49, 0x46, 0x46, 1], portrait ? 300 : 400, portrait ? 400 : 300),
                new ThumbnailRendition([0x52, 0x49, 0x46, 0x46, 2], portrait ? 150 : 200, portrait ? 200 : 150));
        }
    }

    public sealed class FakeSelfTest : IThumbnailDecoderSelfTest
    {
        public Func<ThumbnailSelfTestResult> Behaviour { get; set; } = () => ThumbnailSelfTestResult.Passed;

        public ThumbnailSelfTestResult Run() => Behaviour();
    }

    public sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopCalls++;

        public void Stop() => stopping.Cancel();

        public void Dispose() => stopping.Dispose();
    }
}
