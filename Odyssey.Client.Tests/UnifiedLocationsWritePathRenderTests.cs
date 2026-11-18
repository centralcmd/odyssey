using System.Net;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #383 AC 1 through the real components (senior-tester M1). The server's PUT is a full replacement, so a
/// write path that drops <c>locationId</c> silently clears a record's place. Each test renders the component,
/// drives the path through the component's own wiring (the grid's drop/change callbacks, the row menu's
/// Archive item, the attachment handler, the series dialog's Save) and captures the body a STRICT API mock
/// received — any call the path should not make fails the test.
/// </summary>
/// <remarks>
/// <c>CalendarPage</c> and <c>JournalCard</c> load nothing outside a browser (<c>OperatingSystem.IsBrowser()</c>),
/// so the two pieces of state their browser-only initialisers would have set — the update permission and, for
/// the calendar, the view — are set by name; the events themselves still arrive through the page's own load.
/// A renamed member fails <see cref="Field"/> loudly rather than letting a test pass vacuously.
/// </remarks>
public class UnifiedLocationsWritePathRenderTests
{
    private static readonly Guid CalendarId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid EventId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid PatternId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid EntryId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid FileKept = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid FileRemoved = Guid.Parse("ffffffff-0000-0000-0000-000000000002");

    // ── Calendar: month-grid drag and time-grid resize ─────────────────────────────

    private static ExistingCalendarEvent StoredEvent(DateTime start) => new()
    {
        CalendarEventId = EventId,
        CalendarId = CalendarId,
        Title = "Dinner",
        Location = "Room 4B",
        LocationId = HyttaId,
        StartDateTime = start,
        EndDateTime = start.AddHours(2),
    };

    private sealed record CalendarPageHarness(
        BunitContext Ctx, IRenderedComponent<CalendarPage> Cut, List<NewCalendarEvent> Puts);

    private static async Task<CalendarPageHarness> RenderCalendarPageAsync(string view)
    {
        var ctx = Context();
        var start = DateTime.Today.AddHours(18);
        var puts = new List<NewCalendarEvent>();
        var calendar = new Mock<ICalendarApiClient>(MockBehavior.Strict);
        calendar.Setup(c => c.ListEventsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingCalendarEvent>>.Success([StoredEvent(start)], HttpStatusCode.OK));
        calendar.Setup(c => c.UpdateEventAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, NewCalendarEvent body, CancellationToken _) => puts.Add(body))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        ctx.Services.AddSingleton(calendar.Object);
        ctx.Services.AddSingleton(Locations(Hytta()).Object);
        ctx.Services.AddSingleton(Mock.Of<IPageStateService>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddFallbackServiceProvider(new LooseMocks());

        var cut = ctx.Render<CalendarPage>();
        Field(cut.Instance, "_canUpdate").SetValue(cut.Instance, true);
        Field(cut.Instance, "_view").SetValue(cut.Instance, view);
        await cut.InvokeAsync(() => Call(cut.Instance, "LoadEventsAsync"));
        return new(ctx, cut, puts);
    }

    [Fact]
    public async Task A_month_grid_drag_keeps_the_events_place()
    {
        var h = await RenderCalendarPageAsync("month");
        await using var _ = h.Ctx;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var grid = h.Cut.FindComponent<OdsCalendarGrid>().Instance;
        await h.Cut.InvokeAsync(() => grid.OnEventDrop.InvokeAsync((EventId, today.AddDays(2), today)));

        var put = Assert.Single(h.Puts);
        Assert.Equal((HyttaId, "Room 4B"), (put.LocationId, put.Location));
        Assert.Equal(DateTime.Today.AddDays(2).AddHours(18), put.StartDateTime);
    }

    [Fact]
    public async Task A_time_grid_resize_keeps_the_events_place()
    {
        var h = await RenderCalendarPageAsync("week");
        await using var _ = h.Ctx;
        var newStart = DateTime.Today.AddHours(17);

        var grid = h.Cut.FindComponent<OdsCalTimeGrid>().Instance;
        await h.Cut.InvokeAsync(() => grid.OnEventChange.InvokeAsync((EventId, newStart, newStart.AddHours(3))));

        var put = Assert.Single(h.Puts);
        Assert.Equal((HyttaId, "Room 4B"), (put.LocationId, put.Location));
        Assert.Equal((newStart, newStart.AddHours(3)), (put.StartDateTime, put.EndDateTime));
    }

    // ── Journal: archive and attachment removal ────────────────────────────────

    private static ExistingJournalEntry StoredEntry() => new()
    {
        JournalEntryId = EntryId,
        Title = "Cabin trip",
        Content = "Snow.",
        EntryDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        Location = "By the lake",
        LocationId = HyttaId,
        CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        Attachments =
        [
            new JournalEntryAttachmentDto { JournalEntryAttachmentId = Guid.NewGuid(), FileId = FileKept, CreatedAt = DateTime.UtcNow },
            new JournalEntryAttachmentDto { JournalEntryAttachmentId = Guid.NewGuid(), FileId = FileRemoved, CreatedAt = DateTime.UtcNow },
        ],
    };

    private static JournalEntrySummary Summary() => new()
    {
        JournalEntryId = EntryId,
        Title = "Cabin trip",
        Content = "Snow.",
        EntryDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        LocationId = HyttaId,
        CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        AttachmentCount = 2,
    };

    private sealed record JournalHarness(BunitContext Ctx, IRenderedComponent<JournalCard> Cut, List<UpdateJournalEntry> Puts);

