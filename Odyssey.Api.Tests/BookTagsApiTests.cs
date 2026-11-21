using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>The book-tag contract (issue #527 §5, the photo-tag shape plus <c>bookCount</c>).</summary>
public class BookTagsApiTests(BookTagsApiTests.Host host) : SharedApiHostTests<BookTagsApiTests.Host>(host)
{
    private const string ActorUserId = "book-tags-actor-id";
    private const string Path = "/api/book-tags";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.BookTagsRead, PermissionClaims.BookTagsCreate, PermissionClaims.BookTagsUpdate,
        PermissionClaims.BookTagsDelete, PermissionClaims.BooksRead, PermissionClaims.BooksCreate, PermissionClaims.ContactsRead,
    ];

    [Fact]
    public async Task Read_only_caller_cannot_write()
    {
        using var client = Factory.ForCaller([PermissionClaims.BookTagsRead]).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, new NewBookTag { Name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Round_trip_duplicate_names_and_in_use_delete()
    {
        using var client = Factory.ForCaller(ReadWrite).CreateClient();
        var post = await client.PostAsJsonAsync(Path, new NewBookTag { Name = "Signed", Color = "#aabbcc" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var tag = (await post.Content.ReadFromJsonAsync<ExistingBookTag>())!;
        Assert.Equal("#AABBCC", tag.Color);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, new NewBookTag { Name = "signed" })).StatusCode);

        var archived = await client.PutAsJsonAsync($"{Path}/{tag.BookTagId}", new UpdateBookTag { Name = "Signed", Archived = true });
        Assert.NotNull((await archived.Content.ReadFromJsonAsync<ExistingBookTag>())!.Archived);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, new NewBookTag { Name = "SIGNED" })).StatusCode);
        await client.PutAsJsonAsync($"{Path}/{tag.BookTagId}", new UpdateBookTag { Name = "Signed", Archived = false });

        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var book = await client.PostAsJsonAsync("/api/books", new NewBook
        {
            Title = "Tagged", Format = BookFormat.Hardcover, Status = BookStatus.Owned, MainAuthorContactId = author, TagIds = [tag.BookTagId],
        });
        Assert.Equal(HttpStatusCode.Created, book.StatusCode);

        var listed = (await client.GetFromJsonAsync<PagedResult<ExistingBookTag>>(Path))!;
        Assert.Equal(1, listed.Items.Single().BookCount);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Path}/{tag.BookTagId}")).StatusCode);

        var unused = (await (await client.PostAsJsonAsync(Path, new NewBookTag { Name = "Unused" })).Content.ReadFromJsonAsync<ExistingBookTag>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{unused.BookTagId}")).StatusCode);
    }

    public sealed class Host() : OdysseyApiFactory([], ActorUserId);
}
