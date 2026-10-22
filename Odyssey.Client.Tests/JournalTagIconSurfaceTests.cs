using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of issue #345 (design system · <c>docs/components-calendar-journal-and-tasks.md</c>,
/// "Tag icons on journal entries, tasks and photos"): the display icon on the entry card, the task row
/// and the photo dialog, shown only when a tag chose one, and the icon field on the three tag pages.
/// </summary>
public class JournalTagIconSurfaceTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public JournalTagIconSurfaceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/files/{id}/content");
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(Mock.Of<IPhotosApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    [Fact]
    public void An_entry_card_leads_its_title_with_the_display_icon_decoratively()
    {
        var cut = ctx.Render<JournalEntryCard>(p => p.Add(c => c.Entry, Entry("beach_access")));

        var glyph = cut.Find(".jec-head .jec-glyph");
        Assert.Equal("beach_access", glyph.TextContent);
        Assert.Equal("true", glyph.GetAttribute("aria-hidden"));
        Assert.Contains("material-icons", glyph.ClassList);
        Assert.Contains("jec-title", glyph.NextElementSibling!.ClassList);
    }

    [Fact]
    public void An_entry_card_without_an_iconned_tag_draws_no_placeholder() =>
        Assert.Empty(ctx.Render<JournalEntryCard>(p => p.Add(c => c.Entry, Entry(TagIcons.Default))).FindAll(".jec-glyph"));

    [Theory]
    [InlineData("checklist", true)]
    [InlineData(TagIcons.Default, false)]
    public void A_task_row_shows_the_display_icon_only_when_a_tag_chose_one(string icon, bool shown)
    {
        var cut = ctx.Render<TaskListRow>(p => p.Add(r => r.Task, TaskRow(icon)));

        var glyphs = cut.FindAll(".tk-list-top .tk-list-glyph");
        Assert.Equal(shown ? 1 : 0, glyphs.Count);
        if (shown)
        {
            Assert.Equal(icon, glyphs[0].TextContent);
            Assert.Equal("true", glyphs[0].GetAttribute("aria-hidden"));
        }
    }

    [Theory]
    [InlineData("landscape", "landscape")]
    [InlineData(TagIcons.Default, "photo_library")]
    public void The_photo_dialog_leads_with_the_display_icon_else_the_library_glyph(string icon, string lead)
    {
        var photo = new PhotoSummary { PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), PersonCount = 0, AlbumCount = 0, DisplayIcon = icon };

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
