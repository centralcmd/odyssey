using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

public class PhotoTagsApiTests
{
    private const string ActorUserId = "photo-tags-actor-id";
    private const string Path = "/api/photo-tags";

    private static readonly string[] ReadOnly = [PermissionClaims.PhotoTagsRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.PhotoTagsRead, PermissionClaims.PhotoTagsCreate,
        PermissionClaims.PhotoTagsUpdate, PermissionClaims.PhotoTagsDelete,
        // Also allow linking a tag to a photo so the delete-in-use test can create the link.
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.FilesRead,
    ];

    [Fact]
    public async Task List_Guest_ReturnsForbidden()
    {
        await using var factory = new OdysseyApiFactory(permissions: [], actorUserId: ActorUserId);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnly_ReturnForbidden()
    {
        await using var factory = new OdysseyApiFactory(ReadOnly, ActorUserId);
        using var client = factory.CreateClient();
        var post = await client.PostAsJsonAsync(Path, new NewPhotoTag { Name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Post_Get_Update_Delete_RoundTrip()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhotoTag { Name = "Landscape", Description = "Scenery" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingPhotoTag>())!;

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoTagId}", new UpdatePhotoTag { Name = "Nature", Archived = true });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingPhotoTag>())!;
        Assert.Equal("Nature", updated.Name);
        Assert.NotNull(updated.Archived);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.PhotoTagId}")).StatusCode);
    }

    [Fact]
    public async Task Post_DuplicateName_ReturnsConflict()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite, ActorUserId);
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync(Path, new NewPhotoTag { Name = "Travel" })).EnsureSuccessStatusCode();
        var dup = await client.PostAsJsonAsync(Path, new NewPhotoTag { Name = "travel" }); // case-insensitive
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Post_NameCollidingWithArchivedTag_ReturnsConflict()
    {
        // Uniqueness is global across active + archived (§7).
        await using var factory = new OdysseyApiFactory(ReadWrite, ActorUserId);
        await PhotoTestSupport.SeedPhotoTagAsync(factory, "OldTag", archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhotoTag { Name = "OldTag" });
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
    }

    [Fact]
    public async Task Delete_InUseTag_ReturnsConflict()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite, ActorUserId);
        var tagId = await PhotoTestSupport.SeedPhotoTagAsync(factory, "InUse");
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var photo = await client.PostAsJsonAsync("/api/photos", new NewPhoto { FileId = fileId, TagIds = [tagId] });
        photo.EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"{Path}/{tagId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }
}
