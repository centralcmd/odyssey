using Odyssey.Client.Components;
using Odyssey.Client.Layout;
using Odyssey.Client.Pages.Library;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Books frontend's pure rules (issue #571; design system · docs/components-books.md): the Library nav module,
/// the EPUB upload content type, the display registries, the claim-gated counts, and the one-array link PUT.
/// </summary>
public class BooksClientTests
{
    // ── Navigation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Library_sits_between_journal_and_internet_with_books_then_reference_book_tags()
    {
        var keys = NavModel.All.Select(m => m.Key).ToList();
        Assert.Equal(keys.IndexOf("journal") + 1, keys.IndexOf("library"));
        Assert.Equal(keys.IndexOf("library") + 1, keys.IndexOf("internet"));

        var library = NavModel.All.Single(m => m.Key == "library");
        Assert.Equal("local_library", library.Icon);
        Assert.Collection(library.Groups,
            g =>
            {
                Assert.Null(g.Label);
                var books = Assert.Single(g.Items);
                Assert.Equal(("books", "auto_stories", PermissionClaims.BooksRead), (books.Href, books.Icon, books.Claim));
            },
            g =>
            {
                Assert.Equal("Reference", g.Label);
                var tags = Assert.Single(g.Items);
                Assert.Equal(("book-tags", PermissionClaims.BookTagsRead), (tags.Href, tags.Claim));
            });
    }

    // ── EPUB on upload (§3.10.3) ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("book.epub", "")]
    [InlineData("BOOK.EPUB", "")]
    [InlineData("book.epub", null)]
    [InlineData("book.epub", "application/octet-stream")]
    public void An_epub_with_no_useful_type_is_sent_as_epub(string name, string? declared) =>
        Assert.Equal("application/epub+zip", ApiInteropExtensions.UploadContentType(name, declared));

    [Theory]
    [InlineData("x.epub.exe", "")]
    [InlineData("epub", "")]
    [InlineData("book.epub", "application/pdf")]
    [InlineData("book.epub", "application/zip")]
    [InlineData("notes.pdf", "application/octet-stream")]
    public void Every_other_combination_is_sent_as_declared(string name, string declared) =>
        Assert.Equal(declared, ApiInteropExtensions.UploadContentType(name, declared));

    // ── Registries ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Formats_follow_the_server_ordinals_and_an_unknown_one_is_never_other()
    {
        Assert.Equal(Enum.GetNames<BookFormat>(), OdsTypeRegistries.BookFormats.Select(f => f.Key));
        Assert.Equal("E-book", OdsTypeRegistries.BookFormatOf(BookFormat.Ebook).Label);
        Assert.Equal("Unknown format", OdsTypeRegistries.BookFormatOf((BookFormat)99).Label);
    }

    [Fact]
    public void Statuses_follow_the_server_ordinals_and_an_unknown_one_says_so()
    {
        Assert.Equal(Enum.GetNames<BookStatus>(), BookVisuals.Statuses.Select(s => s.Key));
        Assert.Equal(OdsChipTone.Pending, BookVisuals.StatusOf(BookStatus.LentOut).ChipTone);
        Assert.Equal("Unknown status", BookVisuals.StatusOf((BookStatus)99).Label);
        Assert.True(BookVisuals.OnTheShelf(BookStatus.LentOut));
        Assert.False(BookVisuals.OnTheShelf(BookStatus.Wishlist));
    }

