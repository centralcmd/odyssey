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

/// <summary>
/// Journal photo links (issue #321 v4, as changed by issue #428 §3.3): a journal entry links library photos by id, the
/// photos are uploaded through the photo API first, and the journal never creates a photo from a file.
/// </summary>
public class JournalPhotoUnificationApiTests
{
    private const string ActorUserId = "journal-unify-actor-id";
    private const string JournalPath = "/api/journal-entries";

    private static readonly string[] JournalUserNoPhotos =
    [
        // A journal user holding NO photos.* claim: since #428 it cannot link a photo it may not read.
        PermissionClaims.JournalCreate, PermissionClaims.JournalRead, PermissionClaims.JournalUpdate,
        PermissionClaims.JournalDelete, PermissionClaims.FilesRead,
    ];

    private static readonly string[] JournalAndPhotos =
    [
        PermissionClaims.JournalCreate, PermissionClaims.JournalRead, PermissionClaims.JournalUpdate,
        PermissionClaims.JournalDelete, PermissionClaims.FilesRead,
        PermissionClaims.PhotosCreate, PermissionClaims.PhotosRead, PermissionClaims.PhotosDelete,
    ];

    [Fact]
    public async Task JournalAdd_LinksAnUploadedPhoto_CarryingItsLabel()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        using var client = factory.CreateClient();
        var uploaded = await PhotoTestSupport.UploadPhotoAsync(
            client, new NewPhotoUpload { Title = "Beach" }, fileName: "beach.jpg");

        var entry = await CreateEntryWithPhoto(client, uploaded.PhotoId);

        var photo = Assert.Single(entry.Photos);
        Assert.Equal(uploaded.PhotoId, photo.PhotoId);
        Assert.Equal("Beach", photo.Title);
        Assert.Equal("beach.jpg", photo.FileName);
    }

    [Fact]
    public async Task JournalUser_WithoutPhotosRead_CannotLinkAPhoto()
    {
        await using var factory = new ApiFactory(JournalUserNoPhotos);
        var (photoId, _) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(JournalPath, NewEntry([photoId]));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    /// <summary>Issue #428 AC 16: the retired <c>photoFileIds</c> is ignored — no photo is made from a file.</summary>
    [Fact]
    public async Task A_body_carrying_photoFileIds_creates_no_photo()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(JournalPath, new
        {
            title = "Old client",
            content = "c",
            entryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            photoFileIds = new[] { fileId },
        });

        post.EnsureSuccessStatusCode();
        var entry = (await post.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
        Assert.Empty(entry.Photos);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos);
    }

    [Fact]
    public async Task UnknownPhotoId_IsUnprocessable_KeyedPhotoIds()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(JournalPath, NewEntry([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
        Assert.Contains("photoIds", await post.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamePhoto_InTwoEntries_IsOneLibraryPhoto()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        using var client = factory.CreateClient();
        var uploaded = await PhotoTestSupport.UploadPhotoAsync(client);

        var first = await CreateEntryWithPhoto(client, uploaded.PhotoId);
        var second = await CreateEntryWithPhoto(client, uploaded.PhotoId);
        Assert.Equal(first.Photos[0].PhotoId, second.Photos[0].PhotoId);

        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos.CountAsync());
    }

    [Fact]
    public async Task DeletingEntry_KeepsTheLibraryPhoto()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        using var client = factory.CreateClient();
        var uploaded = await PhotoTestSupport.UploadPhotoAsync(client);

        var entry = await CreateEntryWithPhoto(client, uploaded.PhotoId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{JournalPath}/{entry.JournalEntryId}")).StatusCode);

        // The library Photo remains — the photo stays in the library (criterion #18c).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/photos/{uploaded.PhotoId}")).StatusCode);
    }

    [Fact]
    public async Task DeletingLibraryPhoto_DropsTheStaleJournalLink()
    {
        await using var factory = new ApiFactory(JournalAndPhotos);
        using var client = factory.CreateClient();
        var uploaded = await PhotoTestSupport.UploadPhotoAsync(client);

        var entry = await CreateEntryWithPhoto(client, uploaded.PhotoId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/photos/{uploaded.PhotoId}")).StatusCode);

        // The journal entry renders without the photo; the stale link is omitted entirely (criterion #18d).
        var reread = await client.GetFromJsonAsync<ExistingJournalEntry>($"{JournalPath}/{entry.JournalEntryId}");
        Assert.Empty(reread!.Photos);
    }

    private static NewJournalEntry NewEntry(Guid[] photoIds) => new()
    {
        Title = "With a photo",
        Content = "Dear journal, today had a picture.",
        EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        PhotoIds = photoIds,
    };

    private static async Task<ExistingJournalEntry> CreateEntryWithPhoto(HttpClient client, Guid photoId)
    {
        var post = await client.PostAsJsonAsync(JournalPath, NewEntry([photoId]));
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
