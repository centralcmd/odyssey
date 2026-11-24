using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Library;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shared library photo tile rolled out to the remaining surfaces (Odyssey Design System · components/PhotoTile,
/// version 1795477868-c9a2): a journal entry's photos, a book's images, the Tasks page's Archived divider and the
/// viewer opened over the calendar event dialog. The picker and the event dialog's Photos field are pinned in
/// <see cref="AttachPhotosDialogTests"/> and <see cref="CalendarAttachmentDialogTests"/>.
/// </summary>
public sealed class PhotoTileRolloutTests
{
    static PhotoTileRolloutTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static PhotoImageSource Image(Guid id) => new($"/thumb/{id}", $"/files/{id}");

    // ── Journal entry card ───────────────────────────────────────────────────────────────────

    private static JournalEntrySummary Entry() => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "Summit day",
        Content = string.Empty,
        EntryDate = new DateTime(2026, 9, 1),
        CreatedAt = new DateTime(2026, 9, 1),
        UpdatedAt = new DateTime(2026, 9, 1),
        AttachmentCount = 0,
    };

    [Fact]
    public async Task A_journal_entry_shows_its_photos_as_the_shared_tile_with_the_heart_and_the_archived_badge()
    {
        await using var ctx = PageTestContext.Create();
        var fav = new JournalEntryCard.Photo(Guid.NewGuid(), "Ridge", Image(Guid.NewGuid()), Favourite: true);
        var archived = new JournalEntryCard.Photo(Guid.NewGuid(), "Cairn", Image(Guid.NewGuid()), Favourite: false, Archived: true);
        var unknown = new JournalEntryCard.Photo(Guid.NewGuid(), "Valley", Image(Guid.NewGuid()));
        var opened = new List<Guid>();
        var hearted = new List<Guid>();

        var cut = ctx.Render<JournalEntryCard>(p => p
            .Add(c => c.Entry, Entry())
            .Add(c => c.Photos, [fav, archived, unknown])
            .Add(c => c.OnOpenPhoto, (Guid id) => opened.Add(id))
            .Add(c => c.OnToggleFavourite, (Guid id) => hearted.Add(id)));

        var grid = cut.FindComponent<OdsPhotoGrid<JournalEntryCard.Photo>>();
        Assert.Equal(120, grid.Instance.EffectiveMinTile);
        Assert.Equal("3 photos", cut.Find("ul.odc-photogrid2").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".odc-photogrid-head"));

        var tiles = cut.FindComponents<OdsPhotoTile>().Select(t => t.Instance).ToList();
        Assert.Equal(["Ridge", "Cairn", "Valley"], tiles.Select(t => t.Name));
        Assert.Equal(["Archived"], tiles[1].Badges);
        Assert.DoesNotContain(tiles[0].Badges, b => b is not null);

        // The heart only once the state is known: "Valley" has none, so it shows no heart rather than a guess.
        Assert.Equal(["Unfavourite Ridge", "Favourite Cairn"], cut.FindAll(".odc-phototile-fav").Select(b => b.GetAttribute("aria-label")));
        Assert.Equal("true", cut.Find(".odc-phototile-fav.on").GetAttribute("aria-pressed"));

        cut.FindAll(".odc-phototile-fav")[1].Click();
        Assert.Equal([archived.PhotoId], hearted);

        cut.FindAll(".odc-phototile-open")[2].Click();
        Assert.Equal([unknown.PhotoId], opened);
    }

    [Fact]
    public async Task Without_a_favourite_callback_a_journal_entry_draws_no_heart()
    {
        await using var ctx = PageTestContext.Create();
        var cut = ctx.Render<JournalEntryCard>(p => p
            .Add(c => c.Entry, Entry())
            .Add(c => c.Photos, [new JournalEntryCard.Photo(Guid.NewGuid(), "Ridge", Image(Guid.NewGuid()), Favourite: true)]));

        Assert.Empty(cut.FindAll(".odc-phototile-fav"));
        Assert.Equal("Open Ridge", cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_journal_tiles_take_their_heart_and_badge_from_the_link_dto_without_reading_any_photo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var photos = new Mock<IPhotosApiClient>();
        photos.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"/photos/{id}");
        JournalEntryPhotoDto Link(Guid id, int position, DateTime? favourited = null, DateTime? archived = null) => new()
        {
            JournalEntryPhotoId = Guid.NewGuid(),
            PhotoId = id,
            Position = position,
            CreatedAt = DateTime.UtcNow,
            Favourited = favourited,
            Archived = archived,
        };
        var links = new[] { Link(b, 1, archived: DateTime.UtcNow), Link(a, 0, favourited: DateTime.UtcNow) };

        var tiles = JournalCard.GalleryPhotos(links, new PhotoImageAccess(true), photos.Object);

        Assert.Equal([a, b], tiles.Select(p => p.PhotoId));
        Assert.Equal([true, false], tiles.Select(p => p.Favourite));
        Assert.Equal([false, true], tiles.Select(p => p.Archived));
        // Without photos.read the tile has no image and shows no heart and no badge.
        var noAccess = JournalCard.GalleryPhotos(links, new PhotoImageAccess(false), photos.Object);
        Assert.All(noAccess, p => Assert.Equal((true, (bool?)null, false), (p.Unavailable, p.Favourite, p.Archived)));
        photos.Verify(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Pages/Journal/JournalCard.razor.cs")]
    [InlineData("Pages/Library/BookRecord.razor.cs")]
    [InlineData("Pages/Library/BookImageGrid.razor")]
    [InlineData("Pages/Library/RecipePhotoGrid.razor.cs")]
    public void A_surface_listing_linked_photos_reads_no_photo_for_its_tiles(string path)
    {
        // The link DTOs carry the library photo's favourite and archive state; a per-photo GET /api/photos/{id} for the
        // heart or the badge is the N+1 they replaced.
        var source = Odyssey.Testing.SourceLint.StripComments(File.ReadAllText(Path.Combine(ClientSource.Root, path)));
        Assert.DoesNotMatch(@"Photos(Api)?\.GetAsync\(", source);
        Assert.False(File.Exists(Path.Combine(ClientSource.Root, "Services/LinkedPhotoStates.cs")));
    }

    // ── Books ────────────────────────────────────────────────────────────────────────────────

    private static BookImageDto BookImage(string title, bool cover = false, DateTime? favourited = null, DateTime? archived = null) =>
        new()
        {
            BookImageId = Guid.NewGuid(),
            PhotoId = Guid.NewGuid(),
            Title = title,
            IsCover = cover,
            ThumbnailsReady = true,
            Favourited = favourited,
            Archived = archived,
        };

    private static IRenderedComponent<BookImageGrid> Grid(BunitContext ctx, IReadOnlyList<BookImageDto> images, bool canUpdate,
        bool fav = false) =>
        ctx.Render<BookImageGrid>(p =>
        {
            p.Add(g => g.Images, images).Add(g => g.CanUpdate, canUpdate).Add(g => g.IdPrefix, "bk");
            if (fav)
            {
                p.Add(g => g.OnToggleFavourite, (Guid _) => { });
            }
        });

    [Fact]
    public async Task A_books_images_are_the_shared_tile_in_a_132px_grid_with_a_visible_cover_badge()
    {
        await using var ctx = PageTestContext.Create();
        var cover = BookImage("Front", cover: true);
        var back = BookImage("Back");
        var cut = Grid(ctx, [cover, back], canUpdate: true);

        Assert.Equal(132, cut.FindComponent<OdsPhotoGrid<BookImageDto>>().Instance.EffectiveMinTile);
        Assert.Equal("2 images", cut.Find("ul.odc-photogrid2").GetAttribute("aria-label"));
        Assert.Equal([$"bk-{cover.PhotoId:N}", $"bk-{back.PhotoId:N}"], cut.FindAll("li.odc-photogrid2-item").Select(li => li.Id));

        var tiles = cut.FindComponents<OdsPhotoTile>().Select(t => t.Instance).ToList();
        Assert.Equal(["Cover"], tiles[0].Badges.Where(b => b is not null));
        Assert.DoesNotContain(tiles[1].Badges, b => b is not null);
        Assert.Equal("Actions for image: Front", tiles[0].ActionsLabel);
        Assert.Equal("Cover", cut.Find(".odc-phototile-badge").TextContent);
    }

    [Fact]
    public async Task A_book_image_menu_is_open_then_cover_then_detach_and_only_open_without_books_update()
    {
        await using var ctx = PageTestContext.Create();
        var cover = BookImage("Front", cover: true);
        var back = BookImage("Back");

        var editable = Grid(ctx, [cover, back], canUpdate: true);
        var tiles = editable.FindComponents<OdsPhotoTile>().Select(t => t.Instance).ToList();
        Assert.Equal(["Open", null, "Remove cover", null, "Detach"], tiles[0].Actions!.Select(a => a.Divider ? null : a.Label));
        Assert.Equal(["Open", null, "Make cover", null, "Detach"], tiles[1].Actions!.Select(a => a.Divider ? null : a.Label));
        Assert.True(tiles[1].Actions!.Last().Danger);
        Assert.Equal("Detach Back", tiles[1].Actions!.Last().AriaLabel);

        var readOnly = Grid(ctx, [cover, back], canUpdate: false);
        Assert.All(readOnly.FindComponents<OdsPhotoTile>(), t => Assert.Equal(["Open"], t.Instance.Actions!.Select(a => a.Label)));
    }

    [Fact]
    public async Task A_book_image_shows_the_heart_and_archived_badge_from_its_link()
    {
        await using var ctx = PageTestContext.Create();
        var image = BookImage("Front", cover: true, favourited: DateTime.UtcNow, archived: DateTime.UtcNow);
        var plain = BookImage("Back");

        var cut = Grid(ctx, [image, plain], canUpdate: true, fav: true);
        Assert.Equal(["Unfavourite Front", "Favourite Back"], cut.FindAll(".odc-phototile-fav").Select(b => b.GetAttribute("aria-label")));
        Assert.Equal(["Cover", "Archived"], cut.FindAll(".odc-phototile-badge").Select(b => b.TextContent));

        // No favourite callback (no photos.update): no heart.
        Assert.Empty(Grid(ctx, [image], canUpdate: true, fav: false).FindAll(".odc-phototile-fav"));
    }

    [Fact]
    public async Task A_books_record_lists_the_cover_first_and_opens_the_shared_viewer_over_its_images()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.BooksRead, PermissionClaims.PhotosRead);
        ctx.Render<MudDialogProvider>();
        var first = BookImage("First");
        var cover = BookImage("Cover photo", cover: true);
        var last = BookImage("Last");
        var book = new ExistingBook
        {
            BookId = Guid.NewGuid(),
            Version = 1,
            Title = "Dune",
            Format = BookFormat.Paperback,
            Status = BookStatus.Owned,
            MainAuthor = new BookContactReference { ContactId = Guid.NewGuid(), Name = "Frank Herbert" },
            Contributors = [],
            Images = [first, cover, last],
            CoverPhotoId = cover.PhotoId,
            Attachments = [],
            TagIds = [],
        };

        var cut = ctx.Render<BookRecord>(p => p.Add(r => r.BookId, book.BookId).Add(r => r.CanReadPhotos, true));
        await cut.InvokeAsync(() => cut.Instance.UseBook(book));
        cut.Render();

        Assert.Equal(["Cover photo", "First", "Last"], cut.FindComponents<OdsPhotoTile>().Select(t => t.Instance.Name));
        Assert.DoesNotContain(cut.FindComponents<PhotoDetailDialog>(), d => d.Instance.Open);

        await cut.InvokeAsync(() => cut.FindComponents<OdsPhotoTile>()[1].Instance.OnOpen.InvokeAsync());
        var viewer = cut.FindComponent<PhotoViewer>().Instance;
        Assert.Equal(1, viewer.Index);
        Assert.Equal([cover.PhotoId, first.PhotoId, last.PhotoId], viewer.Photos.Select(p => p.PhotoId));
        Assert.Equal(["Cover photo", "First", "Last"], viewer.Photos.Select(p => p.Title));
        // Focus goes back to the opened image's tile, named by its list item.
        Assert.EndsWith($"{first.PhotoId:N}", viewer.ReturnFocusToId);

        // The viewer's favourite and archive are patched on the image link itself — no photo is re-read.
        await cut.InvokeAsync(() => cut.Instance.OnViewerChanged(new PhotoViewerChange(first.PhotoId, PhotoViewerChangeKind.Favourite)));
        await cut.InvokeAsync(() => cut.Instance.OnViewerChanged(new PhotoViewerChange(first.PhotoId, PhotoViewerChangeKind.Archive)));
        Assert.NotNull(first.Favourited);
        Assert.Equal(["Cover", "Archived"], cut.FindAll(".odc-phototile-badge").Select(b => b.TextContent));
    }

    // ── Calendar: the viewer over the event dialog ───────────────────────────────────────────

    [Fact]
    public async Task Escape_in_the_viewer_over_the_event_dialog_closes_only_the_viewer()
    {
        await using var h = new CalendarAttachmentDialogTests.Harness();
        h.OpenEvent(CalendarAttachmentDialogTests.Standalone(photos: [CalendarAttachmentDialogTests.Photo("Beach", 0), CalendarAttachmentDialogTests.Photo("Dunes", 1)]));

        await h.Cut.InvokeAsync(() => h.Tiles[1].OnOpen.InvokeAsync());
        h.Cut.WaitForAssertion(() => Assert.Equal(2, h.Cut.FindComponents<MudDialogContainer>().Count));
        var viewer = h.Cut.FindComponent<PhotoViewer>().Instance;
        Assert.Equal(1, viewer.Index);
        Assert.EndsWith($"{h.Fields.Photos[1].PhotoId:N}", viewer.ReturnFocusToId);

        var handler = typeof(MudDialogContainer).GetMethod("HandleEscapeAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException("MudDialogContainer no longer has HandleEscapeAsync; update this test's Escape seam.");
        var top = h.Cut.FindComponents<MudDialogContainer>()[^1].Instance;
        await h.Cut.InvokeAsync(() => (Task)handler.Invoke(top, null)!);

        h.Cut.WaitForAssertion(() => Assert.Null(h.Cut.FindComponent<PhotoViewer>().Instance.Index));
        h.Cut.WaitForAssertion(() => Assert.Single(h.Cut.FindComponents<MudDialogContainer>()));
        Assert.True(h.Cut.FindComponent<CalendarEventDialog>().Instance.Open);
        Assert.Equal(2, h.Tiles.Count);
    }

    // ── Tasks ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_tasks_archived_heading_is_the_section_divider_with_a_task_count()
    {
        var source = Odyssey.Testing.SourceLint.StripComments(File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Journal/TasksPage.razor")));
        Assert.Matches(@"<OdsSectionDivider Label=""Archived""[^>]*Meta=""@\(\$""\{ArchivedTasks\.Count\} \{\(ArchivedTasks\.Count == 1 \? ""task"" : ""tasks""\)\}""\)""", source);
        Assert.DoesNotContain("tk-archived-head", source, StringComparison.Ordinal);
        Assert.DoesNotContain("odc-photogrid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tk-archived-", File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Journal/TasksPage.razor.css")), StringComparison.Ordinal);
    }
}
