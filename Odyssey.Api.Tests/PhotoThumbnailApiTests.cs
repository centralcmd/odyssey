using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.FileStorage;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #362 §4.3–§4.4 over HTTP: the thumbnail endpoint (headers, conditional requests, one 404 for every miss),
/// its claim gate, the journal-embed readiness rule, and the stats worker state's claim gate.
/// </summary>
public sealed class PhotoThumbnailApiTests
{
    private static readonly byte[] WebP = [.. "RIFF\0\0\0\0WEBPVP8 "u8, 1, 2, 3, 4];

    private static string ThumbnailPath(Guid photoId, string size = "Small") => $"/api/photos/{photoId}/thumbnail?size={size}";

    // ── Endpoint ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_ready_thumbnail_is_served_inline_with_a_strong_etag_and_revalidation_headers()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.PhotosRead]);
        var photoId = await SeedPhotoWithThumbnailsAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ThumbnailPath(photoId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(WebP, await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
    }

    [Fact]
    public async Task Medium_and_small_are_distinct_objects()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.PhotosRead]);
        var photoId = await SeedPhotoWithThumbnailsAsync(factory);
        using var client = factory.CreateClient();

        var small = await client.GetAsync(ThumbnailPath(photoId, "Small"));
        var medium = await client.GetAsync(ThumbnailPath(photoId, "Medium"));

        Assert.Equal(HttpStatusCode.OK, medium.StatusCode);
        Assert.NotEqual(small.Headers.ETag, medium.Headers.ETag);
        Assert.Equal([.. WebP, 2], await medium.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_matching_if_none_match_is_a_304_without_opening_the_file()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.PhotosRead]);
        var photoId = await SeedPhotoWithThumbnailsAsync(factory);
        using var client = factory.CreateClient();
        var etag = (await client.GetAsync(ThumbnailPath(photoId))).Headers.ETag!;

        // The file gone from disk: a 304 still answers, because the conditional path reads metadata only.
        foreach (var file in Directory.EnumerateFiles(factory.VolumeRoot("primary"), "*.webp", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, ThumbnailPath(photoId));
        request.Headers.IfNoneMatch.Add(etag);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unknown_photo_not_ready_and_other_feature_object_are_the_same_404()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.PhotosRead]);
        var ready = await SeedPhotoWithThumbnailsAsync(factory);
        var notReady = await SeedPhotoAsync(factory);
        var foreign = await SeedPhotoAsync(factory);
        // A thumbnail row pointing at another feature's object (a hand edit) never serves it.
        await WithScopeAsync(factory, async (context, store) =>
        {
            using var bytes = new MemoryStream(WebP);
            var other = await store.PutAsync("TestFiles", Guid.NewGuid(), "x.jpg", null, "image/jpeg", bytes, 1024, CancellationToken.None);
            context.PhotoThumbnails.Add(Thumbnail(foreign, ThumbnailSize.Small, other.Id));
            await context.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        var bodies = new List<string>();
        foreach (var path in new[] { ThumbnailPath(Guid.NewGuid()), ThumbnailPath(notReady), ThumbnailPath(foreign), ThumbnailPath(notReady, "Medium") })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Everything but the per-request trace id: no field may tell the three cases apart.
            bodies.Add(string.Join("|", problem.RootElement.EnumerateObject()
                .Where(property => property.Name != "traceId")
                .Select(property => $"{property.Name}={property.Value}")));
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ThumbnailPath(ready))).StatusCode);
        Assert.Single(bodies.Distinct());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?size=Large")]
    [InlineData("?size=0")]
    [InlineData("?size=3")]
    public async Task A_missing_or_unknown_size_is_400(string query)
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.PhotosRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/photos/{Guid.NewGuid()}/thumbnail{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_needs_photos_read()
    {
        await using (var anonymous = FileStorageApiFactory.Create(null))
        {
            using var client = anonymous.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ThumbnailPath(Guid.NewGuid()))).StatusCode);
        }

        await using (var unclaimed = FileStorageApiFactory.Create([PermissionClaims.FilesRead, PermissionClaims.JournalRead]))
        {
            using var client = unclaimed.CreateClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(ThumbnailPath(Guid.NewGuid()))).StatusCode);
        }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_carry_the_worker_state_only_for_a_settings_reader()
    {
        await using (var admin = new OdysseyApiFactory([PermissionClaims.PhotosRead, PermissionClaims.SystemSettingsRead]))
        {
            using var client = admin.CreateClient();
            var stats = await client.GetFromJsonAsync<PhotoLibraryStats>("/api/photos/stats");
            Assert.Equal(ThumbnailWorkerState.NotRunning, stats!.ThumbnailWorkerState);
        }

        await using (var user = new OdysseyApiFactory([PermissionClaims.PhotosRead]))
        {
            using var client = user.CreateClient();
            var stats = await client.GetFromJsonAsync<PhotoLibraryStats>("/api/photos/stats");
            Assert.Null(stats!.ThumbnailWorkerState);
        }
    }

    // ── Journal embed rule (§4.4) ──────────────────────────────────────────────

    private const string JournalPath = "/api/journal-entries";

    private static readonly string[] JournalClaims =
    [
        PermissionClaims.JournalCreate, PermissionClaims.JournalRead, PermissionClaims.JournalUpdate, PermissionClaims.FilesRead,
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_journal_photo_is_thumbnail_ready_only_for_a_photos_reader_on_every_action(bool photosRead)
    {
        await using var factory = new JournalFactory(photosRead ? [.. JournalClaims, PermissionClaims.PhotosRead] : JournalClaims);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, JournalFactory.Actor);
        using var client = factory.CreateClient();

        // The first entry creates the library photo; its thumbnails are then made ready by hand.
        var first = await PostEntryAsync(client, fileId);
        var photoId = Assert.Single(first.Photos).PhotoId;
        Assert.False(first.Photos[0].ThumbnailsReady);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            // A photo the journal path creates is queued in the same save, in the uploader's interactive lane.
            var job = await context.PhotoThumbnailJobs.SingleAsync(j => j.PhotoId == photoId);
            Assert.Equal((PhotoThumbnailJobState.Pending, PhotoThumbnailJobPriority.Interactive), (job.State, job.Priority));
            context.PhotoThumbnails.AddRange(Thumbnail(photoId, ThumbnailSize.Small, Guid.NewGuid()), Thumbnail(photoId, ThumbnailSize.Medium, Guid.NewGuid()));
            await context.SaveChangesAsync();
        }

        var created = await PostEntryAsync(client, fileId);
        var detail = await client.GetFromJsonAsync<ExistingJournalEntry>($"{JournalPath}/{created.JournalEntryId}");
        var list = await client.GetPagedItemsAsync<ExistingJournalEntry>(JournalPath);
        var put = await client.PutAsJsonAsync($"{JournalPath}/{created.JournalEntryId}", new UpdateJournalEntry
        {
            Title = "Renamed",
            Content = "Still with a picture.",
            EntryDate = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
            PhotoFileIds = [fileId],
        });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;

        Assert.Equal(photosRead, Assert.Single(created.Photos).ThumbnailsReady);
        Assert.Equal(photosRead, Assert.Single(detail!.Photos).ThumbnailsReady);
        Assert.All(list!.SelectMany(e => e.Photos), photo => Assert.Equal(photosRead, photo.ThumbnailsReady));
        Assert.Equal(photosRead, Assert.Single(updated.Photos).ThumbnailsReady);
    }

    private static async Task<ExistingJournalEntry> PostEntryAsync(HttpClient client, Guid fileId)
    {
        var post = await client.PostAsJsonAsync(JournalPath, new NewJournalEntry
        {
            Title = "With a photo",
            Content = "Dear journal, today had a picture.",
            EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PhotoFileIds = [fileId],
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
    }

    private sealed class JournalFactory(IReadOnlyCollection<string> permissions, Action<IServiceCollection>? extra = null)
        : OdysseyApiFactory(permissions, Actor, configureServices: services => { Isolate(services); extra?.Invoke(services); })
    {
        public const string Actor = "thumbnail-journal-actor";

        private static void Isolate(IServiceCollection services)
        {
            var databaseName = $"thumbnails-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }

    // ── Rename busy over HTTP (AC 17) ─────────────────────────────────────────────

    [Fact]
    public async Task A_rename_that_keeps_losing_to_a_worker_write_is_a_409_with_retry_after()
    {
        await using var factory = new JournalFactory(
            [PermissionClaims.FilesRead, PermissionClaims.FilesUpdate],
            services => services.AddScoped<IFileRenameObserver, AlwaysInterfering>());
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, JournalFactory.Actor, "Before.jpg");
        var photoId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Photos.Add(new Photo { PhotoId = photoId, FileId = fileId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            context.PhotoThumbnailJobs.Add(new PhotoThumbnailJobEntry
            {
                PhotoId = photoId, State = PhotoThumbnailJobState.Done, NextAttemptAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/files/{fileId}/metadata", new Odyssey.Dtos.Finance.UpdateFileMetadataRequest { FileName = "After.jpg" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("rename-busy", problem.RootElement.ToString(), StringComparison.Ordinal);
        // That the name is kept is a property of one atomic save, which EF InMemory does not have; it is asserted on
        // MariaDB (PhotoThumbnailRelationalTests and the rename scenarios rerun in Odyssey.IntegrationTests).
    }

    /// <summary>A worker write that lands between every load and save of the rename (a separate context, so a real token conflict).</summary>
    private sealed class AlwaysInterfering(IServiceScopeFactory scopes) : IFileRenameObserver
    {
        public async Task OnFileRenamingAsync(Guid fileId, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var photoId = await context.Photos.Where(p => p.FileId == fileId).Select(p => p.PhotoId).SingleAsync(cancellationToken);
            var job = await context.PhotoThumbnailJobs.SingleAsync(j => j.PhotoId == photoId, cancellationToken);
            job.Generation++;
            await context.SaveChangesAsync(cancellationToken);
        }

        public bool Owns(Type entityType) => false;
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_api_resolves_the_rename_observer_and_hosts_no_thumbnail_job()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.PhotosRead]);
        using var scope = factory.Services.CreateScope();

        Assert.Contains(scope.ServiceProvider.GetServices<IFileRenameObserver>(), o => o is PhotoThumbnailRenameObserver);
        Assert.Null(factory.Services.GetService<PhotoThumbnailJob>());
        Assert.Null(factory.Services.GetService<IPhotoThumbnailRenderer>());
        Assert.DoesNotContain(
            factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service.GetType().FullName!.Contains("Thumbnail", StringComparison.Ordinal));
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private static PhotoThumbnail Thumbnail(Guid photoId, ThumbnailSize size, Guid storedObjectId) => new()
    {
        PhotoThumbnailId = Guid.NewGuid(),
        PhotoId = photoId,
        Size = size,
        StoredObjectId = storedObjectId,
        PixelWidth = 200,
        PixelHeight = 150,
        RecipeVersion = PhotoThumbnailRecipe.Version,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task WithScopeAsync(WebApplicationFactory<Program> factory, Func<OdysseyContext, IObjectStore, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), scope.ServiceProvider.GetRequiredService<IObjectStore>());
    }

    private static async Task<Guid> SeedPhotoAsync(WebApplicationFactory<Program> factory)
    {
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, "thumbnail-uploader");
        var photoId = Guid.NewGuid();
        await WithScopeAsync(factory, async (context, _) =>
        {
            context.Photos.Add(new Photo { PhotoId = photoId, FileId = fileId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        });
        return photoId;
    }

    private static async Task<Guid> SeedPhotoWithThumbnailsAsync(WebApplicationFactory<Program> factory)
    {
        var photoId = await SeedPhotoAsync(factory);
        await WithScopeAsync(factory, async (context, store) =>
        {
            foreach (var (size, bytes) in new[] { (ThumbnailSize.Small, WebP), (ThumbnailSize.Medium, [.. WebP, 2]) })
            {
                using var stream = new MemoryStream(bytes);
                var stored = await store.PutAsync(
                    PhotoThumbnailStorage.FeatureKey, new PhotoThumbnailLayout(size, 1), "holiday.jpg", null, "image/webp",
                    stream, PhotoThumbnailRecipe.MaxOutputBytes, CancellationToken.None);
                context.PhotoThumbnails.Add(Thumbnail(photoId, size, stored.Id));
            }

            await context.SaveChangesAsync();
        });
        return photoId;
    }
}