    private static JournalHarness RenderJournalCard()
    {
        var ctx = Context();
        var puts = new List<UpdateJournalEntry>();
        var journal = new Mock<IJournalApiClient>(MockBehavior.Strict);
        journal.Setup(j => j.GetAsync(EntryId, It.IsAny<CancellationToken>())).ReturnsAsync(StoredEntry());
        journal.Setup(j => j.UpdateAsync(EntryId, It.IsAny<UpdateJournalEntry>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateJournalEntry body, CancellationToken _) => puts.Add(body))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        // The reload after a successful write is a read, and allowed; every write but the one under test fails.
        journal.Setup(j => j.ListAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<JournalEntrySummary>>.Success([Summary()], HttpStatusCode.OK));
        ctx.Services.AddSingleton(journal.Object);
        // Every other collaborator of the card and its child dialogs is a loose mock: this test is about the
        // one strict client above.
        ctx.Services.AddFallbackServiceProvider(new LooseMocks());

        var cut = ctx.Render<JournalCard>();
        Field(cut.Instance, "_canUpdate").SetValue(cut.Instance, true);
        return new(ctx, cut, puts);
    }

    [Fact]
    public async Task Archiving_an_entry_from_its_row_menu_keeps_its_place()
    {
        var h = RenderJournalCard();
        await using var _ = h.Ctx;

        var menu = MenuFor(h.Cut.Instance, Summary());
        var archive = menu.Single(item => item.Label == "Archive");
        await h.Cut.InvokeAsync(() => archive.OnClick.InvokeAsync());

        var put = Assert.Single(h.Puts);
        Assert.Equal((HyttaId, "By the lake"), (put.LocationId, put.Location));
        Assert.True(put.Archived);
    }

    [Fact]
    public async Task Removing_an_attachment_keeps_the_entrys_place_and_its_other_files()
    {
        var h = RenderJournalCard();
        await using var _ = h.Ctx;

        await h.Cut.InvokeAsync(() => Call(h.Cut.Instance, "RemoveAttachmentAsync", Summary(), FileRemoved));

        var put = Assert.Single(h.Puts);
        Assert.Equal((HyttaId, "By the lake"), (put.LocationId, put.Location));
        Assert.Equal([FileKept], put.AttachmentFileIds);
    }

    // ── The series dialog ────────────────────────────────────────────────────────

    [Fact]
    public async Task Saving_the_series_dialog_keeps_the_series_place()
    {
        var ctx = Context();
        await using var _ = ctx;
        var puts = new List<NewRecurrencePattern>();
        var calendar = new Mock<ICalendarApiClient>(MockBehavior.Strict);
        calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>())).ReturnsAsync(new ExistingRecurrencePattern
        {
            RecurrencePatternId = PatternId,
            CalendarId = CalendarId,
            Title = "Choir",
            Location = "Side door",
            LocationId = HyttaId,
            StartDateTime = new DateTime(2026, 3, 2, 18, 0, 0),
            EndDateTime = new DateTime(2026, 3, 2, 20, 0, 0),
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 10,
        });
        calendar.Setup(c => c.UpdatePatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, NewRecurrencePattern body, CancellationToken _) => puts.Add(body))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        ctx.Services.AddSingleton(calendar.Object);
        ctx.Services.AddSingleton(Locations(Hytta()).Object);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CalendarEventDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Mode, CalendarEventDialogMode.Series)
            .Add(x => x.PatternId, PatternId)
            .Add(x => x.Calendars, [new ExistingCalendar { CalendarId = CalendarId, Name = "Family", Color = "#4F8EF7" }])
            .Add(x => x.CanUpdate, true)
            .Add(x => x.CanReadLocations, true)
            .Add(x => x.CanCreateLocations, true)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsLocationSelect>()));
        cut.FindAll("button").First(b => b.TextContent.Contains("Save series", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Single(puts));
        Assert.Equal((HyttaId, "Side door"), (puts[0].LocationId, puts[0].Location));
    }

    /// <summary>A loose Moq mock for any Odyssey interface nothing registered explicitly.</summary>
    private sealed class LooseMocks : IServiceProvider
    {
        private readonly Dictionary<Type, object> made = [];

        public object? GetService(Type serviceType)
        {
            // Only the application's own collaborators: the framework's (the component activator, …) must keep
            // their real defaults.
            if (!serviceType.IsInterface || serviceType.Namespace?.StartsWith("Odyssey.", StringComparison.Ordinal) != true)
                return null;
            if (!made.TryGetValue(serviceType, out var mock))
            {
                mock = ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(serviceType))!).Object;
                made[serviceType] = mock;
            }

            return mock;
        }
    }

    // ── Reflection helpers (see remarks) ─────────────────────────────────────────

    private static FieldInfo Field(object component, string name) =>
        component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{component.GetType().Name}.{name} was renamed; update this test.");

    private static Task Call(object component, string name, params object[] args) =>
        (Task)(component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{component.GetType().Name}.{name} was renamed; update this test."))
        .Invoke(component, args)!;

    /// <summary>The entry's row menu, built by the card's own menu builder.</summary>
    private static IReadOnlyList<OdsMenuItem> MenuFor(JournalCard card, JournalEntrySummary entry)
    {
        var builder = card.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.ReturnType.IsAssignableTo(typeof(IEnumerable<OdsMenuItem>))
                && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(JournalEntrySummary));
        return [.. (IEnumerable<OdsMenuItem>)builder.Invoke(card, [entry])!];
    }
}
