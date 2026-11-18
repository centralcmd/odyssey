using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// An album's icon and colour (issue #404): the write validation, the read normalisation on detail and
/// list, full-replacement <c>PUT</c> semantics, guard order and the unchanged claim gates.
/// </summary>
public class AlbumIconColorApiTests
{
    private const string ActorUserId = "album-icon-actor-id";
    private const string Path = "/api/albums";

    private static readonly string[] Claims =
    [
        PermissionClaims.PhotoAlbumsRead, PermissionClaims.PhotoAlbumsCreate,
        PermissionClaims.PhotoAlbumsUpdate, PermissionClaims.PhotoAlbumsDelete,
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.FilesRead,
    ];

    [Fact]
    public async Task Create_stores_the_icon_and_upper_cases_the_colour_and_both_reads_return_them()
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAlbum(client, new NewPhotoAlbum { Name = "Lofoten", Icon = "landscape", Color = "#006b5a" });
        Assert.Equal("landscape", created.Icon);
        Assert.Equal("#006B5A", created.Color);

        var detail = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{created.PhotoAlbumId}");
        Assert.Equal("landscape", detail!.Icon);
        Assert.Equal("#006B5A", detail.Color);

        var list = await client.GetFromJsonAsync<PagedResult<PhotoAlbumSummary>>(Path);
        var row = Assert.Single(list!.Items, a => a.PhotoAlbumId == created.PhotoAlbumId);
        Assert.Equal("landscape", row.Icon);
        Assert.Equal("#006B5A", row.Color);
    }

    [Fact]
    public async Task Put_sets_a_new_icon_and_upper_cases_a_new_colour()
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();
        var album = await CreateAlbum(client, new NewPhotoAlbum { Name = "A" });

        var put = await client.PutAsJsonAsync($"{Path}/{album.PhotoAlbumId}",
            new UpdatePhotoAlbum { Name = "A", Icon = "yard", Color = "#15803d" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;
        Assert.Equal("yard", updated.Icon);
        Assert.Equal("#15803D", updated.Color);

        var reread = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{album.PhotoAlbumId}");
        Assert.Equal("yard", reread!.Icon);
        Assert.Equal("#15803D", reread.Color);
    }

    [Fact]
    public async Task Put_with_null_or_omitted_icon_and_colour_clears_both()
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();

        var first = await CreateAlbum(client, new NewPhotoAlbum { Name = "A", Icon = "home", Color = "#15803D" });
        var put = await client.PutAsJsonAsync($"{Path}/{first.PhotoAlbumId}",
            new UpdatePhotoAlbum { Name = "A", Icon = null, Color = null });
        var cleared = (await put.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;
        Assert.Null(cleared.Icon);
        Assert.Null(cleared.Color);

        var second = await CreateAlbum(client, new NewPhotoAlbum { Name = "B", Icon = "home", Color = "#15803D" });
        var omitted = await client.PutAsJsonAsync($"{Path}/{second.PhotoAlbumId}", new { name = "B", archived = false });
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var reread = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{second.PhotoAlbumId}");
        Assert.Null(reread!.Icon);
        Assert.Null(reread.Color);
    }

    public static TheoryData<string> InvalidIcons() =>
        new() { TagIcons.Default, PhotoAlbumIcons.Default, "", " ", "Landscape", "<svg>", new string('q', TagIcons.MaxKeyLength + 1) };

    [Theory]
    [MemberData(nameof(InvalidIcons))]
    public async Task An_invalid_icon_is_one_400_on_Icon_that_echoes_nothing_and_persists_nothing(string icon)
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();
        var existing = await CreateAlbum(client, new NewPhotoAlbum { Name = "Keep", Icon = "home" });

        var post = await client.PostAsJsonAsync(Path, new { name = "New", icon });
        var put = await client.PutAsJsonAsync($"{Path}/{existing.PhotoAlbumId}", new { name = "Keep", archived = false, icon });

        foreach (var response in new[] { post, put })
        {
            await AssertSingleFieldError(response, "Icon", TagIcons.InvalidIconMessage, icon);
        }

        var reread = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{existing.PhotoAlbumId}");
        Assert.Equal("home", reread!.Icon);
        Assert.Equal(1, await AlbumCountAsync(factory));
    }

    public static TheoryData<string> InvalidColors()
    {
        var data = new TheoryData<string>();
        foreach (var value in HexColorNegativeCorpus.All)
        {
            data.Add(value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidColors))]
    public async Task An_invalid_colour_is_one_400_on_Color_that_echoes_nothing_and_persists_nothing(string color)
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();
        var existing = await CreateAlbum(client, new NewPhotoAlbum { Name = "Keep", Color = "#0369A1" });

        var post = await client.PostAsJsonAsync(Path, new { name = "New", color });
        var put = await client.PutAsJsonAsync($"{Path}/{existing.PhotoAlbumId}", new { name = "Keep", archived = false, color });

        foreach (var response in new[] { post, put })
        {
            await AssertSingleFieldError(response, "Color", HexColor.InvalidMessage, color);
        }

        var reread = await client.GetFromJsonAsync<ExistingPhotoAlbum>($"{Path}/{existing.PhotoAlbumId}");
        Assert.Equal("#0369A1", reread!.Color);
        Assert.Equal(1, await AlbumCountAsync(factory));
    }

    [Fact]
    public async Task An_invalid_icon_with_an_unknown_photo_or_foreign_cover_is_the_400_not_the_422()
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        using var client = factory.CreateClient();
        var existing = await CreateAlbum(client, new NewPhotoAlbum { Name = "Keep" });
        var unknownPhoto = Guid.NewGuid();

        var post = await client.PostAsJsonAsync(Path, new { name = "New", icon = "<svg>", photoIds = new[] { unknownPhoto } });
        var postCover = await client.PostAsJsonAsync(Path, new { name = "New", icon = "<svg>", coverPhotoId = unknownPhoto });
        var put = await client.PutAsJsonAsync($"{Path}/{existing.PhotoAlbumId}",
            new { name = "Keep", archived = false, icon = "<svg>", photoIds = new[] { unknownPhoto }, coverPhotoId = unknownPhoto });

        foreach (var response in new[] { post, postCover, put })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Icon", out _));
        }
    }

    [Fact]
    public async Task A_stored_unknown_icon_and_invalid_colour_read_as_null_on_detail_and_list()
    {
        await using var factory = new OdysseyApiFactory(Claims, ActorUserId);
        var id = await SeedAlbumAsync(factory, icon: "<script>retired</script>", color: "#12345Z");
        using var client = factory.CreateClient();

        var detail = await client.GetAsync($"{Path}/{id}");
        var list = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var album = (await detail.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;
        Assert.Null(album.Icon);
        Assert.Null(album.Color);
        var row = Assert.Single((await list.Content.ReadFromJsonAsync<PagedResult<PhotoAlbumSummary>>())!.Items);
        Assert.Null(row.Icon);
        Assert.Null(row.Color);
        Assert.DoesNotContain("retired", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("12345Z", await detail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_update_a_colour_only_put_is_403_and_without_read_both_gets_are_403()
    {
        await using var readOnly = new OdysseyApiFactory([PermissionClaims.PhotoAlbumsRead], ActorUserId);
        await using var none = new OdysseyApiFactory([], ActorUserId);
        var id = await SeedAlbumAsync(readOnly, icon: null, color: null);

        using var readClient = readOnly.CreateClient();
        var put = await readClient.PutAsJsonAsync($"{Path}/{id}", new UpdatePhotoAlbum { Name = "A", Color = "#0369A1" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        using var noneClient = none.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync($"{Path}/{id}")).StatusCode);
    }

    private static async Task AssertSingleFieldError(HttpResponseMessage response, string field, string message, string submitted)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var errors = body.RootElement.GetProperty("errors").GetProperty(field);
        Assert.Equal([message], errors.EnumerateArray().Select(e => e.GetString()));
        // The submitted value never appears as a JSON string anywhere in the body (a bare substring check
        // would match "red" inside "occurred").
        if (!string.IsNullOrWhiteSpace(submitted))
        {
            Assert.DoesNotContain("\"" + JsonEncodedText.Encode(submitted) + "\"", raw, StringComparison.Ordinal);
        }
    }

    private static async Task<Guid> SeedAlbumAsync(OdysseyApiFactory factory, string? icon, string? color)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var album = new PhotoAlbum { Name = "Seeded", Icon = icon, Color = color, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        context.PhotoAlbums.Add(album);
        await context.SaveChangesAsync();
        return album.PhotoAlbumId;
    }

    private static async Task<int> AlbumCountAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoAlbums.CountAsync();
    }

    private static async Task<ExistingPhotoAlbum> CreateAlbum(HttpClient client, NewPhotoAlbum request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingPhotoAlbum>())!;
    }
}
