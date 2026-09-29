using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Focus return after a removal destroyed the control that had focus (WCAG 2.4.3). Blazor cannot focus
/// "whatever is still there", so each surface hands focus-return.js a selector chain — the neighbour
/// first, then a stable fallback — and these pin that chain: the neighbour it names is the right one, and
/// the fallback is what is left when there is none.
/// </summary>
public class AttachmentFocusReturnTests
{
    static AttachmentFocusReturnTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const string Module = "./js/focus-return.js";

    private static BunitContext NewContext(out BunitJSModuleInterop module)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        module = ctx.JSInterop.SetupModule(Module);
        ctx.Services.AddMudServices();
        var files = new Mock<IFilesApiClient>();
        files.Setup(f => f.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/files/{id}/content");
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    private static IReadOnlyList<string?> LastCandidates(BunitJSModuleInterop module, string identifier) =>
        [.. module.Invocations[identifier].Last().Arguments.Select(a => a as string)];

    // ── Journal entry dialog: staged photos ──────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<JournalEntryFields> Cut, BunitJSModuleInterop Module, JournalEntryDraft Draft)
        EntryFields(int photos)
    {
        var ctx = NewContext(out var module);
        var draft = new JournalEntryDraft
        {
            Title = "Hike",
            Content = "Summit.",
            Photos = [.. Enumerable.Range(1, photos).Select(i => new JournalDraftPhoto(Guid.NewGuid(), $"photo-{i}.jpg"))],
        };
        var cut = ctx.Render<JournalEntryFields>(p => p.Add(f => f.Draft, draft));
        return (ctx, cut, module, draft);
    }

    private static void RemovePhoto(IRenderedComponent<JournalEntryFields> cut, int index) =>
        cut.FindAll("button.jef-photo-remove")[index].Click();

    [Fact]
    public async Task Removing_a_middle_photo_lands_on_the_next_photos_remove_button()
    {
        var (ctx, cut, module, draft) = EntryFields(3);
        await using var _ = ctx;
        var next = draft.Photos[2];

        RemovePhoto(cut, 1);

        cut.WaitForAssertion(() => Assert.True(module.Invocations["focusFirstLater"].Count > 0));
        var chain = LastCandidates(module, "focusFirstLater");
        Assert.EndsWith(next.FileId.ToString(), chain[0]);
        Assert.EndsWith(" button", chain[^1]);
        Assert.Equal(2, draft.Photos.Count);
        Assert.Contains(cut.FindAll("[role=status].sr-only"), r => r.TextContent == "photo-2.jpg removed from the entry.");
    }

    [Fact]
    public async Task Removing_the_last_photo_lands_on_the_previous_one()
    {
        var (ctx, cut, module, draft) = EntryFields(2);
        await using var _ = ctx;
        var previous = draft.Photos[0];

        RemovePhoto(cut, 1);

        cut.WaitForAssertion(() => Assert.EndsWith(previous.FileId.ToString(), LastCandidates(module, "focusFirstLater")[0]));
    }

    [Fact]
    public async Task Removing_the_only_photo_falls_back_to_add_photos()
    {
        var (ctx, cut, module, _) = EntryFields(1);
        await using var __ = ctx;

        RemovePhoto(cut, 0);

        cut.WaitForAssertion(() =>
        {
            var chain = LastCandidates(module, "focusFirstLater");
            Assert.Null(chain[0]);
            Assert.StartsWith("#jef-add-photos-", chain[1]);
        });
    }

    // ── Task row: "Remove from task" ─────────────────────────────────────────────

    private static FileMetadataResponse Meta(string name) =>
        new(Guid.NewGuid(), name, "application/pdf", 10, "hash", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), null);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Removing_a_task_file_lands_on_its_neighbours_menu(int removed, int neighbour)
    {
        await using var ctx = NewContext(out var module);
        List<FileMetadataResponse> files = [Meta("a.pdf"), Meta("b.pdf")];
        var task = new JournalTaskSummary
        {
            JournalTaskId = Guid.NewGuid(), Title = "Renew passport", Status = JournalTaskStatus.Doing,
            Position = 0, AttachmentCount = files.Count,
        };
        var cut = ctx.Render<TaskListRow>(p => p
            .Add(r => r.Task, task)
            .Add(r => r.CanUpdate, true)
            .Add(r => r.LoadFiles, (Func<Guid, Task<IReadOnlyList<FileMetadataResponse>>>)(_ => Task.FromResult<IReadOnlyList<FileMetadataResponse>>(files)))
            .Add(r => r.OnRemoveFile, EventCallback.Factory.Create<Guid>(this, _ => { })));

        cut.Find("button.jec-filecount").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".jec-file").Count));
        await cut.InvokeAsync(() => cut.FindComponent<JournalFileRows>().Instance.OnRemove!.Value.InvokeAsync(files[removed].Id));

        cut.WaitForAssertion(() =>
        {
            var chain = LastCandidates(module, "focusFirst");
            Assert.Equal($"#{JournalFileRows.MenuId($"tk-files-{task.JournalTaskId}", files[neighbour].Id)} button", chain[0]);
            Assert.Equal($"#tk-row-{task.JournalTaskId} .jec-filecount", chain[1]);
            Assert.Equal($"#tk-row-{task.JournalTaskId} .tk-list-top .je-cardmenu button", chain[2]);
        });
    }
}
