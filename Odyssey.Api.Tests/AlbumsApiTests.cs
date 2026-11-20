using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

public class AlbumsApiTests(AlbumsApiTests.Host host) : SharedApiHostTests<AlbumsApiTests.Host>(host)
{
    private const string ActorUserId = "albums-actor-id";
    private const string Path = "/api/albums";

    private static readonly string[] Claims =
    [
        PermissionClaims.PhotoAlbumsRead, PermissionClaims.PhotoAlbumsCreate,
        PermissionClaims.PhotoAlbumsUpdate, PermissionClaims.PhotoAlbumsDelete,
        // Needed to create the member photos the album references.
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosDelete, PermissionClaims.FilesRead,
    ];

    [Fact]
    public async Task List_Guest_ReturnsForbidden()
    {
        var factory = Factory.ForCaller([]);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Create_OverMemberCap_ReturnsBadRequest()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();

        var tooMany = new Guid[PhotoLimits.MaxAlbumMembers + 1];
        var post = await client.PostAsJsonAsync(Path, new NewPhotoAlbum { Name = "Big", PhotoIds = tooMany });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Create_WithOrderedMembers_ReturnsThemInOrder()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();
        var a = await CreatePhoto(factory, client);
        var b = await CreatePhoto(factory, client);

        var created = await CreateAlbum(client, new NewPhotoAlbum { Name = "Trip", PhotoIds = [b, a], CoverPhotoId = a });
        Assert.Equal(new[] { b, a }, created.PhotoIds);
        Assert.Equal(a, created.CoverPhotoId);
    }

    [Fact]
    public async Task Create_CoverNotAMember_ReturnsUnprocessableEntity()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();
        var a = await CreatePhoto(factory, client);
        var stranger = await CreatePhoto(factory, client);

        var post = await client.PostAsJsonAsync(Path, new NewPhotoAlbum { Name = "Trip", PhotoIds = [a], CoverPhotoId = stranger });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task SamePhoto_CanBelongToTwoAlbums()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();
        var photo = await CreatePhoto(factory, client);

        var one = await CreateAlbum(client, new NewPhotoAlbum { Name = "A", PhotoIds = [photo] });
        var two = await CreateAlbum(client, new NewPhotoAlbum { Name = "B", PhotoIds = [photo] });

        Assert.Contains(photo, one.PhotoIds);
        Assert.Contains(photo, two.PhotoIds);
    }

    // Note: deleting a cover photo nulls CoverPhotoId (FK SET NULL) and cascades its album-item rows —
    // relational-FK behaviour EF InMemory cannot emulate. Verified in PhotoRelationalIntegrationTests.

    [Fact]
    public async Task Update_ReplacesMembership_AndCoverRemovedByReplaceIsNulled()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();
        var a = await CreatePhoto(factory, client);
        var b = await CreatePhoto(factory, client);

        var album = await CreateAlbum(client, new NewPhotoAlbum { Name = "Trip", PhotoIds = [a], CoverPhotoId = a });

        // Replace membership with [b] and keep the old cover 'a' (now removed) — cover nulls, not 422 (§7 13c).
        var put = await client.PutAsJsonAsync($"{Path}/{album.PhotoAlbumId}",
            new UpdatePhotoAlbum { Name = "Trip", PhotoIds = [b], CoverPhotoId = a });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;

        Assert.Equal(new[] { b }, updated.PhotoIds);
        Assert.Null(updated.CoverPhotoId);
    }

    [Fact]
    public async Task Delete_Album_LeavesMemberPhotosIntact()
    {
        var factory = Factory.ForCaller(Claims);
        using var client = factory.CreateClient();
        var photo = await CreatePhoto(factory, client);
        var album = await CreateAlbum(client, new NewPhotoAlbum { Name = "Trip", PhotoIds = [photo] });

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{album.PhotoAlbumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/photos/{photo}")).StatusCode);
    }

    private static async Task<Guid> CreatePhoto(OdysseyApiFactory factory, HttpClient client)
    {
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload());
        post.EnsureSuccessStatusCode();
        var created = (await post.Content.ReadFromJsonAsync<ExistingPhoto>())!;
        return created.PhotoId;
    }

    // ── Author-name attribution (#316): Albums route CreatedByUserId through the shared claim-aware
    //    resolver on both the list and the single-album read. ──────────────────────────────────────
    [Fact]
    public async Task Author_DisplayName_Resolves_On_List_And_Detail()
    {
        var factory = Factory.ForCaller(Claims);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        using var client = factory.CreateClient();
        var photo = await CreatePhoto(factory, client);

        var album = await CreateAlbum(client, new NewPhotoAlbum { Name = "Trip", PhotoIds = [photo] });

        var detail = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{album.PhotoAlbumId}");
        Assert.Equal("Ada L.", detail!.CreatedByName);

        var list = await client.GetPagedItemsAsync<PhotoAlbumSummary>(Path);
        Assert.Equal("Ada L.", list!.Single(a => a.PhotoAlbumId == album.PhotoAlbumId).CreatedByName);
    }

    // An albums reader (no users.read) whose author has no profile name gets "Unknown user", NEVER the
    // author's email — the #315 minimisation fix extended to Albums (#316 §5).
    [Fact]
    public async Task Author_NoProfileName_And_NoUsersRead_ReturnsUnknownUser_NotEmail()
    {
        var factory = Factory.ForCaller(Claims);
        var userName = await factory.SeedActorUserAsync();
        using var client = factory.CreateClient();
        var photo = await CreatePhoto(factory, client);

        var album = await CreateAlbum(client, new NewPhotoAlbum { Name = "Trip", PhotoIds = [photo] });

        var detail = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{album.PhotoAlbumId}");
        Assert.Equal("Unknown user", detail!.CreatedByName);
        Assert.NotEqual(userName, detail.CreatedByName);
    }

    private static async Task<ExistingPhotoAlbum> CreateAlbum(HttpClient client, NewPhotoAlbum request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : OdysseyApiFactory([], ActorUserId);
}
