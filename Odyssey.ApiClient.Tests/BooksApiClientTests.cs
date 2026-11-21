using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Books;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>The typed books client (issue #527): the list's query-string keys, the version conflict and the delete.</summary>
public class BooksApiClientTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tag = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Author = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static (BooksApiClient Client, RecordingHttpHandler Handler) Create(RecordingHttpHandler handler) =>
        (new BooksApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);

    [Fact]
    public async Task ListAsync_maps_every_filter_onto_its_query_key()
    {
        var (client, handler) = Create(new RecordingHttpHandler("""{"items":[],"totalCount":0}"""));

        await client.ListAsync(2, 25, new BooksListFilter
        {
            Search = "dune",
            Statuses = ["Owned", "LentOut"],
            Formats = ["Ebook"],
            TagIds = [Tag],
            ContactId = Author,
            HasCover = true,
            HasAttachments = false,
            SortBy = "Author",
            SortDir = "desc",
        });

        Assert.Equal("/api/books", handler.LastPath);
        var query = System.Web.HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query);
        Assert.Equal("dune", query["search"]);
        Assert.Equal(["Owned", "LentOut"], query.GetValues("status")!);
        Assert.Equal("Ebook", query["format"]);
        Assert.Equal(Tag.ToString(), query["tagIds"]);
        Assert.Equal(Author.ToString(), query["contactId"]);
        Assert.Equal("true", query["hasCover"]);
        Assert.Equal("false", query["hasAttachments"]);
        Assert.Equal("Author", query["sortBy"]);
        Assert.Equal("desc", query["sortDir"]);
    }

    [Fact]
    public async Task ListAsync_without_a_filter_sends_only_the_window()
    {
        var (client, handler) = Create(new RecordingHttpHandler("""{"items":[],"totalCount":0}"""));

        await client.ListAsync(1, 10);

        var query = System.Web.HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query);
        Assert.DoesNotContain("search", query.AllKeys);
        Assert.DoesNotContain("status", query.AllKeys);
        Assert.DoesNotContain("hasCover", query.AllKeys);
    }

    [Fact]
    public async Task UpdateAsync_puts_to_the_id_and_surfaces_the_version_conflict()
    {
        var (client, handler) = Create(new RecordingHttpHandler(
            HttpStatusCode.Conflict,
            """{"title":"Conflict","status":409,"code":"book-version-conflict"}""",
            RecordingHttpHandler.ProblemMediaType));

        var result = await client.UpdateAsync(Id, new UpdateBook { Version = 3, Title = "Dune" });

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal($"/api/books/{Id}", handler.LastPath);
        Assert.Contains("\"version\":3", handler.LastBody);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("book-version-conflict", result.Problem!.Code);
    }

    [Fact]
    public async Task DeleteAsync_sends_no_body()
    {
        var (client, handler) = Create(new RecordingHttpHandler(HttpStatusCode.NoContent));

        var result = await client.DeleteAsync(Id);

        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal($"/api/books/{Id}", handler.LastPath);
        Assert.True(string.IsNullOrEmpty(handler.LastBody));
        Assert.True(result.IsSuccess);
    }
}
