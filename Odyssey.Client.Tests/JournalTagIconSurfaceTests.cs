using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of issue #345 (design system · <c>docs/components-calendar-journal-and-tasks.md</c>,
/// "Tag icons on journal entries, tasks and photos"): each tag chip on the entry card, the task row and
/// the photo dialog leads with its own tag's icon, no glyph sits beside a card's title, the photo dialog
/// always leads with the library glyph, and the icon field on the tag pages.
/// </summary>
public class JournalTagIconSurfaceTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();
    private readonly Mock<IPhotosApiClient> photos = new();

    public JournalTagIconSurfaceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/files/{id}/content");
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(photos.Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    [Fact]
    public void An_entry_card_draws_no_glyph_beside_its_title()
    {
        var cut = ctx.Render<JournalEntryCard>(p => p
            .Add(c => c.Entry, Entry("beach_access"))
            .Add(c => c.Tags, [new OdsTagChips.Tag("Travel", "beach_access", null)]));

        Assert.Empty(cut.FindAll(".jec-head .material-icons"));
    }

    [Fact]
    public void An_entry_card_leads_each_tag_chip_with_its_own_icon_else_the_generic_glyph()
    {
        var cut = ctx.Render<JournalEntryCard>(p => p
            .Add(c => c.Entry, Entry("beach_access"))
            .Add(c => c.Tags, [new OdsTagChips.Tag("Travel", "beach_access", null), new OdsTagChips.Tag("Misc", null, null)]));

        var glyphs = cut.FindAll(".jec-chips .odc-chip.tag .material-icons");
        Assert.Equal(["beach_access", OdsTagChip.FallbackGlyph], glyphs.Select(g => g.TextContent.Trim()));
        Assert.All(glyphs, g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
    }

    [Theory]
    [InlineData("checklist", "checklist")]
    [InlineData(null, OdsTagChip.FallbackGlyph)]
    [InlineData("not_a_known_key", OdsTagChip.FallbackGlyph)]
    public void A_task_row_shows_the_tag_icon_on_the_chip_not_beside_the_title(string? icon, string glyph)
    {
        var cut = ctx.Render<TaskListRow>(p => p
            .Add(r => r.Task, TaskRow(icon ?? TagIcons.Default))
            .Add(r => r.Tags, [new OdsTagChips.Tag("Errand", icon, null)]));

        Assert.Empty(cut.FindAll(".tk-list-top .material-icons"));
        var chip = cut.Find(".tk-list-chips .odc-chip.tag .material-icons");
        Assert.Equal(glyph, chip.TextContent.Trim());
        Assert.Equal("true", chip.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void The_photo_dialog_shows_its_tags_as_tag_chips_with_their_icons()
    {
        var tagId = Guid.NewGuid();
        var photo = new PhotoSummary { PhotoId = Guid.NewGuid(), PersonCount = 0, AlbumCount = 0, DisplayIcon = "flight" };
        photos.Setup(a => a.GetAsync(photo.PhotoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExistingPhoto
            {
                PhotoId = photo.PhotoId, TagIds = [tagId],
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<PhotoDetailDialog>(p => p
            .Add(d => d.Photos, [photo]).Add(d => d.Index, 0)
            .Add(d => d.TagName, _ => "Travel").Add(d => d.TagIcon, _ => "flight"));

        provider.WaitForAssertion(() =>
        {
            var chip = provider.Find(".pl-vchips .odc-chip.tag");
            Assert.Equal("flight", chip.QuerySelector(".material-icons")!.TextContent.Trim());
            Assert.Contains("Travel", chip.TextContent);
            Assert.Empty(provider.FindAll(".pl-vchips .odc-chip.outline"));
        });
    }

    [Fact]
    public void The_photo_dialog_shows_its_albums_as_chips_with_their_icon_and_colour()
    {
        var coloured = Guid.NewGuid();
        var plain = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var photo = new PhotoSummary { PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), PersonCount = 0, AlbumCount = 3, DisplayIcon = "flight" };
        photos.Setup(a => a.GetAsync(photo.PhotoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExistingPhoto
            {
                PhotoId = photo.PhotoId, FileId = photo.FileId, AlbumIds = [coloured, unknown, plain],
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        var albums = new Dictionary<Guid, OdsTagChips.Tag>
        {
            [coloured] = new("Summer", "beach_access", "#0369a1"),
            [plain] = new("Misc", null, null),
        };

        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<PhotoDetailDialog>(p => p
            .Add(d => d.Photos, [photo]).Add(d => d.Index, 0)
            .Add(d => d.AlbumChip, id => albums.GetValueOrDefault(id)));

        provider.WaitForAssertion(() =>
        {
            var group = provider.FindAll(".pl-vgroup").Single(g => g.QuerySelector(".pl-vgroup-lab")!.TextContent == "Albums");
            var chips = group.QuerySelectorAll(".odc-chip");
            Assert.Equal(2, chips.Length);
            Assert.Equal("beach_access", chips[0].QuerySelector(".material-icons")!.TextContent.Trim());
            Assert.EndsWith("Summer", chips[0].TextContent.Trim());
            Assert.Equal("--user-color:#0369A1;", chips[0].GetAttribute("style"));
            Assert.Equal(PhotoAlbumIcons.Default, chips[1].QuerySelector(".material-icons")!.TextContent.Trim());
            Assert.Null(chips[1].GetAttribute("style"));
            Assert.DoesNotContain("tag", chips[1].ClassList);
            // The album row it replaced is gone: albums read only as chips.
            Assert.DoesNotContain(provider.FindAll(".pl-vrow"), r => r.TextContent.Contains("Summer", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("landscape", "photo_library")]
    [InlineData(TagIcons.Default, "photo_library")]
    public void The_photo_dialog_always_leads_with_the_library_glyph(string icon, string lead)
    {
        var photo = new PhotoSummary { PhotoId = Guid.NewGuid(), PersonCount = 0, AlbumCount = 0, DisplayIcon = icon };

        // An inline MudDialog renders into the provider, so the provider's markup is what the user sees.
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<PhotoDetailDialog>(p => p.Add(d => d.Photos, [photo]).Add(d => d.Index, 0));

        provider.WaitForAssertion(() => Assert.Equal(lead, provider.Find(".ods-modal-lead .material-icons").TextContent.Trim()));
    }

    /// <summary>
    /// All four tag pages are the one <c>OdsTagAdmin</c> with icons on (the design system's
    /// <c>createTagsPage</c> with <c>icons: true</c>), and each helper names the records its icon is drawn on.
    /// </summary>
    [Theory]
    [InlineData("Pages/Finance/TransactionTagsPage.razor", "transaction")]
    [InlineData("Pages/Journal/JournalTagsPage.razor", "journal entry")]
    [InlineData("Pages/Journal/TaskTagsPage.razor", "task")]
    [InlineData("Pages/Photos/PhotoTagsPage.razor", "photo")]
    public void Every_tag_page_turns_icons_on_and_names_its_records(string page, string noun)
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, page));

        Assert.Contains("IconOf=\"@(t => t.Icon)\"", source);
        Assert.Contains($"IconHelp=\"Shown for this tag and on every {noun} carrying it. Default keeps the tag icon.\"", source);
    }

    private static JournalEntrySummary Entry(string displayIcon) => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "A day out",
        Content = "Dear journal.",
        EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        AttachmentCount = 0,
        DisplayIcon = displayIcon,
    };

    private static JournalTaskSummary TaskRow(string displayIcon) => new()
    {
        JournalTaskId = Guid.NewGuid(),
        Title = "Book the ferry",
        Status = JournalTaskStatus.Backlog,
        Position = 0,
        AttachmentCount = 0,
        DisplayIcon = displayIcon,
    };
}
