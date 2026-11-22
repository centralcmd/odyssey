using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Library;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The book dialog's request (issue #571; design system · AddBookModal.jsx): contributors, images and files are never
/// edited there — empty on POST, null on PUT — and tags follow the claim snapshot. A stale version shows the conflict
/// alert, never an overwrite. And the contact-delete dialog's books state: no detach, Close only.
/// </summary>
public class BooksDialogsTests
{
    private static readonly Guid AuthorId = Guid.NewGuid();

    private static ExistingContact Contact(Guid id, string name) => new()
    {
        ContactId = id,
        Type = ContactType.Person,
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        ExternalUid = id.ToString(),
    };

    private static ExistingBook Existing() => new()
    {
        BookId = Guid.NewGuid(),
        Version = 4,
        Title = "Dune",
        Format = BookFormat.Hardcover,
        Status = BookStatus.LentOut,
        MainAuthor = new BookContactReference { ContactId = AuthorId, Name = "Frank Herbert", Type = ContactType.Person },
        TagIds = [Guid.NewGuid()],
    };

    private static (IRenderedComponent<BookDialogHost> Cut, Mock<IBooksApiClient> Books) Render(
        ExistingBook? book, bool canReadTags, ApiResult<ExistingBook>? updateResult = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var books = new Mock<IBooksApiClient>();
        books.Setup(b => b.CreateAsync(It.IsAny<NewBook>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewBook body, CancellationToken _) => ApiResult<ExistingBook>.Success(new ExistingBook
            {
                BookId = Guid.NewGuid(),
                Title = body.Title,
                MainAuthor = new BookContactReference { ContactId = body.MainAuthorContactId!.Value },
            }, HttpStatusCode.Created));
        books.Setup(b => b.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateBook>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult ?? ApiResult<ExistingBook>.Success(book ?? Existing(), HttpStatusCode.OK));
        ctx.Services.AddSingleton(books.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new SignedInWith([]));

        var cut = ctx.Render<BookDialogHost>(p => p.Add(h => h.Book, book));
        var dialog = cut.FindComponent<BookDialog>();
        cut.InvokeAsync(() => dialog.Instance.UseContext(canReadTags, [Contact(AuthorId, "Frank Herbert")]));
        return (cut, books);
    }

