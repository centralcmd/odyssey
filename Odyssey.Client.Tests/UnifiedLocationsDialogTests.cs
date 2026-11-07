using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Weather;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dialogs of issue #383: Place + Details on the event, series and entry editors (AC 1–3), Merge and Delete
/// (AC 9, 10), the location dialog's address group, optional coordinates and Find coordinates (AC 16), and the
/// weather dialogs' <i>Add coordinates first</i> (AC 15).
/// </summary>
public class UnifiedLocationsDialogTests
{
    private static readonly Guid Stored = HyttaId;
    private static readonly Guid CalendarId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private static ExistingCalendarEvent Event(Guid? locationId) => new()
    {
        CalendarEventId = Guid.NewGuid(),
        CalendarId = CalendarId,
        Title = "Dinner",
        Location = "Room 4B",
        LocationId = locationId,
        LocationRef = locationId is { } id ? new LocationRef { LocationId = id, Name = "Hytta" } : null,
        StartDateTime = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc),
        EndDateTime = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc),
    };

    private sealed record CalendarHarness(
        BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<ICalendarApiClient> Calendar, Mock<ILocationsApiClient> Locations,
        List<string> Calls);

    private static CalendarHarness RenderEventDialog(
        CalendarEventDialogMode mode, ExistingCalendarEvent? existing, bool canRead, bool canCreate,
        ApiResult<ExistingLocation>? createResult = null, ApiResult? writeResult = null)
    {
        var ctx = Context();
        var calls = new List<string>();
        var calendar = new Mock<ICalendarApiClient>();
        calendar.Setup(c => c.GetEventAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        calendar.Setup(c => c.UpdateEventAsync(It.IsAny<Guid>(), It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("event"))
            .ReturnsAsync(writeResult ?? ApiResult.Success(HttpStatusCode.OK));
        calendar.Setup(c => c.CreateEventAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("event"))
            .ReturnsAsync(writeResult ?? ApiResult.Success(HttpStatusCode.Created));
        var locations = Locations(Hytta());
        locations.Setup(l => l.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("place"))
            .ReturnsAsync(createResult ?? ApiResult<ExistingLocation>.Success(
                new ExistingLocation { LocationId = StorgataId, Name = "Storgata" }, HttpStatusCode.Created));
        ctx.Services.AddSingleton(calendar.Object);
        ctx.Services.AddSingleton(locations.Object);
        ctx.Services.AddSingleton(Mock.Of<IWeatherApiClient>());

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CalendarEventDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Mode, mode)
            .Add(x => x.EventId, existing?.CalendarEventId)
            .Add(x => x.Calendars, [new ExistingCalendar { CalendarId = CalendarId, Name = "Family", Color = "#4F8EF7" }])
            .Add(x => x.CanUpdate, true)
            .Add(x => x.CanReadLocations, canRead)
            .Add(x => x.CanCreateLocations, canCreate)));
        return new(ctx, cut, calendar, locations, calls);
    }

    private static void Click(IRenderedComponent<ProvidersHost> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static Task Stage(IRenderedComponent<ProvidersHost> cut, string name) => cut.InvokeAsync(() =>
        cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Staged(name)));

    // ── Place + Details (AC 1, 2) ──────────────────────────────────────────────

    [Fact]
    public async Task The_event_editor_shows_place_and_details_and_an_untouched_save_keeps_the_link()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Edit, Event(Stored), canRead: true, canCreate: true);
        await using var _ = h.Ctx;

        Assert.Single(h.Cut.FindComponents<OdsLocationSelect>());
        var details = h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Details").Instance;
        Assert.Equal("Room 4B", details.Value);
        Assert.Equal("Not a place? Put meeting links or room numbers here.", details.Help);

        Click(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => h.Calendar.Verify(c => c.UpdateEventAsync(It.IsAny<Guid>(),
            It.Is<NewCalendarEvent>(e => e.LocationId == Stored && e.Location == "Room 4B"), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Without_locations_read_there_is_no_picker_and_the_save_sends_the_stored_link()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Edit, Event(Stored), canRead: false, canCreate: false);
        await using var _ = h.Ctx;

        Assert.Empty(h.Cut.FindComponents<OdsLocationSelect>());
        Assert.DoesNotContain("Search places", h.Cut.Markup);
        Assert.Single(h.Cut.FindComponents<OdsField>(), f => f.Instance.Label == "Details");

        Click(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => h.Calendar.Verify(c => c.UpdateEventAsync(It.IsAny<Guid>(),
            It.Is<NewCalendarEvent>(e => e.LocationId == Stored), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Clearing_the_picker_clears_the_link()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Edit, Event(Stored), canRead: true, canCreate: false);
        await using var _ = h.Ctx;

        await h.Cut.InvokeAsync(() => h.Cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(null));
        Click(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => h.Calendar.Verify(c => c.UpdateEventAsync(It.IsAny<Guid>(),
            It.Is<NewCalendarEvent>(e => e.LocationId == null), It.IsAny<CancellationToken>()), Times.Once));
    }

    // ── Staged create (AC 3) ──────────────────────────────────────────────────

    [Fact]
    public async Task Saving_creates_the_staged_place_first_then_the_record_with_its_id()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Create, null, canRead: true, canCreate: true);
        await using var _ = h.Ctx;
        await h.Cut.InvokeAsync(() => h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.ValueChanged.InvokeAsync("Dinner"));

        await Stage(h.Cut, "Storgata");
        Assert.Empty(h.Calls);
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        h.Calendar.Verify(c => c.CreateEventAsync(It.Is<NewCalendarEvent>(e => e.LocationId == StorgataId), It.IsAny<CancellationToken>()));
        h.Locations.Verify(l => l.CreateAsync(It.Is<NewLocation>(n => n.Name == "Storgata"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_create_blocks_the_save_and_shows_on_the_picker()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Create, null, canRead: true, canCreate: true,
            createResult: ApiResult<ExistingLocation>.Failure(HttpStatusCode.Conflict, Problem(409, "name", "A location with this name already exists.")));
        await using var _ = h.Ctx;
        await h.Cut.InvokeAsync(() => h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.ValueChanged.InvokeAsync("Dinner"));

        await Stage(h.Cut, "Storgata");
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal("A location with this name already exists.",
            h.Cut.FindComponent<OdsLocationSelect>().Instance.Error));
        Assert.Equal(["place"], h.Calls);
        h.Cut.WaitForAssertion(() => Assert.Contains("Couldn’t add Storgata", h.Cut.Markup));
    }

    /// <summary>
    /// Accessibility review 2: retrying a save whose staged place fails the same way again produces the SAME
    /// status text. It must still be spoken — the region empties and refills — rather than being a no-op
    /// assignment of an unchanged Message.
    /// </summary>
    [Fact]
    public async Task A_repeated_failed_create_is_announced_again()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Create, null, canRead: true, canCreate: true,
            createResult: ApiResult<ExistingLocation>.Failure(HttpStatusCode.Conflict, Problem(409, "name", "A location with this name already exists.")));
        await using var _ = h.Ctx;
        await h.Cut.InvokeAsync(() => h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.ValueChanged.InvokeAsync("Dinner"));
        await Stage(h.Cut, "Storgata");

        var announcer = h.Cut.FindComponents<OdsLiveAnnouncer>().Single(a => a.Instance.Message is null);
        var spoken = new List<string>();
        announcer.OnMarkupUpdated += (_, _) =>
        {
            var text = announcer.Find("[role=status]").TextContent;
            if (spoken.Count == 0 || spoken[^1] != text)
                spoken.Add(text);
        };

        Click(h.Cut, "Create event");
        h.Cut.WaitForAssertion(() => Assert.Contains("Couldn’t add Storgata", spoken));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(2, spoken.Count(t => t == "Couldn’t add Storgata")));
        Assert.Equal(["place", "place"], h.Calls);
    }

    [Fact]
    public async Task Cancelling_creates_nothing()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Create, null, canRead: true, canCreate: true);
        await using var _ = h.Ctx;

        await Stage(h.Cut, "Storgata");
        Click(h.Cut, "Cancel");

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_failed_record_write_reuses_the_created_place_on_retry()
    {
        var h = RenderEventDialog(CalendarEventDialogMode.Create, null, canRead: true, canCreate: true,
            writeResult: ApiResult.Failure(HttpStatusCode.InternalServerError, Problem(500)));
        await using var _ = h.Ctx;
        await h.Cut.InvokeAsync(() => h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.ValueChanged.InvokeAsync("Dinner"));
        await Stage(h.Cut, "Storgata");

        Click(h.Cut, "Create event");
        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event", "event"], h.Calls));
    }

    // ── The journal entry dialog (AC 1, 3) ────────────────────────────────────

    [Fact]
    public async Task The_entry_dialog_keeps_the_stored_link_and_creates_a_staged_place_before_the_entry()
    {
        await using var ctx = Context(PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate);
        var journal = new Mock<IJournalApiClient>();
        var calls = new List<string>();
        journal.Setup(j => j.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateJournalEntry>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("entry")).ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        var locations = Locations(Hytta());
        locations.Setup(l => l.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("place"))
            .ReturnsAsync(ApiResult<ExistingLocation>.Success(new ExistingLocation { LocationId = StorgataId, Name = "Storgata" }, HttpStatusCode.Created));
        ctx.Services.AddSingleton(journal.Object);
        ctx.Services.AddSingleton(locations.Object);
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IPhotosApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton(Mock.Of<ITagQuickCreate<ExistingJournalTag>>());
        var entry = new ExistingJournalEntry
        {
            JournalEntryId = Guid.NewGuid(), Title = "Hike", Content = "Summit.", EntryDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            LocationId = Stored, LocationRef = new LocationRef { LocationId = Stored, Name = "Hytta" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CreateJournalEntryDialog>(d => d.Add(x => x.Open, true).Add(x => x.Entry, entry)));

        Click(cut, "Save changes");
        cut.WaitForAssertion(() => journal.Verify(j => j.UpdateAsync(entry.JournalEntryId,
            It.Is<UpdateJournalEntry>(u => u.LocationId == Stored), It.IsAny<CancellationToken>()), Times.Once));

        await Stage(cut, "Storgata");
        Click(cut, "Save changes");
        cut.WaitForAssertion(() => Assert.Equal(["entry", "place", "entry"], calls));
        journal.Verify(j => j.UpdateAsync(entry.JournalEntryId, It.Is<UpdateJournalEntry>(u => u.LocationId == StorgataId), It.IsAny<CancellationToken>()));
    }

    // ── Merge (AC 9) ──────────────────────────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<ILocationsApiClient> Api, List<ExistingLocation> Merged)
        RenderMerge(ApiResult<ExistingLocation> reply, ExistingLocation? reloadedSource = null)
    {
        var ctx = Context();
        var source = Storgata();
        var api = Locations(reloadedSource ?? source, Hytta());
        api.Setup(a => a.MergeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(reply);
        ctx.Services.AddSingleton(api.Object);
        var merged = new List<ExistingLocation>();
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationMergeDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Source, source)
            .Add(x => x.DebounceMs, 0)
            .Add(x => x.OnMerged, EventCallback.Factory.Create<ExistingLocation>(new object(), t => merged.Add(t)))));
        return (ctx, cut, api, merged);
    }

    private static Task PickTarget(IRenderedComponent<ProvidersHost> cut) => cut.InvokeAsync(() =>
        cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Existing(HyttaId)));

    [Fact]
    public async Task The_merge_dialog_is_named_described_and_opens_on_the_target_picker()
    {
        var (ctx, cut, _, _) = RenderMerge(ApiResult<ExistingLocation>.Success(Hytta(), HttpStatusCode.OK));
        await using var _ = ctx;

        Assert.Contains("Merge “Storgata 1” into another place", cut.Markup);
        var description = cut.Find(".loc-dialog-desc");
        Assert.StartsWith("Every event, journal entry, contact and property that uses “Storgata 1” will use the target instead.", description.TextContent);
        var a11y = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsDialogA11y");
        Assert.Equal(("dialog", description.Id), ((string?)a11y.Arguments[1], (string?)a11y.Arguments[2]));
        // N5: the script is told which dialog — its own class token, on this dialog and on no other.
        var key = Assert.IsType<string>(a11y.Arguments[0]);
        Assert.Equal(cut.FindComponent<OdsModal>().Instance.ModalKey, key);
        Assert.Single(cut.FindAll($".{key}"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstField");
        // The picker is the body's first focusable control, so the default first-field focus lands on it.
        Assert.Equal("combobox", cut.Find(".ods-modal-body input, .ods-modal-body button").GetAttribute("role"));
        Assert.Equal([StorgataId], cut.FindComponent<OdsLocationSelect>().Instance.ExcludeIds);
    }

    [Fact]
    public async Task Merging_without_a_target_says_choose_one()
    {
        var (ctx, cut, api, _) = RenderMerge(ApiResult<ExistingLocation>.Success(Hytta(), HttpStatusCode.OK));
        await using var _ = ctx;

        Click(cut, "Merge");

        Assert.Equal("Choose the place to merge into.", cut.FindComponent<OdsLocationSelect>().Instance.Error);
        api.Verify(a => a.MergeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_successful_merge_hands_back_the_target()
    {
        var (ctx, cut, _, merged) = RenderMerge(ApiResult<ExistingLocation>.Success(Hytta(), HttpStatusCode.OK));
        await using var _ = ctx;

        await PickTarget(cut);
        Click(cut, "Merge");

        cut.WaitForAssertion(() => Assert.Equal(HyttaId, merged.Single().LocationId));
    }

    [Fact]
    public async Task A_403_stays_in_the_dialog_as_an_alert()
    {
        var (ctx, cut, _, merged) = RenderMerge(ApiResult<ExistingLocation>.Failure(HttpStatusCode.Forbidden, Problem(403)));
        await using var _ = ctx;

        await PickTarget(cut);
        Click(cut, "Merge");

        cut.WaitForAssertion(() => Assert.Equal("You don’t have permission to change every record that uses this place.",
            cut.Find(".loc-dialog-alert[role=alert]").TextContent.Replace("error_outline", "").Trim()));
        Assert.Empty(merged);
    }

    [Theory]
    [InlineData(true, "This place is archived and can’t be merged.")]
    [InlineData(false, "This place can’t be merged right now. Reload Locations and try again.")]
    public async Task A_422_on_the_source_reloads_it_and_chooses_the_message_from_what_it_is_now(bool archived, string message)
    {
        var reloaded = Storgata() with { Archived = archived ? DateTime.UtcNow : null };
        var (ctx, cut, api, _) = RenderMerge(
            ApiResult<ExistingLocation>.Failure(HttpStatusCode.UnprocessableEntity, Problem(422, "sourceId", "Server text that is never shown.")),
            reloaded);
        await using var _ = ctx;

        await PickTarget(cut);
        Click(cut, "Merge");

        cut.WaitForAssertion(() => Assert.Contains(message, cut.Find(".loc-dialog-alert").TextContent));
        Assert.DoesNotContain("Server text", cut.Markup);
        api.Verify(a => a.GetAsync(StorgataId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_422_on_the_target_is_a_picker_error()
    {
        var (ctx, cut, _, _) = RenderMerge(
            ApiResult<ExistingLocation>.Failure(HttpStatusCode.UnprocessableEntity, Problem(422, "targetLocationId")));
        await using var _ = ctx;

        await PickTarget(cut);
        Click(cut, "Merge");

        cut.WaitForAssertion(() => Assert.Equal("That place can’t be used here — pick another.",
            cut.FindComponent<OdsLocationSelect>().Instance.Error));
    }

    // ── Delete (AC 10) ────────────────────────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, List<ExistingLocation> Deleted) RenderDelete(ApiResult reply)
    {
        var ctx = Context();
        var api = Locations(Hytta());
        api.Setup(a => a.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(reply);
        ctx.Services.AddSingleton(api.Object);
        var deleted = new List<ExistingLocation>();
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationDeleteDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Location, Hytta())
            .Add(x => x.OnDeleted, EventCallback.Factory.Create<ExistingLocation>(new object(), l => deleted.Add(l)))));
        return (ctx, cut, deleted);
    }

    /// <summary>N5: the open-edge script finds its dialog by the key it is given, and waits for the portal.</summary>
    [Fact]
    public void The_dialog_a11y_script_targets_its_own_dialog_and_retries_until_it_renders()
    {
        var html = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var start = html.IndexOf("window.odsDialogA11y", StringComparison.Ordinal);
        var script = html[start..html.IndexOf("window.odsFocusChainOrHeading", start, StringComparison.Ordinal)];

        Assert.Contains("function (modalKey, role, describedById)", script, StringComparison.Ordinal);
        Assert.Contains("getElementsByClassName(modalKey)", script, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame(apply)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("length - 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_delete_dialog_is_an_alertdialog_described_on_open_that_starts_on_cancel()
    {
        var (ctx, cut, _) = RenderDelete(ApiResult.Success(HttpStatusCode.NoContent));
        await using var _ = ctx;

        Assert.Contains("Delete “Hytta”?", cut.Markup);
        var description = cut.Find(".loc-dialog-desc");
        Assert.Equal("This removes the place permanently. It can only be deleted when nothing uses it.", description.TextContent);
        var a11y = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsDialogA11y");
        Assert.Equal(("alertdialog", description.Id), ((string?)a11y.Arguments[1], (string?)a11y.Arguments[2]));
        // N5: the script is told which dialog — its own class token, on this dialog and on no other.
        var key = Assert.IsType<string>(a11y.Arguments[0]);
        Assert.Equal(cut.FindComponent<OdsModal>().Instance.ModalKey, key);
        Assert.Single(cut.FindAll($".{key}"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusDialogCancel");
        Assert.NotNull(cut.Find("button[data-dialog-cancel]"));
    }

    [Fact]
    public async Task A_409_stays_in_the_dialog_and_a_204_hands_back_the_place()
    {
        var (ctx, cut, deleted) = RenderDelete(ApiResult.Failure(HttpStatusCode.Conflict, Problem(409)));
        await using var _ = ctx;

        cut.FindAll("button").Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("This place is still used — by a record or a merged place",
            cut.Find(".loc-dialog-alert[role=alert]").TextContent));
        Assert.Empty(deleted);

        var (okCtx, okCut, okDeleted) = RenderDelete(ApiResult.Success(HttpStatusCode.NoContent));
        await using var __ = okCtx;
        okCut.FindAll("button").Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        okCut.WaitForAssertion(() => Assert.Single(okDeleted));
    }

    // ── Focus destinations (accessibility review 3) ─────────────────────────────

    [Fact]
    public async Task A_merge_returns_focus_to_the_source_on_cancel_and_to_the_target_row_on_success()
    {
        var ctx = Context();
        await using var _ = ctx;
        var api = Locations(Storgata(), Hytta());
        api.Setup(a => a.MergeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingLocation>.Success(Hytta(), HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationMergeDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Source, Storgata())
            .Add(x => x.DebounceMs, 0)
            .Add(x => x.ReturnFocusToId, LocationsPage.MenuId(StorgataId))
            .Add(x => x.ReturnFocusOnMerged, LocationsPage.MergeDestination)));
        string? ReturnFocus() => cut.FindComponent<OdsModal>().Instance.ReturnFocusToId;

        Assert.Equal(LocationsPage.MenuId(StorgataId), ReturnFocus());

        await PickTarget(cut);
        Click(cut, "Merge");

        cut.WaitForAssertion(() => Assert.Equal($"{LocationsPage.MenuId(HyttaId)} {PageHeader.HeadingId}", ReturnFocus()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_delete_returns_focus_to_the_neighbour_else_the_heading_never_the_deleted_row(bool hasNeighbour)
    {
        var ctx = Context();
        await using var _ = ctx;
        var api = Locations(Hytta());
        api.Setup(a => a.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(api.Object);
        var destination = LocationsPage.DeleteDestination(hasNeighbour ? StorgataId : null);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationDeleteDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Location, Hytta())
            .Add(x => x.ReturnFocusToId, LocationsPage.MenuId(HyttaId))
            .Add(x => x.ReturnFocusOnDeleted, destination)));
        string? ReturnFocus() => cut.FindComponent<OdsModal>().Instance.ReturnFocusToId;

        Assert.Equal(LocationsPage.MenuId(HyttaId), ReturnFocus());

        cut.FindAll("button").Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(destination, ReturnFocus()));
        Assert.DoesNotContain(LocationsPage.MenuId(HyttaId), ReturnFocus()!, StringComparison.Ordinal);
        Assert.EndsWith(PageHeader.HeadingId, destination, StringComparison.Ordinal);
        Assert.Equal(hasNeighbour, destination.StartsWith(LocationsPage.MenuId(StorgataId), StringComparison.Ordinal));
    }

    [Fact]
    public void The_page_heading_carries_the_id_a_focus_chain_ends_on()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "PageHeader.razor"));

        Assert.Matches(new System.Text.RegularExpressions.Regex(@"HtmlTag=""h1""[^>]*tabindex=""-1""[^>]*id=""@HeadingId"""), markup);
    }

    // ── The location dialog (AC 16, §3.6) ─────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<ILocationsApiClient> Api, List<UpdateLocation> Updates, List<NewLocation> Creates)
        RenderLocationDialog(ExistingLocation? location, bool findCoordinates = false)
    {
        var ctx = Context();
        var api = Locations();
        var updates = new List<UpdateLocation>();
        var creates = new List<NewLocation>();
        api.Setup(a => a.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateLocation body, CancellationToken _) =>
            {
                updates.Add(body);
                return ApiResult<ExistingLocation>.Success(new ExistingLocation { LocationId = id, Name = body.Name }, HttpStatusCode.OK);
            });
        api.Setup(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewLocation body, CancellationToken _) =>
            {
                creates.Add(body);
                return ApiResult<ExistingLocation>.Success(new ExistingLocation { LocationId = Guid.NewGuid(), Name = body.Name }, HttpStatusCode.Created);
            });
        ctx.Services.AddSingleton(api.Object);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Location, location)
            .Add(x => x.FindCoordinates, findCoordinates)));
        return (ctx, cut, api, updates, creates);
    }

    private static Task SetField(IRenderedComponent<ProvidersHost> cut, string label, string value) => cut.InvokeAsync(() =>
        cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    [Fact]
    public async Task Find_coordinates_shows_the_disclosure_prefills_the_address_and_sends_nothing_before_search()
    {
        var (ctx, cut, api, _, _) = RenderLocationDialog(Hytta(coordinates: false), findCoordinates: true);
        await using var _ = ctx;

        Assert.Equal("Searching sends the text you enter to Kartverket (Norway) or Open-Meteo (World).",
            cut.Find(".loc-search-disclose").TextContent.Replace("travel_explore", "").Trim());
        Assert.Equal("Fjellveien 12, 2630 Ringebu, NO", cut.Find(".loc-search input[role=combobox]").GetAttribute("value"));
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.FindAll("button").Single(b => b.TextContent.Trim().EndsWith("Search", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => api.Verify(a => a.SearchAsync(
            It.Is<LocationSearchRequest>(r => r.Query == "Fjellveien 12, 2630 Ringebu, NO"), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task The_address_group_round_trips_and_a_place_without_coordinates_keeps_its_source()
    {
        var converted = Hytta(coordinates: false) with { Source = LocationSource.Converted };
        var (ctx, cut, _, updates, _) = RenderLocationDialog(converted);
        await using var _ = ctx;

        await SetField(cut, "Line 2", "Bak låven");
        await SetField(cut, "Country", "se");
        Click(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Single(updates));
        var body = updates[0];
        Assert.Equal(("Fjellveien 12", "Bak låven", "2630", "Ringebu", "SE"), (body.Line1, body.Line2, body.PostalCode, body.City, body.CountryCode));
        Assert.Null(body.Latitude);
        Assert.Null(body.TimeZoneId);
        Assert.Equal(LocationSource.Converted, body.Source);
    }

    [Fact]
    public async Task A_partial_coordinate_pair_is_reported_on_save_in_a_summary_that_takes_focus()
    {
        var (ctx, cut, _, updates, _) = RenderLocationDialog(Hytta(coordinates: false));
        await using var _ = ctx;

        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LatChanged.InvokeAsync(61.5m));
        Assert.Empty(cut.FindAll(".loc-err-summary"));
        Click(cut, "Save changes");

        var summary = cut.WaitForElement(".loc-err-summary");
        Assert.Contains("Enter both latitude and longitude, or neither.", summary.TextContent);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], summary.Id));
        Assert.Empty(updates);
    }

    [Fact]
    public async Task Entered_by_hand_may_save_without_coordinates()
    {
        var (ctx, cut, _, _, creates) = RenderLocationDialog(null);
        await using var _ = ctx;

        Click(cut, "Enter by hand");
        await SetField(cut, "Name", "Hytta");
        await cut.InvokeAsync(() => cut.FindComponents<OdsCombobox>().Single(c => c.Instance.Placeholder == "Search, e.g. Oslo or Lisbon")
            .Instance.ValueChanged.InvokeAsync(null));
        Click(cut, "Create location");

        cut.WaitForAssertion(() => Assert.Single(creates));
        Assert.Null(creates[0].Latitude);
        Assert.Null(creates[0].TimeZoneId);
        Assert.Equal(LocationSource.Manual, creates[0].Source);
    }

}
