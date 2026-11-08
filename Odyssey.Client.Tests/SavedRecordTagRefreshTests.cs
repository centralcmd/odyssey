using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A tag minted inline in a create/edit dialog exists only on the server until the host reloads its tag
/// list. The host resolves a row's tag ids against that list and drops an id it cannot name, so a save
/// that reloads the rows but not the tags shows the new record without the new tag until a refresh.
/// </summary>
/// <remarks>
/// The pages return early from <c>OnInitializedAsync</c> outside a browser, so the tag list starts
/// empty here and the save handler's own reload is the only way the tag can become nameable.
/// </remarks>
public sealed class SavedRecordTagRefreshTests
{
    static SavedRecordTagRefreshTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid HikingId = Guid.NewGuid();

    [Fact]
    public async Task Journal_a_created_entry_shows_a_tag_minted_with_it()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.JournalRead);

        var tags = new Mock<ITagsApiClient<ExistingJournalTag>>();
        tags.Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingJournalTag>>.Success(
                [new ExistingJournalTag { JournalTagId = HikingId, Name = "Hiking" }], HttpStatusCode.OK));
        ctx.Services.AddSingleton(tags.Object);

        var journal = new Mock<IJournalApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        journal.Setup(j => j.ListAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<JournalEntrySummary>>.Success(
            [
                new JournalEntrySummary
                {
                    JournalEntryId = Guid.NewGuid(),
                    Title = "Summit day",
                    Content = string.Empty,
                    EntryDate = new DateTime(2026, 9, 1),
                    CreatedAt = new DateTime(2026, 9, 1),
                    UpdatedAt = new DateTime(2026, 9, 1),
                    AttachmentCount = 0,
                    TagIds = [HikingId],
                },
            ], HttpStatusCode.OK));
        ctx.Services.AddSingleton(journal.Object);

        var cut = ctx.Render<JournalCard>();
        await cut.InvokeAsync(cut.Instance.OnEntryCreated);

        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindComponents<OdsTagChips>(), c => c.Instance.Tags.Any(t => t.Name == "Hiking")));
    }

    [Fact]
    public async Task Tasks_a_saved_task_shows_a_tag_minted_with_it()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.TasksRead);

        var tags = new Mock<ITagsApiClient<ExistingJournalTaskTag>>();
        tags.Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingJournalTaskTag>>.Success(
                [new ExistingJournalTaskTag { JournalTaskTagId = HikingId, Name = "Hiking" }], HttpStatusCode.OK));
        ctx.Services.AddSingleton(tags.Object);

        var tasks = new Mock<ITaskApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        tasks.Setup(t => t.ListAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<JournalTaskSummary>>.Success(
            [
                new JournalTaskSummary
                {
                    JournalTaskId = Guid.NewGuid(),
                    Title = "Pack the tent",
                    Status = JournalTaskStatus.Doing,
                    Position = 0,
                    AttachmentCount = 0,
                    TagIds = [HikingId],
                },
            ], HttpStatusCode.OK));
        ctx.Services.AddSingleton(tasks.Object);

        var cut = ctx.Render<TasksPage>();
        await cut.InvokeAsync(cut.Instance.OnTaskSaved);

        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindComponents<OdsTagChips>(), c => c.Instance.Tags.Any(t => t.Name == "Hiking")));
    }

    /// <summary>
    /// The board card (design system · Tasks board): no glyph beside the title, the tag chips lead with
    /// their own icons, and more than four tags fold into a "+N" chip.
    /// </summary>
    [Fact]
    public async Task Tasks_the_board_card_shows_tag_icons_on_capped_chips_not_beside_the_title()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.TasksRead);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var tags = new Mock<ITagsApiClient<ExistingJournalTaskTag>>();
        tags.Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingJournalTaskTag>>.Success(
                [.. ids.Select((id, i) => new ExistingJournalTaskTag { JournalTaskTagId = id, Name = $"Tag {i}", Icon = i == 0 ? "hiking" : null })],
                HttpStatusCode.OK));
        ctx.Services.AddSingleton(tags.Object);

        var tasks = new Mock<ITaskApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        tasks.Setup(t => t.ListAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<JournalTaskSummary>>.Success(
            [
                new JournalTaskSummary
                {
                    JournalTaskId = Guid.NewGuid(),
                    Title = "Pack the tent",
                    Status = JournalTaskStatus.Doing,
                    Position = 0,
                    AttachmentCount = 0,
                    TagIds = ids,
                    DisplayIcon = "hiking",
                },
            ], HttpStatusCode.OK));
        ctx.Services.AddSingleton(tasks.Object);

        var cut = ctx.Render<TasksPage>();
        await cut.InvokeAsync(cut.Instance.OnTaskSaved);
        var view = cut.FindComponents<OdsInlineSelect>().Single(c => c.Instance.Prefix == "View");
        await cut.InvokeAsync(() => view.Instance.ValueChanged.InvokeAsync("board"));

        cut.WaitForAssertion(() =>
        {
            var head = cut.Find(".tk-card-head");
            Assert.Empty(head.QuerySelectorAll(".tk-card-glyph"));
            Assert.DoesNotContain("hiking", head.TextContent);

            var chips = cut.FindAll(".tk-card-tags .odc-chip.tag");
            Assert.Equal(5, chips.Count);
            Assert.Equal("hiking", chips[0].QuerySelector(".material-icons")!.TextContent.Trim());
            Assert.Equal(OdsTagChip.FallbackGlyph, chips[1].QuerySelector(".material-icons")!.TextContent.Trim());
            Assert.StartsWith("+1", chips[4].TextContent.Trim());
        });
    }
}
