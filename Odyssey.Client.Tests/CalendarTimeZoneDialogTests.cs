using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The event dialog's half of issue #520 (calendar recurrence fidelity and time zones): the zone field, the two-zone
/// line, the DST hints, the "Edited separately" cue, the series preview-and-confirm step, the version conflict and the
/// read-only view. Each test names the acceptance criterion it proves. The page and the import are
/// <see cref="CalendarTimeZonePageTests"/> and <see cref="CalendarRecurrenceImportTests"/>.
/// </summary>
public class CalendarTimeZoneDialogTests
{
    static CalendarTimeZoneDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    internal static readonly Guid EventId = Guid.Parse("5e0c0000-0000-0000-0000-000000000001");
    internal static readonly Guid PatternId = Guid.Parse("5e0c0000-0000-0000-0000-000000000002");
    internal static readonly Guid LoadedVersion = Guid.Parse("5e0c0000-0000-0000-0000-0000000000a1");
    internal static readonly Guid OtherVersion = Guid.Parse("5e0c0000-0000-0000-0000-0000000000b2");

    internal static DateTime Utc(DateTime wall, string zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById(zone)), DateTimeKind.Utc);

    /// <summary>A zoned event as the server reads it back: the wall-clock in its zone, plus the instants (#519 §5).</summary>
    internal static ExistingCalendarEvent Zoned(DateTime wall, string zone, Action<ExistingCalendarEvent>? configure = null)
    {
        var e = new ExistingCalendarEvent
        {
            CalendarEventId = EventId, CalendarId = CalendarId, Title = "Standup",
            StartDateTime = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified),
            EndDateTime = DateTime.SpecifyKind(wall.AddHours(1), DateTimeKind.Unspecified),
            TimeZoneId = zone, StartUtc = Utc(wall, zone), EndUtc = Utc(wall.AddHours(1), zone), IsKeyed = true,
        };
        configure?.Invoke(e);
        return e;
    }

    /// <summary>A floating (pre-#519) event: its clock time labelled UTC, no zone, no instant.</summary>
    internal static ExistingCalendarEvent Floating(DateTime wall, Action<ExistingCalendarEvent>? configure = null)
    {
        var e = new ExistingCalendarEvent
        {
            CalendarEventId = EventId, CalendarId = CalendarId, Title = "Standup",
            StartDateTime = DateTime.SpecifyKind(wall, DateTimeKind.Utc), EndDateTime = DateTime.SpecifyKind(wall.AddHours(1), DateTimeKind.Utc),
            IsFloating = true,
        };
        configure?.Invoke(e);
        return e;
    }

    internal static ExistingRecurrencePattern Series(DateTime wall, string? zone, Action<ExistingRecurrencePattern>? configure = null)
    {
        var p = new ExistingRecurrencePattern
        {
            RecurrencePatternId = PatternId, CalendarId = CalendarId, Title = "Choir",
            StartDateTime = wall, EndDateTime = wall.AddHours(2), TimeZoneId = zone,
            Frequency = RecurrenceFrequency.Weekly, Interval = 1, DaysOfWeek = DaysOfWeekFlags.Monday, OccurrenceCount = 10,
            Version = LoadedVersion, IsKeyed = true,
        };
        configure?.Invoke(p);
        return p;
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public Harness(string? viewer = "UTC", bool canUpdate = true)
        {
            Context = new CalendarIcsTestContext { ViewerZone = viewer };
            CanUpdate = canUpdate;
        }

        public CalendarIcsTestContext Context { get; }

        public bool CanUpdate { get; }

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut { get; private set; } = default!;

        public List<bool> OpenChanges { get; } = [];

        public int Saved { get; private set; }

        public List<NewCalendarEvent> EventWrites { get; } = [];

        public List<NewRecurrencePattern> SeriesWrites { get; } = [];

        public List<NewRecurrencePattern> Previews { get; } = [];

        public Mock<Odyssey.ApiClient.Resources.ICalendarApiClient> Calendar => Context.Calendar;

        public Harness Open(CalendarEventDialogMode mode, DateOnly? date = null, TimeSpan? time = null)
        {
            Cut = Context.Render<CalendarEventDialog>(new Dictionary<string, object?>
            {
                [nameof(CalendarEventDialog.Open)] = true,
                [nameof(CalendarEventDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(this, v => OpenChanges.Add(v)),
                [nameof(CalendarEventDialog.Mode)] = mode,
                [nameof(CalendarEventDialog.EventId)] = mode == CalendarEventDialogMode.Edit ? EventId : null,
                [nameof(CalendarEventDialog.PatternId)] = mode == CalendarEventDialogMode.Series ? PatternId : null,
                [nameof(CalendarEventDialog.DefaultDate)] = date,
                [nameof(CalendarEventDialog.DefaultTime)] = time,
                [nameof(CalendarEventDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
                [nameof(CalendarEventDialog.CanUpdate)] = CanUpdate,
                [nameof(CalendarEventDialog.CanDelete)] = true,
                [nameof(CalendarEventDialog.OnSaved)] = EventCallback.Factory.Create(this, () => Saved++),
            });
            Cut.WaitForElement(".ods-modal-foot");
            return this;
        }

        public CalendarEventDialog Dialog => Cut.FindComponent<CalendarEventDialog>().Instance;

        public IRenderedComponent<OdsCombobox> Zone => Cut.FindComponent<OdsCombobox>();

        public Task PickZone(string value) => Cut.InvokeAsync(() => Zone.Instance.ValueChanged.InvokeAsync(value));

        public void Save() => Cut.Find("#" + CalendarEventDialog.SaveButtonId).Click();

        private IRenderedComponent<OdsField> TitleField => Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title");

        public Task SetTitle(string title) => Cut.InvokeAsync(() => TitleField.Instance.ValueChanged.InvokeAsync(title));

        public string? Title => TitleField.Instance.Value;

        public IElement Button(string label) =>
            Cut.FindAll(".ods-modal-foot button, .cal-ce-alert button").Single(b => ImportCalendarDialogTests.Harness.Label(b) == label);

        public string Announced => Cut.FindAll("div.sr-only[aria-live=polite]").First().TextContent;

        public string? ZoneHelp => Cut.FindAll("#" + CalendarEventDialog.TimeZoneHelpId).FirstOrDefault()?.TextContent;

        public IReadOnlyList<object?> Focused =>
            [.. Context.Ctx.JSInterop.Invocations["odsFocusById"].Select(i => i.Arguments[0])];

        public void ReturnsWrittenEvents()
        {
            Calendar.Setup(c => c.CreateEventWithResultAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
                .Callback((NewCalendarEvent body, CancellationToken _) => EventWrites.Add(body))
                .ReturnsAsync((NewCalendarEvent body, CancellationToken _) => Ok(Echo(body)));
            Calendar.Setup(c => c.UpdateEventWithResultAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, NewCalendarEvent body, CancellationToken _) => EventWrites.Add(body))
                .ReturnsAsync((Guid _, NewCalendarEvent body, CancellationToken _) => Ok(Echo(body)));
        }

        public void PreviewReturns(int overrides, int exclusions, Guid? version = null, int detached = 0) =>
            Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, NewRecurrencePattern body, CancellationToken _) => Previews.Add(body))
                .ReturnsAsync(Ok(new RecurrencePatternPreview
                {
                    DroppedOverrideCount = overrides, DroppedExclusionCount = exclusions, DetachedCount = detached,
                    Version = version ?? LoadedVersion,
                }));

        public void PutReturns(params ApiResult<RecurrencePatternUpdateResult>[] results)
        {
            var queue = new Queue<ApiResult<RecurrencePatternUpdateResult>>(results);
            Calendar.Setup(c => c.UpdatePatternWithResultAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, NewRecurrencePattern body, CancellationToken _) => SeriesWrites.Add(body))
                .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    internal static ExistingCalendarEvent Echo(NewCalendarEvent body) => new()
    {
        CalendarEventId = EventId, CalendarId = body.CalendarId, Title = body.Title,
        StartDateTime = body.StartDateTime, EndDateTime = body.EndDateTime, TimeZoneId = body.TimeZoneId,
    };

    /// <summary>A hint or zone line's words, without its decorative icon ligature.</summary>
    internal static string LineText(IElement line) => line.QuerySelector("span:last-child")!.TextContent.Trim();

    internal static ApiResult<RecurrencePatternUpdateResult> Updated(int overrides = 0, int exclusions = 0, int detached = 0, int adjusted = 0, DateTime? start = null) =>
        Ok(new RecurrencePatternUpdateResult
        {
            Pattern = Series(start ?? new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo"),
            DroppedOverrideCount = overrides, DroppedExclusionCount = exclusions, DetachedCount = detached, AdjustedCount = adjusted,
        });

    private static Harness OpenSeries(ExistingRecurrencePattern? pattern = null, string viewer = "Europe/Oslo", bool canUpdate = true)
    {
        var h = new Harness(viewer, canUpdate);
        h.Calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pattern ?? Series(new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo"));
        return h.Open(CalendarEventDialogMode.Series);
    }

    private static Harness OpenEvent(ExistingCalendarEvent source, string? viewer = "UTC", bool canUpdate = true)
    {
        var h = new Harness(viewer, canUpdate);
        h.Calendar.Setup(c => c.GetEventAsync(EventId, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        h.ReturnsWrittenEvents();
        return h.Open(CalendarEventDialogMode.Edit);
    }

    // ── AC 1: default zone ───────────────────────────────────────────────────

    [Fact] // AC 1
    public async Task A_new_timed_event_defaults_to_the_browser_zone_shows_it_and_sends_it_with_the_wall_clock()
    {
        await using var h = new Harness("Europe/Oslo");
        h.ReturnsWrittenEvents();
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 6, 4), new TimeSpan(9, 0, 0));

        Assert.Equal("Europe/Oslo", h.Zone.Instance.Value);
        Assert.Equal("Defaults to your device's time zone (Europe/Oslo). The zone is stored with the event.", h.ZoneHelp);

        await h.SetTitle("Standup");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        var sent = h.EventWrites[0];
        Assert.Equal("Europe/Oslo", sent.TimeZoneId);
        Assert.Equal(new DateTime(2026, 6, 4, 9, 0, 0), sent.StartDateTime);
        Assert.Equal(DateTimeKind.Unspecified, sent.StartDateTime.Kind);
    }

    [Fact] // AC 1: no browser zone → UTC
    public async Task Without_a_browser_zone_a_new_event_defaults_to_UTC()
    {
        await using var h = new Harness(viewer: null);
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 6, 4));

        Assert.Equal("UTC", h.Zone.Instance.Value);
        Assert.Contains("(UTC)", h.ZoneHelp, StringComparison.Ordinal);
    }

    // ── AC 2: floating events ────────────────────────────────────────────────

    [Fact] // AC 2
    public async Task A_floating_event_shows_no_time_zone_floating_and_an_unchanged_save_sends_no_zone()
    {
        await using var h = OpenEvent(Floating(new DateTime(2026, 6, 4, 9, 0, 0)), viewer: "America/New_York");

        Assert.Equal(CalendarZoneClock.FloatingValue, h.Zone.Instance.Value);
        Assert.Equal(CalendarZoneClock.FloatingLabel, h.Zone.Instance.Options[0].Label);
        Assert.Equal(CalendarEventCopy.FloatingHelp, h.ZoneHelp);
        Assert.Empty(h.Cut.FindAll("#" + CalendarEventDialog.TwoZoneLineId));

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Null(h.EventWrites[0].TimeZoneId);
        Assert.Equal(new DateTime(2026, 6, 4, 9, 0, 0), h.EventWrites[0].StartDateTime);
        Assert.Equal(DateTimeKind.Utc, h.EventWrites[0].StartDateTime.Kind);
    }

    // ── AC 3 (dialog half): the two-zone line carries the date when it differs ──

    [Fact] // AC 3
    public async Task The_two_zone_line_shows_the_viewers_time_and_their_date_only_when_it_differs()
    {
        await using (var same = OpenEvent(Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"), viewer: "America/New_York"))
        {
            Assert.Equal("Europe/Oslo · 09:00–10:00 · your time 03:00–04:00", LineText(same.Cut.Find("#" + CalendarEventDialog.TwoZoneLineId)));
        }

        await using var other = OpenEvent(Zoned(new DateTime(2026, 1, 15, 1, 0, 0), "Europe/Oslo"), viewer: "America/New_York");
        Assert.Equal("Europe/Oslo · 01:00–02:00 · your time 19:00–20:00 (Wed 14 Jan)",
            LineText(other.Cut.Find("#" + CalendarEventDialog.TwoZoneLineId)));
    }

    // ── AC 4: changing the zone ──────────────────────────────────────────────

    [Fact] // AC 4
    public async Task Changing_the_zone_keeps_the_clock_time_updates_the_help_line_and_announces_once()
    {
        await using var h = new Harness("America/New_York");
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 1, 15), new TimeSpan(9, 0, 0));

        await h.PickZone("Europe/Oslo");

        const string expected = "Still 09:00 in Europe/Oslo; that's 03:00, Thu 15 Jan your time.";
        Assert.Equal(new TimeSpan(9, 0, 0), h.Cut.FindComponents<OdsTimeField>()[0].Instance.Value);
        h.Cut.WaitForAssertion(() => Assert.Equal(expected, h.ZoneHelp));
        h.Cut.WaitForAssertion(() => Assert.Equal(expected, h.Announced));

        await h.PickZone("Europe/Oslo"); // the same zone again is no change, and says nothing new
        Assert.Equal(expected, h.Announced);
    }

    // ── AC 5: DST hints and the adjusted-time toast ──────────────────────────

    [Fact] // AC 5: spring gap — hint before save, adjusted toast after, worded from the server's value
    public async Task A_spring_gap_time_shows_the_gap_hint_then_the_adjusted_toast_from_the_server()
    {
        await using var h = new Harness("Europe/Oslo");
        h.Calendar.Setup(c => c.CreateEventWithResultAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewCalendarEvent body, CancellationToken _) => Ok(Echo(body) with { StartDateTime = new DateTime(2026, 3, 29, 3, 30, 0) }));
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 3, 29), new TimeSpan(2, 30, 0));

        var hint = LineText(h.Cut.Find("#" + CalendarEventDialog.StartHintId));
        Assert.Equal("This time doesn't exist on that day in Europe/Oslo; Odyssey will save it as 03:30.", hint);
        h.Cut.WaitForAssertion(() => Assert.Equal(hint, h.Announced));

        await h.SetTitle("Early train");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.Context.Toasts));
        var toast = h.Context.Toasts[0];
        Assert.Equal("Saved as 03:30. 02:30 doesn't exist on 29 Mar in Europe/Oslo.", toast.Message);
        Assert.True(toast.RequireInteraction);
    }

    [Fact] // AC 5: autumn duplicate — the ambiguous hint
    public async Task An_autumn_duplicate_time_shows_the_ambiguous_hint()
    {
        await using var h = new Harness("Europe/Oslo");
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 10, 25), new TimeSpan(2, 30, 0));

        Assert.Equal("This time occurs twice on that day in Europe/Oslo; Odyssey will save the first one.",
            LineText(h.Cut.Find("#" + CalendarEventDialog.StartHintId)));
    }

    // ── AC 7 / AC 17: the chip ───────────────────────────────────────────────

    [Fact] // AC 7: an override shows the text chip in the editable dialog, with the write-mode hint
    public async Task An_override_shows_the_edited_separately_chip_as_text_in_edit_mode()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 6, 4, 9, 0, 0), "Europe/Oslo",
            e => { e.RecurrencePatternId = PatternId; e.IsOverride = true; }));

        var chip = h.Cut.Find(".cal-series-hint .odc-chip");
        Assert.Equal("Edited separately", chip.TextContent.Replace("edit_calendar", string.Empty).Trim());
        Assert.Contains("outline", chip.ClassList);
        Assert.Contains(CalendarEventCopy.OverrideHint, h.Cut.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC 7 + AC 18: the chip shows read-only too
    public async Task An_override_shows_the_chip_in_the_read_only_view()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 6, 4, 9, 0, 0), "Europe/Oslo",
            e => { e.RecurrencePatternId = PatternId; e.IsOverride = true; }), canUpdate: false);

        Assert.Single(h.Cut.FindAll(".cal-series-hint .odc-chip"));
        Assert.DoesNotContain(CalendarEventCopy.OverrideHint, h.Cut.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC 17: before its series is keyed no chip can show; the delete copy holds either way
    public async Task A_legacy_series_occurrence_shows_no_chip_and_keeps_the_stays_deleted_copy()
    {
        await using var h = OpenEvent(Floating(new DateTime(2026, 6, 4, 9, 0, 0),
            e => { e.RecurrencePatternId = PatternId; e.IsOverride = true; e.IsKeyed = false; }));

        Assert.Empty(h.Cut.FindAll(".odc-chip"));
        Assert.Equal(
            "Permanently delete the event “Standup”? This cannot be undone. " + Odyssey.Client.Pages.Journal.LocationVisuals.RecordDeleteSentence
            + " It stays deleted when the series' details change. A schedule change that no longer includes this date also discards the deletion.",
            CalendarEventDialog.DeleteOccurrenceMessage("Standup"));
    }

    // ── AC 8: preview and confirm ────────────────────────────────────────────

    [Fact] // AC 8: counts above 0 → the confirm step, plural-correct, its heading focused; Back to editing → Save
    public async Task A_preview_with_drops_shows_the_confirm_step_and_back_to_editing_returns_to_save()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(2, 1);
        h.PutReturns(Updated());

        h.Save();

        h.Cut.WaitForElement("#" + CalendarEventDialog.ConfirmHeadingId);
        Assert.Equal(CalendarEventCopy.ConfirmHeading, h.Cut.Find("#" + CalendarEventDialog.ConfirmHeadingId).TextContent.Trim());
        Assert.Equal("2 occurrences you edited separately and 1 occurrence you deleted no longer fall on the new schedule and will be discarded.",
            h.Cut.Find(".cal-ce-confirm-t").TextContent.Trim());
        var region = h.Cut.Find(".cal-ce-confirm");
        Assert.Equal("region", region.GetAttribute("role"));
        Assert.Equal(CalendarEventDialog.ConfirmHeadingId, region.GetAttribute("aria-labelledby"));
        Assert.Equal("-1", h.Cut.Find("#" + CalendarEventDialog.ConfirmHeadingId).GetAttribute("tabindex"));
        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventDialog.ConfirmHeadingId, h.Focused));
        Assert.Empty(h.SeriesWrites);

        h.Button("Back to editing").Click();

        h.Cut.WaitForAssertion(() => Assert.Empty(h.Cut.FindAll(".cal-ce-confirm")));
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarEventDialog.SaveButtonId, h.Focused[^1]));
        Assert.Empty(h.SeriesWrites);
    }

    [Theory] // AC 8: the copy drops a zero clause and agrees in number
    [InlineData(1, 0, "1 occurrence you edited separately no longer falls on the new schedule and will be discarded.")]
    [InlineData(0, 3, "3 occurrences you deleted no longer fall on the new schedule and will be discarded.")]
    [InlineData(2, 1, "2 occurrences you edited separately and 1 occurrence you deleted no longer fall on the new schedule and will be discarded.")]
    public void The_confirm_copy_is_plural_correct_and_omits_a_zero_clause(int overrides, int exclusions, string expected) =>
        Assert.Equal(expected, CalendarEventCopy.ConfirmBody(overrides, exclusions));

    [Fact] // AC 8: both counts 0 → saved directly, bound to the loaded version and {0, 0}
    public async Task With_no_drops_the_series_saves_directly_with_the_loaded_version()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(0, 0);
        h.PutReturns(Updated());

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.SeriesWrites));
        Assert.Empty(h.Cut.FindAll(".cal-ce-confirm"));
        Assert.Equal(LoadedVersion, h.SeriesWrites[0].ExpectedVersion);
        Assert.Equal((0, 0), (h.SeriesWrites[0].ConfirmedDrops!.Overrides, h.SeriesWrites[0].ConfirmedDrops!.Exclusions));
        Assert.Equal(("Series updated.", Severity.Success), (h.Context.Toasts.Single().Message, h.Context.Toasts.Single().Severity));
        Assert.Equal([false], h.OpenChanges);
    }

    // ── AC 9 / AC 21 / AC 25: version conflict ───────────────────────────────

    [Fact] // AC 25: another user renamed the series while this dialog was open → conflict at the preview, nothing written
    public async Task A_preview_carrying_another_version_shows_the_conflict_alert_and_writes_nothing()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(0, 0, version: OtherVersion);
        h.PutReturns(Updated());

        h.Save();

        h.Cut.WaitForElement("#" + CalendarEventDialog.AlertId);
        var alert = h.Cut.Find("#" + CalendarEventDialog.AlertId);
        Assert.Equal(("alert", "-1"), (alert.GetAttribute("role"), alert.GetAttribute("tabindex")));
        Assert.Contains(CalendarEventCopy.Conflict, alert.TextContent, StringComparison.Ordinal);
        Assert.Empty(h.Cut.FindAll(".cal-ce-confirm"));
        Assert.Empty(h.SeriesWrites);
        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventDialog.AlertId, h.Focused));
    }

    [Fact] // AC 9 + AC 21: a PUT 409 version conflict → alert; Keep editing keeps the draft and saves against the new version
    public async Task A_version_conflict_keep_editing_keeps_the_draft_and_the_next_save_uses_the_new_version()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(0, 0);
        h.PutReturns(Fail<RecurrencePatternUpdateResult>(HttpStatusCode.Conflict, "Changed.", CalendarSeriesCodes.VersionConflict), Updated());
        await h.SetTitle("Choir (moved)");

        h.Calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo", p => p.Version = OtherVersion));
        h.Save();
        h.Cut.WaitForElement("#" + CalendarEventDialog.AlertId);
        Assert.True(h.Button("Save series").GetAttribute("aria-disabled") == "true");

        h.Button("Keep editing").Click();

        h.Cut.WaitForAssertion(() => Assert.Empty(h.Cut.FindAll("#" + CalendarEventDialog.AlertId)));
        Assert.Equal("Choir (moved)", h.Title);
        h.PreviewReturns(0, 0, version: OtherVersion);
        h.Save();
        h.Cut.WaitForAssertion(() => Assert.Equal(2, h.SeriesWrites.Count));
        Assert.Equal(OtherVersion, h.SeriesWrites[1].ExpectedVersion);
    }

    [Fact] // AC 21: Reload series discards the draft
    public async Task A_version_conflict_reload_series_discards_the_draft()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(0, 0, version: OtherVersion);
        await h.SetTitle("Choir (moved)");
        h.Save();
        h.Cut.WaitForElement("#" + CalendarEventDialog.AlertId);

        h.Button("Reload series").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal("Choir", h.Title));
        Assert.Empty(h.Cut.FindAll("#" + CalendarEventDialog.AlertId));
    }

    [Fact] // AC 21: a record that has gone → a persistent toast as the dialog closes
    public async Task A_404_closes_the_dialog_with_a_persistent_toast()
    {
        await using var h = OpenSeries();
        h.Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<RecurrencePatternPreview>(HttpStatusCode.NotFound));

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Equal([false], h.OpenChanges));
        var toast = Assert.Single(h.Context.Toasts);
        Assert.Equal((CalendarEventCopy.NotFound, true), (toast.Message, toast.RequireInteraction));
        Assert.Contains("odsFocusChainOrHeading", h.Context.Ctx.JSInterop.Invocations.Select(i => i.Identifier));
    }

    // ── AC 10: the drop toast ────────────────────────────────────────────────

    [Fact] // AC 10
    public async Task A_save_with_drops_shows_a_persistent_warning_with_the_actual_counts_instead_of_series_updated()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(2, 0);
        h.PutReturns(Updated(overrides: 2, detached: 1));
        h.Save();
        h.Cut.WaitForElement("#" + CalendarEventDialog.ConfirmHeadingId);

        h.Button("Save and discard").Click();

        h.Cut.WaitForAssertion(() => Assert.Single(h.Context.Toasts));
        var toast = h.Context.Toasts[0];
        Assert.Equal("Series updated. 2 occurrences you edited separately were discarded because they no longer fall on the new schedule. "
            + "1 past occurrence that duplicated another start is now a separate event.", toast.Message);
        Assert.Equal((Severity.Warning, true), (toast.Severity, toast.RequireInteraction));
        Assert.Equal((2, 0), (h.SeriesWrites[0].ConfirmedDrops!.Overrides, h.SeriesWrites[0].ConfirmedDrops!.Exclusions));
    }

    // ── AC 13: an unrecognised zone ──────────────────────────────────────────

    [Fact] // AC 13
    public async Task A_stored_zone_this_browser_does_not_know_is_shown_as_not_recognised_and_kept()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 6, 4, 9, 0, 0), "Europe/Oslo", e => e.TimeZoneId = "Mars/Olympus_Mons"));

        Assert.Equal("Mars/Olympus_Mons", h.Zone.Instance.Value);
        Assert.Contains(h.Zone.Instance.Options, o => o.Value == "Mars/Olympus_Mons" && o.Label == "Mars/Olympus_Mons (not recognised here)");

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Equal("Mars/Olympus_Mons", h.EventWrites[0].TimeZoneId);
    }

    // ── AC 14 (dialog half): free text blocks save ───────────────────────────

    [Fact] // AC 14: invalid free text blocks save with the field error, and focus moves to the field
    public async Task Free_text_that_matches_no_zone_blocks_save_and_focuses_the_field()
    {
        await using var h = new Harness("Europe/Oslo");
        h.ReturnsWrittenEvents();
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 6, 4));
        await h.SetTitle("Standup");

        h.Cut.Find(".cal-ev-tz .odc-combo-wrap").Input("Atlantis");
        h.Cut.Find(".cal-ev-tz .odc-combo-wrap").TriggerEvent("onfocusout", new FocusEventArgs());

        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventCopy.ZoneError, h.Cut.Markup, StringComparison.Ordinal));
        h.Save();

        Assert.Empty(h.EventWrites);
        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventDialog.TimeZoneInputId, h.Focused));
        Assert.True(h.Zone.Instance.Error);
    }

    [Fact] // AC 14: the server's 400 keyed timeZoneId lands on the field
    public async Task A_400_keyed_time_zone_marks_the_field_and_focuses_it()
    {
        await using var h = new Harness("Europe/Oslo");
        h.Calendar.Setup(c => c.CreateEventWithResultAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingCalendarEvent>.Failure(HttpStatusCode.BadRequest, new ApiProblem
            {
                Status = 400, Title = "Invalid", Errors = new Dictionary<string, string[]> { ["timeZoneId"] = [CalendarEventCopy.ZoneError] },
            }));
        h.Open(CalendarEventDialogMode.Create, new DateOnly(2026, 6, 4));
        await h.SetTitle("Standup");

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventDialog.TimeZoneInputId, h.Focused));
        Assert.True(h.Zone.Instance.Error);
        Assert.Contains(CalendarEventCopy.ZoneError, h.Cut.Markup, StringComparison.Ordinal);
    }

    // ── AC 15 (dialog half): the two-zone line under the inputs, described by them ──

    [Fact] // AC 15 (FR16)
    public async Task The_two_zone_line_renders_under_the_time_inputs_and_describes_them()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"), viewer: "America/New_York");

        var line = h.Cut.Find("#" + CalendarEventDialog.TwoZoneLineId);
        Assert.Null(line.GetAttribute("aria-live"));
        Assert.Null(line.GetAttribute("role"));
        var described = h.Cut.FindAll("[aria-describedby]")
            .Where(e => (e.GetAttribute("aria-describedby") ?? string.Empty).Split(' ').Contains(CalendarEventDialog.TwoZoneLineId))
            .ToList();
        Assert.True(described.Count >= 2, "the start and end inputs both point at the two-zone line");
        // It sits after the time inputs, inside the Ends field.
        var ends = line.ParentElement!;
        Assert.True(ends.QuerySelectorAll(".cal-when-row").Length == 1);
    }

    // ── AC 18: the read-only viewer ──────────────────────────────────────────

    [Fact] // AC 18
    public async Task A_read_only_viewer_sees_converted_times_and_the_zone_but_no_picker_and_no_save()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"), viewer: "America/New_York", canUpdate: false);

        var summary = h.Cut.Find(".cal-ce-ro").TextContent;
        Assert.Contains("Thu 15 Jan, 03:00–04:00 your time", summary, StringComparison.Ordinal);
        Assert.Contains("Europe/Oslo · 09:00–10:00 · your time 03:00–04:00", summary, StringComparison.Ordinal);
        Assert.Empty(h.Cut.FindComponents<OdsCombobox>());
        Assert.Empty(h.Cut.FindAll("#" + CalendarEventDialog.SaveButtonId));
        Assert.Empty(h.Cut.FindAll(".cal-ce-confirm"));
    }

    [Fact] // AC 18: the deleted-occurrences count shows read-only too
    public async Task A_read_only_viewer_sees_the_deleted_occurrences_line()
    {
        await using var h = OpenSeries(Series(new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo", p => p.ExclusionCount = 3), canUpdate: false);

        Assert.Equal("3 occurrences deleted from this series.", h.Cut.Find(".cal-ce-exclusions").TextContent.Replace("event_busy", string.Empty).Trim());
        Assert.Empty(h.Cut.FindAll("#" + CalendarEventDialog.SaveButtonId));
    }

    // ── AC 19: concurrent consent ────────────────────────────────────────────

    [Fact] // AC 19: drops-changed re-previews and re-confirms with the new counts, refocusing the heading
    public async Task Drops_changed_re_previews_and_re_confirms_with_the_new_counts()
    {
        await using var h = OpenSeries();
        // User A previews and sees nothing to discard; user B then edits an occurrence onto a dropped date, so the
        // PUT is refused and the re-preview counts it.
        h.Calendar.SetupSequence(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new RecurrencePatternPreview { Version = LoadedVersion }))
            .ReturnsAsync(Ok(new RecurrencePatternPreview { Version = LoadedVersion, DroppedOverrideCount = 1 }));
        h.PutReturns(Fail<RecurrencePatternUpdateResult>(HttpStatusCode.Conflict, "More.", CalendarSeriesCodes.DropsChanged), Updated());

        h.Save();

        h.Cut.WaitForElement("#" + CalendarEventDialog.ConfirmHeadingId);
        Assert.Equal(CalendarEventCopy.ConfirmHeadingAgain, h.Cut.Find("#" + CalendarEventDialog.ConfirmHeadingId).TextContent.Trim());
        Assert.Contains("1 occurrence you edited separately", h.Cut.Find(".cal-ce-confirm-t").TextContent, StringComparison.Ordinal);
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarEventDialog.ConfirmHeadingId, h.Focused[^1]));
        Assert.Single(h.SeriesWrites);
        Assert.Equal((0, 0), (h.SeriesWrites[0].ConfirmedDrops!.Overrides, h.SeriesWrites[0].ConfirmedDrops!.Exclusions));

        h.Button("Save and discard").Click();
        h.Cut.WaitForAssertion(() => Assert.Equal(2, h.SeriesWrites.Count));
        Assert.Equal(1, h.SeriesWrites[1].ConfirmedDrops!.Overrides);
    }

    [Fact] // AC 19: a busy series says so and Save is usable again
    public async Task A_busy_series_shows_the_busy_alert_and_save_stays_available()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(0, 0);
        h.PutReturns(Fail<RecurrencePatternUpdateResult>(HttpStatusCode.Conflict, "Busy.", CalendarSeriesCodes.Busy));

        h.Save();

        h.Cut.WaitForElement("#" + CalendarEventDialog.AlertId);
        Assert.Contains(CalendarEventCopy.Busy, h.Cut.Find("#" + CalendarEventDialog.AlertId).TextContent, StringComparison.Ordinal);
        Assert.Null(h.Button("Save series").GetAttribute("aria-disabled"));
    }

    // ── AC 24: Escape, inert, preview failure ────────────────────────────────

    [Fact] // AC 24: Escape in the step → back to editing, dialog stays open; the form is inert meanwhile
    public async Task Escape_in_the_confirm_step_returns_to_editing_without_closing_and_the_form_is_inert()
    {
        await using var h = OpenSeries();
        h.PreviewReturns(1, 0);
        h.Save();
        h.Cut.WaitForElement("#" + CalendarEventDialog.ConfirmHeadingId);

        Assert.True(h.Cut.Find(".cal-ev-form").HasAttribute("inert"));
        Assert.False(h.Cut.FindComponent<OdsModal>().Instance.Dismissible);

        h.Cut.Find(".cal-ce-confirm").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        h.Cut.WaitForAssertion(() => Assert.Empty(h.Cut.FindAll(".cal-ce-confirm")));
        Assert.Empty(h.OpenChanges);
        Assert.False(h.Cut.Find(".cal-ev-form").HasAttribute("inert"));
        Assert.True(h.Cut.FindComponent<OdsModal>().Instance.Dismissible);
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarEventDialog.SaveButtonId, h.Focused[^1]));
    }

    [Theory] // AC 24: a failed preview never falls through to the PUT, and its alert takes focus
    [InlineData(HttpStatusCode.InternalServerError, CalendarEventCopy.PreviewFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarEventCopy.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, CalendarEventCopy.Forbidden)]
    public async Task A_failed_preview_shows_an_alert_takes_focus_and_writes_nothing(HttpStatusCode status, string copy)
    {
        await using var h = OpenSeries();
        h.Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<RecurrencePatternPreview>(status));
        h.PutReturns(Updated());

        h.Save();

        h.Cut.WaitForElement("#" + CalendarEventDialog.AlertId);
        Assert.Contains(copy, h.Cut.Find("#" + CalendarEventDialog.AlertId).TextContent, StringComparison.Ordinal);
        Assert.Empty(h.SeriesWrites);
        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventDialog.AlertId, h.Focused));
        // A revoked claim keeps Save unavailable; any other failure lets the user try again.
        Assert.Equal(status == HttpStatusCode.Forbidden ? "true" : null, h.Button("Save series").GetAttribute("aria-disabled"));
    }

    [Fact] // AC 24 (v3 §B): Save is busy and aria-disabled while the preview runs; a second click does nothing
    public async Task Save_is_busy_while_the_preview_runs_and_a_second_click_does_nothing()
    {
        await using var h = OpenSeries();
        var pending = new TaskCompletionSource<ApiResult<RecurrencePatternPreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        h.Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++).Returns(pending.Task);

        h.Save();
        h.Cut.WaitForAssertion(() => Assert.Equal("true", h.Button("Save series").GetAttribute("aria-disabled")));
        Assert.Equal("true", h.Button("Save series").GetAttribute("aria-busy"));
        h.Save();

        Assert.Equal(1, calls);
        pending.SetResult(Ok(new RecurrencePatternPreview { Version = LoadedVersion, DroppedOverrideCount = 1 }));
        h.Cut.WaitForElement(".cal-ce-confirm");
    }

    // ── v6 F2: the detached occurrence ───────────────────────────────────────

    [Fact] // v6 F2: an edit that landed on a detached row says so in an Info toast
    public async Task An_edit_to_an_occurrence_that_had_detached_says_so()
    {
        await using var h = OpenEvent(Floating(new DateTime(2026, 6, 4, 9, 0, 0), e => e.RecurrencePatternId = PatternId));
        h.Calendar.Setup(c => c.UpdateEventWithResultAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NewCalendarEvent body, CancellationToken _) => Ok(Echo(body) with { Detached = true, DetachedCount = 2 }));

        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.Context.Toasts));
        Assert.Equal(CalendarEventCopy.DetachedEdit + " " + CalendarEventCopy.DetachedNow(2), h.Context.Toasts[0].Message);
        Assert.Equal(Severity.Info, h.Context.Toasts[0].Severity);
    }

    // ── Edit series… while open ──────────────────────────────────────────────

    [Fact] // §3 / v5 B1: "Edit series…" switches the open dialog to the series, loading it and its version
    public async Task Switching_an_open_dialog_to_its_series_loads_the_series_and_its_version()
    {
        await using var context = new CalendarIcsTestContext { ViewerZone = "Europe/Oslo" };
        context.Calendar.Setup(c => c.GetEventAsync(EventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Zoned(new DateTime(2026, 6, 8, 18, 0, 0), "Europe/Oslo", e => { e.Title = "Choir — concert"; e.RecurrencePatternId = PatternId; }));
        context.Calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo"));
        context.Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new RecurrencePatternPreview { Version = LoadedVersion, DroppedOverrideCount = 1 }));
        var mode = CalendarEventDialogMode.Edit;
        var cut = context.Ctx.Render<RefreshHost>(p => p.AddChildContent(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<CalendarEventDialog>(2);
            b.AddComponentParameter(3, nameof(CalendarEventDialog.Open), true);
            b.AddComponentParameter(4, nameof(CalendarEventDialog.Mode), mode);
            b.AddComponentParameter(5, nameof(CalendarEventDialog.EventId), mode == CalendarEventDialogMode.Edit ? EventId : null);
            b.AddComponentParameter(6, nameof(CalendarEventDialog.PatternId), mode == CalendarEventDialogMode.Series ? PatternId : null);
            b.AddComponentParameter(7, nameof(CalendarEventDialog.Calendars), (IReadOnlyList<ExistingCalendar>)[Personal]);
            b.AddComponentParameter(8, nameof(CalendarEventDialog.CanUpdate), true);
            b.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Contains("Choir — concert", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.Value));

        mode = CalendarEventDialogMode.Series;
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        cut.WaitForAssertion(() => Assert.Equal("Choir", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.Value));
        cut.Find("#" + CalendarEventDialog.SaveButtonId).Click();
        // The preview carries the loaded version, so no conflict: the confirm step shows.
        cut.WaitForElement(".cal-ce-confirm");
    }

    // ── v4 §4: an occurrence's zone ──────────────────────────────────────────

    [Fact] // v4 §4: an occurrence of a floating series shows the zone as read-only text
    public async Task An_occurrence_of_a_floating_series_shows_the_zone_as_read_only_text()
    {
        await using var h = OpenEvent(Floating(new DateTime(2026, 6, 4, 9, 0, 0), e => e.RecurrencePatternId = PatternId));

        Assert.Empty(h.Cut.FindComponents<OdsCombobox>());
        Assert.Contains(CalendarEventCopy.FloatingOccurrence, h.Cut.Find(".cal-ce-zone-ro").TextContent, StringComparison.Ordinal);
    }

    [Fact] // v4 §4: an occurrence of a zoned series offers zones but not floating, and says other viewers convert it
    public async Task An_occurrence_of_a_zoned_series_offers_zones_but_not_floating()
    {
        await using var h = OpenEvent(Zoned(new DateTime(2026, 6, 4, 9, 0, 0), "Europe/Oslo", e => e.RecurrencePatternId = PatternId));

        Assert.DoesNotContain(h.Zone.Instance.Options, o => o.Value == CalendarZoneClock.FloatingValue);
        Assert.Contains(CalendarEventCopy.OccurrenceOtherViewers, h.ZoneHelp, StringComparison.Ordinal);

        await h.PickZone("Europe/London");
        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventCopy.OccurrenceBecomesOverride, h.ZoneHelp, StringComparison.Ordinal));
    }

    [Fact] // v4 §4: converting a floating series names the zone every occurrence will be read in
    public async Task Choosing_a_zone_on_a_floating_series_says_every_occurrence_is_read_in_it()
    {
        await using var h = OpenSeries(Series(new DateTime(2026, 6, 1, 18, 0, 0), null));

        Assert.Equal(CalendarZoneClock.FloatingValue, h.Zone.Instance.Value);
        await h.PickZone("Europe/Oslo");

        h.Cut.WaitForAssertion(() => Assert.Contains(CalendarEventCopy.SeriesReadAs("Europe/Oslo"), h.ZoneHelp, StringComparison.Ordinal));
    }
}
