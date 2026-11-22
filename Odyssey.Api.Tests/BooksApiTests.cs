using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>The book catalogue's HTTP contract (issue #527 §5, §12) on the EF InMemory tier.</summary>
public class BooksApiTests(BooksApiTests.Host host) : SharedApiHostTests<BooksApiTests.Host>(host)
{
    private const string ActorUserId = "books-actor-id";
    private const string Path = "/api/books";

    /// <summary>A shipped Owner/User-shaped caller: every book claim plus the four reads the book touches.</summary>
    private static readonly string[] Full =
    [
        PermissionClaims.BooksRead, PermissionClaims.BooksCreate, PermissionClaims.BooksUpdate, PermissionClaims.BooksDelete,
        PermissionClaims.BookTagsRead, PermissionClaims.ContactsRead, PermissionClaims.PhotosRead, PermissionClaims.FilesRead,
    ];

    private static string[] Without(params string[] removed) => Full.Except(removed).ToArray();

    private async Task<Guid> SeedTagAsync(string name, bool archived = false)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var tag = new BookTag { BookTagId = Guid.NewGuid(), Name = name, Archived = archived ? DateTime.UtcNow : null };
        context.BookTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.BookTagId;
    }

    private static NewBook NewBook(Guid author, string title = "Dune") => new()
    {
        Title = title,
        Format = BookFormat.Paperback,
        Status = BookStatus.Owned,
        MainAuthorContactId = author,
    };

    private static UpdateBook ToUpdate(ExistingBook book) => new()
    {
        Version = book.Version,
        Title = book.Title,
        Publisher = book.Publisher,
        Year = book.Year,
        Isbn = book.Isbn,
        Format = book.Format,
        Status = book.Status,
        Description = book.Description,
        Notes = book.Notes,
        MainAuthorContactId = book.MainAuthor.ContactId,
    };

    private static async Task<ExistingBook> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingBook>())!;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Create_with_every_link_round_trips_in_order()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory, "Frank Herbert");
        var translator = await PhotoTestSupport.SeedPersonAsync(Factory, "Ada Translator");
        var editor = await PhotoTestSupport.SeedPersonAsync(Factory, "Bea Editor");
        var front = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "front.jpg");
        var spine = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "spine.jpg");
        var file = await PhotoTestSupport.SeedImageFileAsync(Factory, ActorUserId, "dune.epub", "application/epub+zip");
        var tag = await SeedTagAsync("Signed");
        using var client = Factory.ForCaller(Full).CreateClient();

        var request = NewBook(author) with
        {
            Publisher = "Chilton",
            Year = 1965,
            Isbn = "978-0-306-40615-7",
            Contributors = [new() { ContactId = translator, Role = "Translator" }, new() { ContactId = editor }],
            PhotoIds = [front, spine],
            CoverPhotoId = spine,
            AttachmentFileIds = [file],
            TagIds = [tag],
        };
        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, request));

        Assert.Equal(1, created.Version);
        var book = (await client.GetFromJsonAsync<ExistingBook>($"{Path}/{created.BookId}"))!;
        Assert.Equal("9780306406157", book.Isbn);
        Assert.Equal("Frank Herbert", book.MainAuthor.Name);
        Assert.False(book.MainAuthor.Archived);
        Assert.Equal([translator, editor], book.Contributors.Select(c => c.Contact.ContactId));
        Assert.Equal("Translator", book.Contributors[0].Role);
        Assert.Null(book.Contributors[1].Role);
        Assert.Equal(spine, book.CoverPhotoId);
        Assert.True(book.Images!.Single(i => i.PhotoId == spine).IsCover);
        Assert.False(book.Images!.Single(i => i.PhotoId == front).IsCover);
        var attachment = Assert.Single(book.Attachments!);
        Assert.Equal(("dune.epub", "application/epub+zip"), (attachment.FileName, attachment.ContentType));
        Assert.Equal([tag], book.TagIds);
    }

    [Fact]
    public async Task No_raw_user_id_reaches_the_wire()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        var response = await client.PostAsJsonAsync(Path, NewBook(author));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(ActorUserId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("createdByUserId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET", PermissionClaims.BooksRead)]
    [InlineData("POST", PermissionClaims.BooksCreate)]
    [InlineData("PUT", PermissionClaims.BooksUpdate)]
    [InlineData("DELETE", PermissionClaims.BooksDelete)]
    public async Task Each_endpoint_needs_its_claim(string method, string claim)
    {
        using var client = Factory.ForCaller(Without(claim)).CreateClient();
        var id = Guid.NewGuid();
        var response = method switch
        {
            "GET" => await client.GetAsync(Path),
            "POST" => await client.PostAsJsonAsync(Path, NewBook(Guid.NewGuid())),
            "PUT" => await client.PutAsJsonAsync($"{Path}/{id}", ToUpdate(new ExistingBook
            {
                BookId = id,
                Title = "x",
                MainAuthor = new() { ContactId = Guid.NewGuid() },
            })),
            _ => await client.DeleteAsync($"{Path}/{id}"),
        };
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_contacts_read_is_403_whether_or_not_the_author_exists()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Without(PermissionClaims.ContactsRead)).CreateClient();

        var existing = await client.PostAsJsonAsync(Path, NewBook(author));
        var missing = await client.PostAsJsonAsync(Path, NewBook(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal((await ProblemAsync(existing)).GetProperty("detail").GetString(), (await ProblemAsync(missing)).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(PermissionClaims.PhotosRead)]
    [InlineData(PermissionClaims.FilesRead)]
    [InlineData(PermissionClaims.BookTagsRead)]
    public async Task Create_with_a_link_needs_its_read_claim_before_existence(string claim)
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Without(claim)).CreateClient();
        var unknown = Guid.NewGuid();
        var request = claim switch
        {
            PermissionClaims.PhotosRead => NewBook(author) with { PhotoIds = [unknown] },
            PermissionClaims.FilesRead => NewBook(author) with { AttachmentFileIds = [unknown] },
            _ => NewBook(author) with { TagIds = [unknown] },
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        // An empty array needs no claim.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path, NewBook(author) with
        {
            PhotoIds = [],
            AttachmentFileIds = [],
            TagIds = [],
        })).StatusCode);
    }

    [Fact]
    public async Task Put_without_photos_read_keeps_links_with_null_and_refuses_any_array()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        using var full = Factory.ClientFor(Full);
        var created = await CreatedAsync(await full.PostAsJsonAsync(Path, NewBook(author) with { PhotoIds = [photo], CoverPhotoId = photo }));

        using var limited = Factory.ClientFor(Without(PermissionClaims.PhotosRead));
        var same = await limited.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with { PhotoIds = [photo] });
        Assert.Equal(HttpStatusCode.Forbidden, same.StatusCode);

        var kept = await limited.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with { Title = "Dune Messiah" });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var withheld = (await kept.Content.ReadFromJsonAsync<ExistingBook>())!;
        Assert.Null(withheld.Images);
        Assert.Null(withheld.CoverPhotoId);

        var after = (await full.GetFromJsonAsync<ExistingBook>($"{Path}/{created.BookId}"))!;
        Assert.Equal(photo, after.CoverPhotoId);
        Assert.Equal("Dune Messiah", after.Title);

        // The list's image count follows the same claim: a number with it, absent (not 0) without.
        var listedFull = (await full.GetFromJsonAsync<PagedResult<BookSummary>>(Path))!.Items.Single(b => b.BookId == created.BookId);
        Assert.Equal(1, listedFull.ImageCount);
        var listedLimited = (await limited.GetFromJsonAsync<PagedResult<BookSummary>>(Path))!.Items.Single(b => b.BookId == created.BookId);
        Assert.Null(listedLimited.ImageCount);
    }

    [Fact]
    public async Task Put_without_tags_read_cannot_erase_tags_and_an_omitted_array_changes_nothing()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var contributor = await PhotoTestSupport.SeedPersonAsync(Factory, "Co Author");
        var tag = await SeedTagAsync("Favourite");
        using var full = Factory.ClientFor(Full);
        var created = await CreatedAsync(await full.PostAsJsonAsync(Path, NewBook(author) with
        {
            TagIds = [tag],
            Contributors = [new() { ContactId = contributor }],
        }));

        using var limited = Factory.ClientFor(Without(PermissionClaims.BookTagsRead));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await limited.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with { TagIds = [] })).StatusCode);

        var omitted = await full.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var book = (await omitted.Content.ReadFromJsonAsync<ExistingBook>())!;
        Assert.Equal([tag], book.TagIds);
        Assert.Single(book.Contributors);
    }

    [Fact]
    public async Task Without_contacts_read_names_are_null_and_author_filters_are_403()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory, "Ursula Le Guin");
        using var full = Factory.ClientFor(Full);
        var created = await CreatedAsync(await full.PostAsJsonAsync(Path, NewBook(author)));

        using var limited = Factory.ClientFor(Without(PermissionClaims.ContactsRead));
        var book = (await limited.GetFromJsonAsync<ExistingBook>($"{Path}/{created.BookId}"))!;
        Assert.Equal(author, book.MainAuthor.ContactId);
        Assert.Null(book.MainAuthor.Name);
        Assert.Null(book.MainAuthor.Type);
        Assert.Null(book.MainAuthor.Archived);

        Assert.Equal(HttpStatusCode.Forbidden, (await limited.GetAsync($"{Path}?contactId={author}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await limited.GetAsync($"{Path}?sortBy=Author")).StatusCode);

        // Name search is silently skipped, never a 403.
        var search = await limited.GetFromJsonAsync<PagedResult<BookSummary>>($"{Path}?search=ursula");
        Assert.Empty(search!.Items);
        var searchFull = await full.GetFromJsonAsync<PagedResult<BookSummary>>($"{Path}?search=ursula");
        Assert.Single(searchFull!.Items);
    }

    [Fact]
    public async Task Version_conflict_is_409_with_its_code()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author)));

        var first = await client.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with { Title = "A" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<ExistingBook>())!.Version);

        var second = await client.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with { Title = "B" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("book-version-conflict", (await ProblemAsync(second)).GetProperty("code").GetString());
        Assert.Equal("A", (await client.GetFromJsonAsync<ExistingBook>($"{Path}/{created.BookId}"))!.Title);
    }

    [Fact]
    public async Task Put_to_an_unknown_id_is_404_for_every_caller()
    {
        using var full = Factory.ClientFor(Full);
        using var limited = Factory.ClientFor(Without(PermissionClaims.PhotosRead, PermissionClaims.BookTagsRead));
        var id = Guid.NewGuid();
        var body = ToUpdate(new ExistingBook { BookId = id, Title = "x", MainAuthor = new() { ContactId = Guid.NewGuid() } }) with
        {
            PhotoIds = [Guid.NewGuid()],
            TagIds = [Guid.NewGuid()],
        };

        Assert.Equal(HttpStatusCode.NotFound, (await full.PutAsJsonAsync($"{Path}/{id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PutAsJsonAsync($"{Path}/{id}", body)).StatusCode);
    }

    [Theory]
    [InlineData(0, null, "year")]
    [InlineData(null, "9780306406158", "isbn")]
    [InlineData(null, "030640615X", "isbn")]
    public async Task Shape_rules_are_422_keyed(int? year, string? isbn, string field)
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        var response = await client.PostAsJsonAsync(Path, NewBook(author) with { Year = year, Isbn = isbn });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData(-10000)]
    [InlineData(10000)]
    public async Task Year_outside_the_range_is_400(int year)
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, NewBook(author) with { Year = year })).StatusCode);
    }

    [Fact]
    public async Task Bc_and_future_years_and_spaced_isbns_are_stored_as_given()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();

        var bc = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with { Year = -44, Isbn = " 978 - 0 - 306 - 40615 - 7 " }));
        var future = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with { Year = 3000, Isbn = "0-306-40615-2" }));

        Assert.Equal((-44, "9780306406157"), (bc.Year, bc.Isbn));
        Assert.Equal((3000, "0306406152"), (future.Year, future.Isbn));

        // Search matches the other form of a 978 ISBN.
        var byTen = (await client.GetFromJsonAsync<PagedResult<BookSummary>>($"{Path}?search=0306406152"))!;
        Assert.Equal(2, byTen.Items.Count);
    }

    [Fact]
    public async Task Link_rules_are_422()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var archivedTag = await SeedTagAsync("Old", archived: true);
        using var client = Factory.ForCaller(Full).CreateClient();

        async Task<string> FieldOf(NewBook request)
        {
            var response = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await ProblemAsync(response)).GetProperty("errors").EnumerateObject().Single().Name;
        }

        Assert.Equal("contributors[0].contactId", await FieldOf(NewBook(author) with { Contributors = [new() { ContactId = author }] }));
        Assert.Equal("coverPhotoId", await FieldOf(NewBook(author) with { PhotoIds = [photo], CoverPhotoId = Guid.NewGuid() }));
        Assert.Equal("photoIds", await FieldOf(NewBook(author) with { PhotoIds = [photo, photo] }));
        Assert.Equal("photoIds", await FieldOf(NewBook(author) with { PhotoIds = [Guid.NewGuid()] }));
        Assert.Equal("tagIds", await FieldOf(NewBook(author) with { TagIds = [archivedTag] }));
        Assert.Equal("mainAuthorContactId", await FieldOf(NewBook(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_nested_object_in_the_body_creates_nothing()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        var json = $$"""
            {"title":"x","format":1,"status":0,"mainAuthorContactId":"{{author}}",
             "mainAuthor":{"name":"Injected"},"tags":[{"name":"Injected"}],"images":[{"photoId":"{{Guid.NewGuid()}}"}]}
            """;
        var response = await client.PostAsync(Path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        var created = await CreatedAsync(response);
        Assert.Empty(created.TagIds);
        Assert.Empty(created.Images!);
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await context.BookTags.CountAsync());
        Assert.Equal(1, await context.Contacts.CountAsync());
    }

    [Fact]
    public async Task List_filters_and_sorts()
    {
        var herbert = await PhotoTestSupport.SeedPersonAsync(Factory, "Herbert");
        var asimov = await PhotoTestSupport.SeedPersonAsync(Factory, "Asimov");
        var tag = await SeedTagAsync("Sci-fi");
        using var client = Factory.ForCaller(Full).CreateClient();
        await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(herbert, "Dune") with { Year = 1965, TagIds = [tag] }));
        await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(asimov, "Foundation") with
        {
            Year = null,
            Status = BookStatus.Wishlist,
            Format = BookFormat.Ebook,
        }));
        await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(asimov, "Robots") with
        {
            Year = -10,
            Status = BookStatus.LentOut,
            Contributors = [new() { ContactId = herbert }],
        }));

        async Task<List<string>> Titles(string query) =>
            (await client.GetFromJsonAsync<PagedResult<BookSummary>>($"{Path}{query}"))!.Items.Select(b => b.Title).ToList();

        Assert.Equal(["Dune", "Foundation", "Robots"], await Titles(""));
        Assert.Equal(["Foundation", "Robots"], await Titles("?status=2&status=1"));
        Assert.Equal(["Foundation"], await Titles("?format=4"));
        Assert.Equal(["Dune"], await Titles($"?tagIds={tag}"));
        Assert.Equal(["Dune", "Robots"], await Titles($"?contactId={herbert}"));
        Assert.Equal(["Robots", "Dune", "Foundation"], await Titles("?sortBy=Year"));
        Assert.Equal(["Dune", "Robots", "Foundation"], await Titles("?sortBy=Year&sortDir=Desc"));
        Assert.Equal("Dune", (await Titles("?sortBy=Author"))[^1]);
        Assert.Equal("Dune", (await Titles("?sortBy=Author&sortDir=Desc"))[0]);
        Assert.Equal(["Dune", "Robots"], await Titles("?search=her")); // author or any contributor
        Assert.Empty(await Titles("?search=50%25"));
        var tooMany = string.Join("&", Enumerable.Range(0, ListDefaults.MaxFilterArrayLength + 1).Select(_ => $"tagIds={Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?{tooMany}")).StatusCode);
    }

    [Fact]
    public async Task Put_diffs_children_in_place()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var a = await PhotoTestSupport.SeedPersonAsync(Factory, "A");
        var b = await PhotoTestSupport.SeedPersonAsync(Factory, "B");
        var c = await PhotoTestSupport.SeedPersonAsync(Factory, "C");
        var front = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var back = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        using var client = Factory.ForCaller(Full).CreateClient();
        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with
        {
            Contributors = [new() { ContactId = a }, new() { ContactId = b }],
            PhotoIds = [front, back],
            CoverPhotoId = front,
        }));
        var keptRow = created.Contributors.Single(x => x.Contact.ContactId == b).BookContributorId;

        var put = await client.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with
        {
            Contributors = [new() { ContactId = b, Role = "Editor" }, new() { ContactId = c }],
            PhotoIds = [front, back],
            CoverPhotoId = back,
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var book = (await put.Content.ReadFromJsonAsync<ExistingBook>())!;
        Assert.Equal([b, c], book.Contributors.Select(x => x.Contact.ContactId));
        Assert.Equal(keptRow, book.Contributors[0].BookContributorId);
        Assert.Equal("Editor", book.Contributors[0].Role);
        Assert.Equal(back, book.CoverPhotoId);
        Assert.Single(book.Images!, i => i.IsCover);
    }

    [Fact]
    public async Task Delete_removes_the_book_and_its_links_but_not_their_targets()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var tag = await SeedTagAsync("Keep");
        using var client = Factory.ForCaller(Full).CreateClient();
        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with { PhotoIds = [photo], TagIds = [tag] }));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.BookId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{created.BookId}")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await context.BookImages.CountAsync());
        Assert.Equal(0, await context.BookTagLinks.CountAsync());
        Assert.True(await context.Photos.AnyAsync(p => p.PhotoId == photo));
        Assert.True(await context.BookTags.AnyAsync(t => t.BookTagId == tag));
        Assert.True(await context.Contacts.AnyAsync(x => x.ContactId == author));
    }

    [Fact]
    public async Task Contact_delete_is_blocked_by_book_credits_with_claim_conditional_titles()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var contributor = await PhotoTestSupport.SeedPersonAsync(Factory, "Helper");
        using var full = Factory.ClientFor(Full);
        var created = await CreatedAsync(await full.PostAsJsonAsync(Path, NewBook(author, "Credited") with
        {
            Contributors = [new() { ContactId = contributor }],
        }));

        using var deleter = Factory.ClientFor([PermissionClaims.ContactsDelete, PermissionClaims.ContactsRead, PermissionClaims.BooksRead]);
        foreach (var url in new[]
        {
            $"/api/contacts/{author}", $"/api/contacts/{contributor}",
            $"/api/contacts/{author}?detachBlockingLinks=true", $"/api/contacts/{contributor}?detachBlockingLinks=true",
        })
        {
            var response = await deleter.DeleteAsync(url);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await ProblemAsync(response);
            var credits = problem.GetProperty("bookCredits");
            Assert.Equal(1, credits.GetProperty("totalCredits").GetInt32());
            Assert.Equal("Credited", credits.GetProperty("books")[0].GetProperty("title").GetString());
            Assert.DoesNotContain("detachBlockingLinks", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var blind = Factory.ClientFor([PermissionClaims.ContactsDelete, PermissionClaims.ContactsRead]);
        var withheld = await ProblemAsync(await blind.DeleteAsync($"/api/contacts/{author}"));
        Assert.Equal(0, withheld.GetProperty("bookCredits").GetProperty("books").GetArrayLength());
        Assert.Equal(1, withheld.GetProperty("bookCredits").GetProperty("bookCount").GetInt32());

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await context.Contacts.CountAsync());
        // The detach valve never removes a credit (§7.10): both rows survive every refused delete.
        Assert.True(await context.Books.AnyAsync(b => b.BookId == created.BookId && b.MainAuthorContactId == author));
        Assert.True(await context.BookContributors.AnyAsync(c => c.BookId == created.BookId && c.ContactId == contributor));
    }

    /// <summary>
    /// The writes carry the 256 KiB transport cap (§7.12). TestServer does not enforce a request-size limit, so this pins
    /// the attribute; the handler's generic 413 is pinned by <c>WeatherApiTests.An_oversized_body_maps_to_a_generic_413</c>.
    /// </summary>
    [Theory]
    [InlineData(nameof(Controllers.BooksController.Post))]
    [InlineData(nameof(Controllers.BooksController.Put))]
    public void Writes_carry_the_request_size_cap(string action)
    {
        var limit = typeof(Controllers.BooksController).GetMethod(action)!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>().Single();
        Assert.Equal(BookLimits.MaxRequestBytes, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    [Fact]
    public async Task A_body_at_every_field_cap_is_accepted()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ForCaller(Full).CreateClient();
        var full = NewBook(author, new string('t', BookLimits.MaxTitleLength)) with
        {
            Publisher = new string('p', BookLimits.MaxPublisherLength),
            Description = new string('\u00e9', BookLimits.MaxDescriptionLength),
            Notes = new string('\u00e9', BookLimits.MaxNotesLength),
        };
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(full);
        Assert.True(json.Length < BookLimits.MaxRequestBytes);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path, full)).StatusCode);
    }

    private async Task ArchiveAsync<T>(Guid id) where T : class
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        switch (await context.Set<T>().FindAsync(id))
        {
            case Contact contact:
                contact.Archived = DateTime.UtcNow;
                break;
            case BookTag tag:
                tag.Archived = DateTime.UtcNow;
                break;
            default:
                throw new InvalidOperationException($"No {typeof(T).Name} {id}.");
        }

        await context.SaveChangesAsync();
    }

    /// <summary>§3 step 8: a NEW link to an archived contact or tag is a <c>422</c>; a KEPT one survives a <c>PUT</c>.</summary>
    [Fact]
    public async Task An_archived_contact_or_tag_cannot_be_newly_linked_but_a_kept_one_stays()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        var contributor = await PhotoTestSupport.SeedPersonAsync(Factory, "Helper");
        var archivedAuthor = await PhotoTestSupport.SeedPersonAsync(Factory, "Retired Author");
        var archivedContributor = await PhotoTestSupport.SeedPersonAsync(Factory, "Retired Helper");
        var tag = await SeedTagAsync("Classics");
        await ArchiveAsync<Contact>(archivedAuthor);
        await ArchiveAsync<Contact>(archivedContributor);
        using var client = Factory.ClientFor(Full);

        var refusedAuthor = await client.PostAsJsonAsync(Path, NewBook(archivedAuthor));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedAuthor.StatusCode);
        Assert.True((await ProblemAsync(refusedAuthor)).GetProperty("errors").TryGetProperty("mainAuthorContactId", out _));
        var refusedContributor = await client.PostAsJsonAsync(Path, NewBook(author) with
        {
            Contributors = [new() { ContactId = archivedContributor }],
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedContributor.StatusCode);
        Assert.True((await ProblemAsync(refusedContributor)).GetProperty("errors").TryGetProperty("contributors[0].contactId", out _));

        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with
        {
            Contributors = [new() { ContactId = contributor }],
            TagIds = [tag],
        }));
        await ArchiveAsync<Contact>(author);
        await ArchiveAsync<Contact>(contributor);
        await ArchiveAsync<BookTag>(tag);

        var kept = await client.PutAsJsonAsync($"{Path}/{created.BookId}", ToUpdate(created) with
        {
            Title = "Dune Messiah",
            Contributors = [new() { ContactId = contributor, Role = "Editor" }],
            TagIds = [tag],
        });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var updated = (await kept.Content.ReadFromJsonAsync<ExistingBook>())!;
        Assert.Equal("Dune Messiah", updated.Title);
        Assert.Equal([tag], updated.TagIds);
        Assert.True(updated.MainAuthor.Archived);

        var swapped = await client.PutAsJsonAsync($"{Path}/{updated.BookId}", ToUpdate(updated) with
        {
            Contributors = [new() { ContactId = archivedContributor }],
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, swapped.StatusCode);
    }

    /// <summary>Every array cap and string cap is a <c>400</c> one past the limit, on <c>POST</c> and <c>PUT</c> alike.</summary>
    [Fact]
    public async Task One_past_any_cap_is_400_on_post_and_put()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ClientFor(Full);
        var created = await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author)));

        List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var overCap = new (string Field, Func<NewBook, NewBook> Post, Func<UpdateBook, UpdateBook> Put)[]
        {
            ("Title", b => b with { Title = new string('t', BookLimits.MaxTitleLength + 1) }, b => b with { Title = new string('t', BookLimits.MaxTitleLength + 1) }),
            ("Publisher", b => b with { Publisher = new string('p', BookLimits.MaxPublisherLength + 1) }, b => b with { Publisher = new string('p', BookLimits.MaxPublisherLength + 1) }),
            ("Isbn", b => b with { Isbn = new string('1', BookLimits.MaxIsbnInputLength + 1) }, b => b with { Isbn = new string('1', BookLimits.MaxIsbnInputLength + 1) }),
            ("Description", b => b with { Description = new string('d', BookLimits.MaxDescriptionLength + 1) }, b => b with { Description = new string('d', BookLimits.MaxDescriptionLength + 1) }),
            ("Notes", b => b with { Notes = new string('n', BookLimits.MaxNotesLength + 1) }, b => b with { Notes = new string('n', BookLimits.MaxNotesLength + 1) }),
            ("Contributors", b => b with { Contributors = Ids(BookLimits.MaxContributors + 1).Select(id => new BookContributorRequest { ContactId = id }).ToList() },
                b => b with { Contributors = Ids(BookLimits.MaxContributors + 1).Select(id => new BookContributorRequest { ContactId = id }).ToList() }),
            ("Contributors[0].Role", b => b with { Contributors = [new() { ContactId = Guid.NewGuid(), Role = new string('r', BookLimits.MaxRoleLength + 1) }] },
                b => b with { Contributors = [new() { ContactId = Guid.NewGuid(), Role = new string('r', BookLimits.MaxRoleLength + 1) }] }),
            ("PhotoIds", b => b with { PhotoIds = Ids(BookLimits.MaxImages + 1) }, b => b with { PhotoIds = Ids(BookLimits.MaxImages + 1) }),
            ("AttachmentFileIds", b => b with { AttachmentFileIds = Ids(BookLimits.MaxAttachments + 1) }, b => b with { AttachmentFileIds = Ids(BookLimits.MaxAttachments + 1) }),
            ("TagIds", b => b with { TagIds = Ids(BookLimits.MaxTags + 1) }, b => b with { TagIds = Ids(BookLimits.MaxTags + 1) }),
        };

        foreach (var (field, post, put) in overCap)
        {
            var posted = await client.PostAsJsonAsync(Path, post(NewBook(author)));
            Assert.True(posted.StatusCode == HttpStatusCode.BadRequest, $"POST {field}: {posted.StatusCode}");
            var errors = (await ProblemAsync(posted)).GetProperty("errors");
            Assert.True(errors.EnumerateObject().Any(e => e.Name.Equals(field, StringComparison.OrdinalIgnoreCase)), $"POST {field}: {errors}");

            var putted = await client.PutAsJsonAsync($"{Path}/{created.BookId}", put(ToUpdate(created)));
            Assert.True(putted.StatusCode == HttpStatusCode.BadRequest, $"PUT {field}: {putted.StatusCode}");
        }

        using var scope = Factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Books.CountAsync());
    }

    /// <summary>Both ends of the year range are accepted as given (§8.2).</summary>
    [Theory]
    [InlineData(BookLimits.MinYear)]
    [InlineData(BookLimits.MaxYear)]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task The_year_range_ends_are_accepted(int year)
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ClientFor(Full);
        Assert.Equal(year, (await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author) with { Year = year }))).Year);
    }

    /// <summary>A prolific author's <c>409</c> names at most the listed cap of books; the counts stay true.</summary>
    [Fact]
    public async Task The_credit_conflict_lists_a_capped_number_of_books()
    {
        var author = await PhotoTestSupport.SeedPersonAsync(Factory);
        using var client = Factory.ClientFor(Full);
        for (var i = 0; i <= BookLimits.MaxCreditBooksListed; i++)
        {
            await CreatedAsync(await client.PostAsJsonAsync(Path, NewBook(author, $"Volume {i:D2}")));
        }

        using var deleter = Factory.ClientFor([PermissionClaims.ContactsDelete, PermissionClaims.ContactsRead, PermissionClaims.BooksRead]);
        var credits = (await ProblemAsync(await deleter.DeleteAsync($"/api/contacts/{author}"))).GetProperty("bookCredits");
        Assert.Equal(BookLimits.MaxCreditBooksListed + 1, credits.GetProperty("bookCount").GetInt32());
        Assert.Equal(BookLimits.MaxCreditBooksListed + 1, credits.GetProperty("totalCredits").GetInt32());
        Assert.Equal(BookLimits.MaxCreditBooksListed, credits.GetProperty("books").GetArrayLength());
    }

    /// <summary>Security review (PR #577): the tag filter is gated like every other link filter.</summary>
    [Fact]
    public async Task Filtering_by_tag_without_tags_read_is_403()
    {
        using var client = Factory.ClientFor(Without(PermissionClaims.BookTagsRead));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}?tagIds={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
    }

    /// <summary>The class's shared host: every caller's claims travel per request.</summary>
    public sealed class Host() : OdysseyApiFactory([], ActorUserId);
}