    [Theory]
    [InlineData("application/epub+zip", "E-book", false)]
    [InlineData("application/pdf", "PDF", true)]
    [InlineData("image/png", "Image", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word document", false)]
    [InlineData("application/zip", "ZIP archive", false)]
    [InlineData("text/plain", "Text", false)]
    [InlineData("application/octet-stream", "File", false)]
    [InlineData(null, "File", false)]
    public void A_file_kind_comes_from_its_content_type_and_epub_is_never_previewed(string? contentType, string label, bool previewable)
    {
        var kind = OdsTypeRegistries.FileKindOf(contentType);
        Assert.Equal((label, previewable), (kind.Label, kind.Previewable));
    }

    // ── Year and ISBN ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1965, "1965")]
    [InlineData(-44, "44 BC")]
    public void A_year_reads_bc_when_negative(int year, string shown) => Assert.Equal(shown, BookVisuals.Year(year));

    [Theory]
    [InlineData("1965", 1965)]
    [InlineData("-44", -44)]
    [InlineData("−44", -44)]
    [InlineData(" 7 ", 7)]
    [InlineData("", null)]
    public void A_typed_year_parses(string text, int? expected)
    {
        Assert.Null(BookVisuals.ParseYear(text, out var year));
        Assert.Equal(expected, year);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("19.5")]
    [InlineData("MCM")]
    [InlineData("12345")]
    public void A_bad_year_is_an_error_never_a_silent_empty(string text)
    {
        Assert.NotNull(BookVisuals.ParseYear(text, out var year));
        Assert.Null(year);
    }

    [Fact]
    public void The_isbn_check_is_the_servers_own_rule()
    {
        Assert.Equal(("9780441172719", null), BookVisuals.Isbn("978-0-441-17271-9"));
        Assert.Equal(("080442957X", null), BookVisuals.Isbn("0-8044-2957-x"));
        Assert.NotNull(BookVisuals.Isbn("978-0-441-17271-8").Error);
        Assert.Equal((null, null), BookVisuals.Isbn("  "));
    }

    // ── The list row ──────────────────────────────────────────────────────────────────────────

    private static readonly Guid AuthorId = Guid.NewGuid();

    private static ExistingBook Book(int images = 1, int files = 2) => new()
    {
        BookId = Guid.NewGuid(),
        Version = 3,
        Title = "Dune",
        Year = 1965,
        Format = BookFormat.Paperback,
        Status = BookStatus.Owned,
        MainAuthor = new BookContactReference { ContactId = AuthorId, Name = "Frank Herbert", Type = ContactType.Person },
        Contributors =
        [
            new ExistingBookContributor
            {
                BookContributorId = Guid.NewGuid(),
                Position = 0,
                Role = "Translator",
                Contact = new BookContactReference { ContactId = Guid.NewGuid(), Name = "Jay Rubin" },
            },
        ],
        Images = [.. Enumerable.Range(0, images).Select(i => new BookImageDto { BookImageId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), IsCover = i == 0, ThumbnailsReady = true })],
        Attachments = [.. Enumerable.Range(0, files).Select(_ => new BookAttachmentDto { BookAttachmentId = Guid.NewGuid(), FileId = Guid.NewGuid(), FileName = "dune.epub", ContentType = "application/epub+zip" })],
        TagIds = [Guid.NewGuid()],
    };

    [Fact]
    public void Counts_are_absent_without_their_claim_and_at_zero()
    {
        var withheld = BooksPage.ToSummary(Book(), canReadPhotos: false, canReadFiles: false);
        Assert.Null(withheld.ImageCount);
        Assert.Null(withheld.AttachmentCount);
        Assert.Null(withheld.CoverPhotoId);
        Assert.Empty(BooksPage.CountsFor(withheld));

        Assert.Empty(BooksPage.CountsFor(BooksPage.ToSummary(Book(images: 0, files: 0), true, true)));

        var counts = BooksPage.CountsFor(BooksPage.ToSummary(Book(images: 1, files: 2), true, true));
        Assert.Equal([("Images", "1"), ("Files", "2")], counts.Select(c => (c.Label, c.Value)));
    }

    [Theory]
    [InlineData("title", nameof(BookSortBy.Title))]
    [InlineData("author", nameof(BookSortBy.Author))]
    [InlineData("year", nameof(BookSortBy.Year))]
    [InlineData("created", nameof(BookSortBy.CreatedAt))]
    [InlineData("updated", nameof(BookSortBy.UpdatedAt))]
    public void Sort_keys_map_to_the_servers_sort(string key, string sortBy) => Assert.Equal(sortBy, BooksPage.SortByOf(key));

    // ── Link changes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_link_change_starts_from_a_put_that_changes_nothing()
    {
        var book = Book();
        var request = BookRecord.Unchanged(book);

        Assert.Equal(book.Version, request.Version);
        Assert.Equal(AuthorId, request.MainAuthorContactId);
        Assert.Equal((book.Title, book.Year, book.Format, book.Status), (request.Title, request.Year, request.Format, request.Status));
        // Every array null — unchanged — so one link change sends one array.
        Assert.Null(request.Contributors);
        Assert.Null(request.PhotoIds);
        Assert.Null(request.CoverPhotoId);
        Assert.Null(request.AttachmentFileIds);
        Assert.Null(request.TagIds);
    }

    // ── Pickers ───────────────────────────────────────────────────────────────────────────────

    private static ExistingContact Contact(Guid id, string name, bool archived = false) => new()
    {
        ContactId = id,
        Type = ContactType.Person,
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        ExternalUid = id.ToString(),
        Archived = archived ? DateTime.UtcNow : null,
    };

    [Fact]
    public void The_contributor_picker_leaves_out_everyone_already_credited_and_archived_contacts()
    {
        var book = Book();
        var contributorId = book.Contributors[0].Contact.ContactId;
        var free = Guid.NewGuid();
        var archived = Guid.NewGuid();
        var contacts = new[] { Contact(AuthorId, "Frank Herbert"), Contact(contributorId, "Jay Rubin"), Contact(free, "Homer"), Contact(archived, "Old", archived: true) };

        Assert.Equal([free], BookContributorDialog.Candidates(book, null, contacts).Select(c => c.ContactId));
        // Editing a contributor keeps that contributor as a choice.
        Assert.Equal([free, contributorId],
            BookContributorDialog.Candidates(book, book.Contributors[0], contacts).Select(c => c.ContactId));
    }

    [Fact]
    public void The_main_author_picker_leaves_out_contributors_but_keeps_the_current_author_when_archived()
    {
        var book = Book();
        var contributorId = book.Contributors[0].Contact.ContactId;
        var contacts = new[] { Contact(AuthorId, "Frank Herbert", archived: true), Contact(contributorId, "Jay Rubin") };

        Assert.Equal([AuthorId], BookDialog.AuthorCandidates(book, contacts).Select(c => c.ContactId));
    }
}
