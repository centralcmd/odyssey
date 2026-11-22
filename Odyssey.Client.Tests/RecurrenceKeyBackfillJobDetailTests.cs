using Bunit;
using MudBlazor.Services;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The recurrence key backfill's run detail (issue #519 §11, PR #528 review T2): its three counts as labelled tiles,
/// and a count withheld from a caller without <c>calendar.read</c> reading "Not shown — needs calendar access".
/// </summary>
public class RecurrenceKeyBackfillJobDetailTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public RecurrenceKeyBackfillJobDetailTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private static RecurrenceKeyBackfillJobRunDetail Run(int? keyed, int? failures, int? detached) => new()
    {
        Id = Guid.NewGuid(),
        JobType = JobType.RecurrenceKeyBackfill,
        Trigger = JobRunTrigger.Scheduled,
        Status = JobRunStatus.Succeeded,
        StartedAtUtc = DateTime.UtcNow,
        SeriesKeyed = keyed,
        SeriesKeyFailures = failures,
        KeyRowsDetached = detached,
    };

    private static List<(string Label, string Value)> Tiles(IRenderedComponent<JobRunDetailRecurrenceKeyBackfill> cut) =>
        cut.FindAll(".odc-meta-tile")
            .Select(tile => (tile.QuerySelector(".odc-meta-tile-label")!.TextContent.Trim(),
                tile.TextContent.Replace(tile.QuerySelector(".odc-meta-tile-label")!.TextContent, string.Empty).Trim()))
            .ToList();

    [Fact]
    public void A_populated_run_shows_each_count_under_its_label()
    {
        var cut = ctx.Render<JobRunDetailRecurrenceKeyBackfill>(p => p.Add(c => c.Run, Run(12, 1, 3)));

        var tiles = Tiles(cut);
        Assert.Equal(["Series keyed", "Series retried", "Rows detached"], tiles.Take(3).Select(t => t.Label));
        Assert.Equal(["12", "1", "3"], tiles.Take(3).Select(t => t.Value));
    }

    [Fact]
    public void A_withheld_count_reads_not_shown_naming_the_calendar_claim_area()
    {
        Assert.Equal("calendar", BackgroundJobPresentation.JobOf(JobType.RecurrenceKeyBackfill).Area);

        var cut = ctx.Render<JobRunDetailRecurrenceKeyBackfill>(p => p
            .Add(c => c.Run, Run(null, null, null))
            .Add(c => c.CanReadDetail, false));

        Assert.All(Tiles(cut).Take(3), t => Assert.Equal("Not shown — needs calendar access", t.Value));
    }
}