    private static Task SetField(IRenderedComponent<BookDialogHost> cut, string label, string value)
    {
        var field = cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label);
        return cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private static void Submit(IRenderedComponent<BookDialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim().EndsWith(label, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task A_new_book_posts_empty_link_arrays_and_defaults_to_paperback_owned()
    {
        var (cut, books) = Render(null, canReadTags: false);
        await SetField(cut, "Title", "  The Dispossessed ");
        await SetField(cut, "Year", "−44");
        var author = cut.FindComponent<OdsContactSelect>();
        await cut.InvokeAsync(() => author.Instance.ValueChanged.InvokeAsync(AuthorId.ToString()));

        Submit(cut, "Create book");

        cut.WaitForAssertion(() => books.Verify(b => b.CreateAsync(It.Is<NewBook>(n =>
            n.Title == "The Dispossessed" && n.Year == -44
            && n.Format == BookFormat.Paperback && n.Status == BookStatus.Owned
            && n.MainAuthorContactId == AuthorId
            && n.Contributors!.Count == 0 && n.PhotoIds!.Count == 0 && n.AttachmentFileIds!.Count == 0
            && n.CoverPhotoId == null && n.TagIds!.Count == 0), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_edit_leaves_every_link_array_unchanged_and_tags_follow_the_claim(bool canReadTags)
    {
        var book = Existing();
        var (cut, books) = Render(book, canReadTags);
        await SetField(cut, "Title", "Dune Messiah");

        Submit(cut, "Save");

        cut.WaitForAssertion(() => books.Verify(b => b.UpdateAsync(book.BookId, It.Is<UpdateBook>(u =>
            u.Version == 4 && u.Title == "Dune Messiah" && u.Format == BookFormat.Hardcover && u.Status == BookStatus.LentOut
            && u.Contributors == null && u.PhotoIds == null && u.CoverPhotoId == null && u.AttachmentFileIds == null
            && (canReadTags ? u.TagIds!.SequenceEqual(book.TagIds) : u.TagIds == null)), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task A_bad_year_or_isbn_is_an_inline_error_and_nothing_is_sent()
    {
        var (cut, books) = Render(Existing(), canReadTags: true);
        await SetField(cut, "Year", "0");
        await SetField(cut, "ISBN", "978-0-441-17271-8");

        Submit(cut, "Save");

        Assert.Contains("There is no year 0", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("valid ISBN-10 or ISBN-13", cut.Markup, StringComparison.Ordinal);
        books.Verify(b => b.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateBook>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_stale_version_shows_the_conflict_alert_with_reload_and_keeps_the_draft()
    {
        var conflict = ApiResult<ExistingBook>.Failure(HttpStatusCode.Conflict,
            new ApiProblem { Status = 409, Code = BookRecord.VersionConflictCode, Title = "Conflict" });
        var (cut, _) = Render(Existing(), canReadTags: true, conflict);
        await SetField(cut, "Title", "My draft title");

        Submit(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Someone else changed this book while you were editing", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim().EndsWith("Reload", StringComparison.Ordinal));
        Assert.Equal("My draft title", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.Value);
    }

    public sealed class BookDialogHost : ComponentBase
    {
        [Parameter] public ExistingBook? Book { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<BookDialog>(2);
            builder.AddComponentParameter(3, nameof(BookDialog.Open), true);
            builder.AddComponentParameter(4, nameof(BookDialog.Book), Book);
            builder.CloseComponent();
        }
    }

    // ── Contact delete blocked by book credits (§3.10.1) ─────────────────────────────────────

    private static IRenderedComponent<BlockersHost> RenderBlockers(
        ContactBookCreditBlockers? books, bool unreadable = false, int contractLinks = 0, bool detachRefused = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx.Render<BlockersHost>(p => p
            .Add(h => h.Books, books)
            .Add(h => h.Unreadable, unreadable)
            .Add(h => h.ContractLinks, contractLinks)
            .Add(h => h.DetachRefused, detachRefused));
    }

    [Fact]
    public void Book_credits_show_the_books_state_with_close_only_and_the_titles()
    {
        var cut = RenderBlockers(new ContactBookCreditBlockers
        {
            TotalCredits = 3,
            BookCount = 2,
            Books = [new BookCreditReference { BookId = Guid.NewGuid(), Title = "Dune" }],
        });

        Assert.Contains("Unable to delete this contact", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("is credited on 2 books", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("(3 credits)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Dune", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("And 1 more.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Detach", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Close");
        Assert.DoesNotContain("permission to edit contracts", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_books_read_the_titles_are_withheld_and_it_says_why()
    {
        var cut = RenderBlockers(new ContactBookCreditBlockers { TotalCredits = 1, BookCount = 1, Books = [] });

        Assert.Contains("Which books these are is not shown, because you do not have access to book records.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_member_falls_back_to_one_or_more_books_with_no_numbers()
    {
        var cut = RenderBlockers(null, unreadable: true);

        Assert.Contains("is credited on one or more books.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".cpl-kind-count"));
    }

    [Fact]
    public void Mixed_with_beneficiaries_the_books_come_first_and_a_refused_detach_says_nothing_was_deleted()
    {
        var cut = RenderBlockers(new ContactBookCreditBlockers { TotalCredits = 1, BookCount = 1, Books = [] }, contractLinks: 2, detachRefused: true);

        Assert.Contains("Remove it from those books first; the contract links can then be detached here.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Contract beneficiary", cut.Markup, StringComparison.Ordinal);
        // The books come first: nothing here releases a credit, so the detach is not offered even with beneficiary links.
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Detach", StringComparison.Ordinal));
        var alert = cut.Find("[role=alert]");
        Assert.StartsWith("Nothing was deleted. This contact is also credited on books.", alert.TextContent.Trim(), StringComparison.Ordinal);
    }

    public sealed class BlockersHost : ComponentBase
    {
        [Parameter] public ContactBookCreditBlockers? Books { get; set; }

        [Parameter] public bool Unreadable { get; set; }

        [Parameter] public int ContractLinks { get; set; }

        [Parameter] public bool DetachRefused { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ContactDeleteBlockersDialog>(2);
            builder.AddComponentParameter(3, nameof(ContactDeleteBlockersDialog.Open), true);
            builder.AddComponentParameter(4, nameof(ContactDeleteBlockersDialog.ContactName), "Ursula Le Guin");
            builder.AddComponentParameter(5, nameof(ContactDeleteBlockersDialog.ContractBeneficiaries),
                new ContactContractBeneficiaryBlockers { TotalLinks = ContractLinks, ContractCount = ContractLinks > 0 ? 1 : 0 });
            builder.AddComponentParameter(6, nameof(ContactDeleteBlockersDialog.BookCredits), Books);
            builder.AddComponentParameter(7, nameof(ContactDeleteBlockersDialog.BookCreditsUnreadable), Unreadable);
            builder.AddComponentParameter(8, nameof(ContactDeleteBlockersDialog.DetachRefused), DetachRefused);
            builder.AddComponentParameter(9, nameof(ContactDeleteBlockersDialog.OnDetachAndDelete),
                (Func<Task<DetachedContactLinks?>>)(() => Task.FromResult<DetachedContactLinks?>(null)));
            builder.CloseComponent();
        }
    }
}
