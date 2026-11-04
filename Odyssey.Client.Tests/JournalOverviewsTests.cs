using System.Text.Json;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the journal-module Overview figures: the Tasks Open / Done tiles and every "By tag" row on
/// Journal, Tasks and the Photos/Albums panel. Each tag is fed a DISTINCT count, so a row reading the
/// wrong one fails here rather than rendering a plausible number, and each row must lead with its own
/// tag's icon — the generic glyph only for a tag without a known one.
/// </summary>
public class JournalOverviewsTests
{
    private static readonly Guid Travel = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly Guid Retired = Guid.NewGuid();

    private static OdsBreakdownRow Row(IReadOnlyList<OdsBreakdownRow> rows, string label) =>
        rows.Single(r => r.Label == label);

    // ── Tasks: Open / Done ────────────────────────────────────────────────────

    [Fact]
    public void Tasks_open_is_backlog_plus_doing_and_done_leaves_out_archived()
    {
        var overview = JournalOverviews.Tasks([],
        [
            Task(JournalTaskStatus.Backlog), Task(JournalTaskStatus.Backlog), Task(JournalTaskStatus.Backlog),
            Task(JournalTaskStatus.Doing), Task(JournalTaskStatus.Doing),
            Task(JournalTaskStatus.Done),
            Task(JournalTaskStatus.Archived), Task(JournalTaskStatus.Archived), Task(JournalTaskStatus.Archived), Task(JournalTaskStatus.Archived),
        ]);

        Assert.Equal(5, overview.Open);
        Assert.Equal(1, overview.Done);
    }

    // ── Tasks: By tag ─────────────────────────────────────────────────────────

    [Fact]
    public void Task_tag_rows_count_each_tag_over_the_tasks_not_archived()
    {
        var rows = JournalOverviews.TaskTagRows(TaskTags(),
        [
            Task(JournalTaskStatus.Backlog, Travel, Home),
            Task(JournalTaskStatus.Doing, Travel),
            Task(JournalTaskStatus.Done, Travel),
            Task(JournalTaskStatus.Archived, Travel, Home, Work),
        ]);

        Assert.Equal(3, (int)Row(rows, "Travel").Count!);
        Assert.Equal(1, (int)Row(rows, "Home").Count!);
    }

    [Fact]
    public void Task_tag_rows_leave_out_a_tag_only_archived_tasks_carry()
    {
        var rows = JournalOverviews.TaskTagRows(TaskTags(),
            [Task(JournalTaskStatus.Doing, Travel), Task(JournalTaskStatus.Archived, Work)]);

        Assert.DoesNotContain(rows, r => r.Label == "Work");
    }

    [Fact]
    public void Task_tag_rows_leave_out_an_archived_tag()
    {
        var rows = JournalOverviews.TaskTagRows(TaskTags(), [Task(JournalTaskStatus.Doing, Retired, Travel)]);

        Assert.DoesNotContain(rows, r => r.Label == "Retired");
        Assert.Single(rows);
    }

    [Fact]
    public void A_task_with_two_tags_counts_in_both_rows_so_the_rows_sum_past_the_task_count()
    {
        // Why the tile passes Total="false": a sum would read as a task count and be wrong.
        var tasks = new[] { Task(JournalTaskStatus.Backlog, Travel, Home), Task(JournalTaskStatus.Doing, Home) };

        var rows = JournalOverviews.TaskTagRows(TaskTags(), tasks);

        Assert.Equal(1, (int)Row(rows, "Travel").Count!);
        Assert.Equal(2, (int)Row(rows, "Home").Count!);
        Assert.True(rows.Sum(r => (int)r.Count!) > tasks.Length);
    }

    [Fact]
    public void A_task_naming_one_tag_twice_counts_once_in_its_row()
    {
        var rows = JournalOverviews.TaskTagRows(TaskTags(), [Task(JournalTaskStatus.Backlog, Travel, Travel)]);

        Assert.Equal(1, (int)Row(rows, "Travel").Count!);
    }

    [Fact]
    public void Tasks_overview_carries_the_same_tag_rows_as_the_helper()
    {
        var tasks = new[] { Task(JournalTaskStatus.Backlog, Travel), Task(JournalTaskStatus.Done, Home) };

        var overview = JournalOverviews.Tasks(TaskTags(), tasks);

        Assert.Equal(
            JournalOverviews.TaskTagRows(TaskTags(), tasks).Select(r => (r.Label, r.Count)),
            overview.TagRows.Select(r => (r.Label, r.Count)));
    }

