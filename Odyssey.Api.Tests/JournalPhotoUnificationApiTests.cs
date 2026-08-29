using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>Journal photo unification acceptance criteria (issue #321 v4, §18a–f).</summary>
public class JournalPhotoUnificationApiTests
{
    private const string ActorUserId = "journal-unify-actor-id";
    private const string JournalPath = "/api/journal-entries";

    private static readonly string[] JournalUserNoPhotos =
    [
        // A user who can add a journal photo today, but holds NO photos.* claim (criterion #18b).
        PermissionClaims.JournalCreate, PermissionClaims.JournalRead, PermissionClaims.JournalUpdate,
        PermissionClaims.JournalDelete, PermissionClaims.FilesRead,
    ];

    private static readonly string[] JournalAndPhotos =
    [
        PermissionClaims.JournalCreate, PermissionClaims.JournalRead, PermissionClaims.JournalUpdate,
        PermissionClaims.JournalDelete, PermissionClaims.FilesRead,
        PermissionClaims.PhotosRead, PermissionClaims.PhotosDelete,
    ];

    [Fact]
    public async Task JournalAdd_CreatesLibraryPhoto_AppearingOnTheGrid()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var entry = await CreateEntryWithPhoto(client, fileId);
        var photo = Assert.Single(entry.Photos);
        Assert.Equal(fileId, photo.FileId);
        Assert.NotEqual(Guid.Empty, photo.PhotoId);

        // The transparently-created library Photo is discoverable on /api/photos (criterion #18a).
        var grid = await client.GetPagedItemsAsync<PhotoSummary>("/api/photos");
        Assert.Contains(grid!, p => p.PhotoId == photo.PhotoId && p.FileId == fileId);
    }

    [Fact]
    public async Task JournalUser_WithoutAnyPhotoClaim_CanStillAddJournalPhoto()
    {
        await using var factory = new ApiFactory(JournalUserNoPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var entry = await CreateEntryWithPhoto(client, fileId);
        var photo = Assert.Single(entry.Photos);
        Assert.Equal(fileId, photo.FileId);

        // The library Photo was created transparently even though the caller holds no photos.* claim.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Photos.AnyAsync(p => p.FileId == fileId));
    }

    [Fact]
    public async Task SameFile_InTwoEntries_ProducesExactlyOneLibraryPhoto()
    {
        // Proves the FileId-keyed find-or-create (the no-duplicate guarantee behind the backfill, #18e).
        await using var factory = new ApiFactory(JournalAndPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var first = await CreateEntryWithPhoto(client, fileId);
        var second = await CreateEntryWithPhoto(client, fileId);
        Assert.Equal(first.Photos[0].PhotoId, second.Photos[0].PhotoId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.Photos.CountAsync(p => p.FileId == fileId));
    }

    [Fact]
    public async Task DeletingEntry_KeepsTheLibraryPhoto()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var entry = await CreateEntryWithPhoto(client, fileId);
        var photoId = entry.Photos[0].PhotoId;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{JournalPath}/{entry.JournalEntryId}")).StatusCode);

        // The library Photo (and file) remain — the photo stays in the library (criterion #18c).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/photos/{photoId}")).StatusCode);
    }

    [Fact]
    public async Task DeletingLibraryPhoto_DropsTheStaleJournalLink()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var entry = await CreateEntryWithPhoto(client, fileId);
        var photoId = entry.Photos[0].PhotoId;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/photos/{photoId}")).StatusCode);

        // The journal entry renders without the photo; the stale link is omitted entirely (criterion #18d).
        var reread = await client.GetFromJsonAsync<ExistingJournalEntry>($"{JournalPath}/{entry.JournalEntryId}");
        Assert.Empty(reread!.Photos);
    }

    private static async Task<ExistingJournalEntry> CreateEntryWithPhoto(HttpClient client, Guid fileId)
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

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateDomainContext)
        {
        }

        // Re-isolate OdysseyContext to a store name captured once for this factory, so these
        // cross-module unification tests get a clean, self-contained journal/photo store.
        private static void IsolateDomainContext(IServiceCollection services)
        {
            // Capture the name ONCE — the options action runs on every context construction, so an inlined
            // Guid would give each OdysseyContext its own store and lose data between calls.
            var databaseName = $"domain-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }
}