    [Fact]
    public void TasksPageState_saved_before_the_overview_existed_opens_the_overview()
    {
        // State persisted by a build without the Overview has no OverviewOpen member.
        const string legacy = """{"searchOpen":false,"search":"milk","tagFilter":[],"statusFilter":["Doing"],"view":"board"}""";

        var state = JsonSerializer.Deserialize<TasksPage.TasksPageState>(
            legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(state.OverviewOpen);
        Assert.False(state.SearchOpen);
        Assert.Equal("board", state.View);
    }

    // ── Journal: By tag ───────────────────────────────────────────────────────

    [Fact]
    public void Journal_tag_rows_count_entries_per_active_tag_and_omit_empty_rows()
    {
        var rows = JournalOverviews.JournalTagRows(JournalTags(),
            [Entry(Travel, Home), Entry(Travel), Entry(Retired)]);

        Assert.Equal(2, (int)Row(rows, "Travel").Count!);
        Assert.Equal(1, (int)Row(rows, "Home").Count!);
        Assert.DoesNotContain(rows, r => r.Label == "Work");
        Assert.DoesNotContain(rows, r => r.Label == "Retired");
    }

    // ── Photos: By tag ────────────────────────────────────────────────────────

    [Fact]
    public void Photo_tag_rows_read_each_tags_own_server_count_and_omit_empty_rows()
    {
        var rows = JournalOverviews.PhotoTagRows(PhotoTags(),
        [
            new PhotoCountByKey { Key = Travel, Count = 7 },
            new PhotoCountByKey { Key = Home, Count = 2 },
            new PhotoCountByKey { Key = Work, Count = 0 },
        ]);

        Assert.Equal(7, (int)Row(rows, "Travel").Count!);
        Assert.Equal(2, (int)Row(rows, "Home").Count!);
        Assert.DoesNotContain(rows, r => r.Label == "Work");
    }

    // ── Every family: each row leads with its own tag's icon ──────────────────

    [Fact]
    public void Task_tag_rows_lead_with_each_tags_own_icon_else_the_chip_fallback() =>
        AssertGlyphs(JournalOverviews.TaskTagRows(TaskTags(), [Task(JournalTaskStatus.Doing, Travel, Home, Work)]));

    [Fact]
    public void Journal_tag_rows_lead_with_each_tags_own_icon_else_the_chip_fallback() =>
        AssertGlyphs(JournalOverviews.JournalTagRows(JournalTags(), [Entry(Travel, Home, Work)]));

    [Fact]
    public void Photo_tag_rows_lead_with_each_tags_own_icon_else_the_chip_fallback() =>
        AssertGlyphs(JournalOverviews.PhotoTagRows(PhotoTags(),
        [
            new PhotoCountByKey { Key = Travel, Count = 1 },
            new PhotoCountByKey { Key = Home, Count = 1 },
            new PhotoCountByKey { Key = Work, Count = 1 },
        ]));

    // Travel carries a known icon; Home has none; Work's stored key is unknown to the catalogue.
    private static void AssertGlyphs(IReadOnlyList<OdsBreakdownRow> rows)
    {
        Assert.Equal("flight", Row(rows, "Travel").Icon);
        Assert.Equal(JournalTagChips.FallbackGlyph, Row(rows, "Home").Icon);
        Assert.Equal(JournalTagChips.FallbackGlyph, Row(rows, "Work").Icon);
    }

    // ── Every family: each row is tinted in its own tag's colour ──────────────

    [Fact]
    public void Task_tag_rows_take_each_tags_own_colour_else_the_tag_ink() =>
        AssertColours(JournalOverviews.TaskTagRows(TaskTags(), [Task(JournalTaskStatus.Doing, Travel, Home, Work)]),
            JournalOverviews.TagRowColor);

    [Fact]
    public void Journal_tag_rows_take_each_tags_own_colour_else_the_tag_ink() =>
        AssertColours(JournalOverviews.JournalTagRows(JournalTags(), [Entry(Travel, Home, Work)]),
            JournalOverviews.TagRowColor);

    [Fact]
    public void Photo_tag_rows_take_each_tags_own_colour_else_the_panel_ink() =>
        AssertColours(JournalOverviews.PhotoTagRows(PhotoTags(),
        [
            new PhotoCountByKey { Key = Travel, Count = 1 },
            new PhotoCountByKey { Key = Home, Count = 1 },
            new PhotoCountByKey { Key = Work, Count = 1 },
        ]), JournalOverviews.PhotoTagRowColor);

    // Travel carries a colour (stored lower-case, so it must come out rebuilt); Home has none; Work's is corrupt.
    private static void AssertColours(IReadOnlyList<OdsBreakdownRow> rows, string fallback)
    {
        Assert.Equal("oklch(from #0369A1 var(--user-glyph-l, l) c h)", Row(rows, "Travel").IconColor);
        Assert.Equal(fallback, Row(rows, "Home").IconColor);
        Assert.Equal(fallback, Row(rows, "Work").IconColor);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ExistingJournalTaskTag[] TaskTags() =>
    [
        new() { JournalTaskTagId = Travel, Name = "Travel", Icon = "flight", Color = "#0369a1" },
        new() { JournalTaskTagId = Home, Name = "Home", Icon = null },
        new() { JournalTaskTagId = Work, Name = "Work", Icon = "not_a_known_key", Color = "#0369A1;x" },
        new() { JournalTaskTagId = Retired, Name = "Retired", Icon = "flight", Archived = DateTime.UtcNow },
    ];

    private static ExistingJournalTag[] JournalTags() =>
    [
        new() { JournalTagId = Travel, Name = "Travel", Icon = "flight", Color = "#0369a1" },
        new() { JournalTagId = Home, Name = "Home", Icon = null },
        new() { JournalTagId = Work, Name = "Work", Icon = "not_a_known_key", Color = "#0369A1;x" },
        new() { JournalTagId = Retired, Name = "Retired", Icon = "flight", Archived = DateTime.UtcNow },
    ];

    private static ExistingPhotoTag[] PhotoTags() =>
    [
        new() { PhotoTagId = Travel, Name = "Travel", Icon = "flight", Color = "#0369a1" },
        new() { PhotoTagId = Home, Name = "Home", Icon = null },
        new() { PhotoTagId = Work, Name = "Work", Icon = "not_a_known_key", Color = "#0369A1;x" },
    ];

    private static JournalTaskSummary Task(JournalTaskStatus status, params Guid[] tagIds) => new()
    {
        JournalTaskId = Guid.NewGuid(),
        Title = "Task",
        Status = status,
        Position = 0,
        AttachmentCount = 0,
        TagIds = [.. tagIds],
    };

    private static JournalEntrySummary Entry(params Guid[] tagIds) => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "Entry",
        Content = "",
        EntryDate = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        AttachmentCount = 0,
        TagIds = [.. tagIds],
    };
}
