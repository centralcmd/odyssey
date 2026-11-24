using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The event dialog's half of issue #522 (calendar event attachments): the shared Photos and Attachments fields, the
/// read-only "From the series" section, the keep-or-set write, the live cap and the announcements. Each test names the
/// acceptance criterion it proves. The page surfaces are <see cref="CalendarAttachmentPageTests"/>, the import
/// <see cref="CalendarAttachmentImportTests"/>.
/// </summary>
public class CalendarAttachmentDialogTests
{
    static CalendarAttachmentDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    internal static readonly Guid EventId = Guid.Parse("522e0000-0000-0000-0000-000000000001");
    internal static readonly Guid PatternId = Guid.Parse("522e0000-0000-0000-0000-000000000002");
    internal static readonly Guid Version = Guid.Parse("522e0000-0000-0000-0000-0000000000a1");

    internal static CalendarLinkFileDto File(string name, Guid? id = null) => new()
    {
        FileId = id ?? Guid.NewGuid(),
        CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        FileName = name,
        ContentType = "application/pdf",
        SizeBytes = 2048,
        UploadedAtUtc = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
    };

    internal static CalendarLinkPhotoDto Photo(string title, int position, Guid? id = null) => new()
    {
        PhotoId = id ?? Guid.NewGuid(),
        Position = position,
        CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        Title = title,
        FileName = $"{title}.jpg",
    };

    /// <summary>A standalone event with <paramref name="files"/> and <paramref name="photos"/> linked, as a detail read returns it.</summary>
    internal static ExistingCalendarEvent Standalone(IEnumerable<CalendarLinkFileDto>? files = null, IEnumerable<CalendarLinkPhotoDto>? photos = null)
    {
        var f = files?.ToList() ?? [];
        var p = photos?.ToList() ?? [];
        return new ExistingCalendarEvent
        {
            CalendarEventId = EventId,
            CalendarId = CalendarId,
            Title = "Dentist",
            StartDateTime = new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Unspecified),
            EndDateTime = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Unspecified),
            TimeZoneId = "UTC",
            IsKeyed = true,
            Attachments = f,
            Photos = p,
            AttachmentCount = f.Count,
            PhotoCount = p.Count,
        };
    }

    /// <summary>The same detail read as a caller without the read claims gets it: ids and counts, no names (#521 §5.3).</summary>
    internal static ExistingCalendarEvent Withheld(ExistingCalendarEvent source, bool files, bool photos)
    {
        if (files)
        {
            source.Attachments = [.. source.Attachments!.Select(a => new CalendarLinkFileDto { FileId = a.FileId, CreatedAt = a.CreatedAt })];
        }

        if (photos)
        {
            source.Photos = [.. source.Photos!.Select(p => new CalendarLinkPhotoDto { PhotoId = p.PhotoId, Position = p.Position, CreatedAt = p.CreatedAt })];
        }

        return source;
    }

    internal static ExistingCalendarEvent Occurrence(IEnumerable<CalendarLinkFileDto>? files = null, IEnumerable<CalendarLinkPhotoDto>? photos = null)
    {
        var e = Standalone(files, photos);
        e.RecurrencePatternId = PatternId;
        e.LinksFromSeries = true;
        return e;
    }

    internal static ExistingRecurrencePattern Series(IEnumerable<CalendarLinkFileDto>? files = null, IEnumerable<CalendarLinkPhotoDto>? photos = null)
    {
        var f = files?.ToList() ?? [];
        var p = photos?.ToList() ?? [];
        return new ExistingRecurrencePattern
        {
            RecurrencePatternId = PatternId,
            CalendarId = CalendarId,
            Title = "Choir",
            StartDateTime = new DateTime(2026, 6, 1, 18, 0, 0),
            EndDateTime = new DateTime(2026, 6, 1, 20, 0, 0),
            TimeZoneId = "UTC",
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 10,
            Version = Version,
            IsKeyed = true,
            Attachments = f,
            Photos = p,
            AttachmentCount = f.Count,
            PhotoCount = p.Count,
        };
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public Harness(int? cap = 10, bool readFiles = true, bool readPhotos = true, bool canUpdate = true, ImportLimitsDto? limits = null)
        {
            Context.CurrentLimits = limits ?? (ImportLimitsCache.Fallback with { CalendarEventMaxLinksPerKind = cap });
            ReadFiles = readFiles;
            ReadPhotos = readPhotos;
            CanUpdate = canUpdate;
            FocusReturn = Context.Ctx.JSInterop.SetupModule("./js/focus-return.js");
            Context.Ctx.Services.AddSingleton<TimeProvider>(Time);
            var upload = new Mock<IUploadLimitsCache>();
            upload.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(Context.Ctx.Services, upload.Object);
            var photos = new Mock<IPhotosApiClient>();
            photos.Setup(p => p.ContentUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://api.test/api/photos/{id}/content");
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(Context.Ctx.Services, photos.Object);
            Context.Calendar.Setup(c => c.CreateEventWithResultAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
                .Callback((NewCalendarEvent body, CancellationToken _) => EventWrites.Add(body))
                .ReturnsAsync((NewCalendarEvent body, CancellationToken _) => Ok(Echo(body)));
            Context.Calendar.Setup(c => c.UpdateEventWithResultAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, NewCalendarEvent body, CancellationToken _) => EventWrites.Add(body))
                .ReturnsAsync((Guid _, NewCalendarEvent body, CancellationToken _) => Ok(Echo(body)));
            Context.Calendar.Setup(c => c.CreatePatternWithResultAsync(It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
                .Callback((NewRecurrencePattern body, CancellationToken _) => SeriesWrites.Add(body))
                .ReturnsAsync((NewRecurrencePattern _, CancellationToken _) => Ok(Series()));
            Context.Calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new RecurrencePatternPreview { Version = Version }));
            Context.Calendar.Setup(c => c.UpdatePatternWithResultAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, NewRecurrencePattern body, CancellationToken _) => SeriesWrites.Add(body))
                .ReturnsAsync(Ok(new RecurrencePatternUpdateResult { Pattern = Series() }));
        }

        // The principal also holds photos.read, so the tiles draw their images (the dialog's own claim flags are parameters).
        public CalendarIcsTestContext Context { get; } = new([.. AllCalendarClaims,
            Odyssey.Dtos.Authorization.PermissionClaims.PhotosRead, Odyssey.Dtos.Authorization.PermissionClaims.PhotosCreate,
            Odyssey.Dtos.Authorization.PermissionClaims.FilesRead]);

        public bool ReadFiles { get; }

        public bool ReadPhotos { get; }

        public bool CanUpdate { get; }

        public BunitJSModuleInterop FocusReturn { get; }

        /// <summary>The dialog's clock: it stands still unless a test moves it.</summary>
        public FakeTimeProvider Time { get; } = new();

        public List<NewCalendarEvent> EventWrites { get; } = [];

        public List<NewRecurrencePattern> SeriesWrites { get; } = [];

        public List<Guid> EditSeries { get; } = [];

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut { get; private set; } = default!;

        public Mock<ICalendarApiClient> Calendar => Context.Calendar;

        public Harness Open(CalendarEventDialogMode mode)
        {
            Cut = Context.Render<CalendarEventDialog>(new Dictionary<string, object?>
            {
                [nameof(CalendarEventDialog.Open)] = true,
                [nameof(CalendarEventDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(this, _ => { }),
                [nameof(CalendarEventDialog.Mode)] = mode,
                [nameof(CalendarEventDialog.EventId)] = mode == CalendarEventDialogMode.Edit ? EventId : null,
                [nameof(CalendarEventDialog.PatternId)] = mode == CalendarEventDialogMode.Series ? PatternId : null,
                [nameof(CalendarEventDialog.DefaultDate)] = new DateOnly(2026, 6, 4),
                [nameof(CalendarEventDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
                [nameof(CalendarEventDialog.CanUpdate)] = CanUpdate,
                [nameof(CalendarEventDialog.CanDelete)] = true,
                [nameof(CalendarEventDialog.CanReadFiles)] = ReadFiles,
                [nameof(CalendarEventDialog.CanReadPhotos)] = ReadPhotos,
                [nameof(CalendarEventDialog.OnEditSeries)] = EventCallback.Factory.Create<Guid>(this, id => EditSeries.Add(id)),
            });
            Cut.WaitForElement(".ods-modal-foot");
            Cut.WaitForAssertion(() => Assert.Empty(Cut.FindAll("[aria-busy=true]")));
            return this;
        }

        public Harness OpenEvent(ExistingCalendarEvent source)
        {
            Calendar.Setup(c => c.GetEventAsync(EventId, It.IsAny<CancellationToken>())).ReturnsAsync(source);
            return Open(CalendarEventDialogMode.Edit);
        }

        public Harness OpenSeries(ExistingRecurrencePattern source)
        {
            Calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>())).ReturnsAsync(source);
            return Open(CalendarEventDialogMode.Series);
        }

        public AttachmentLinkFields Fields => Cut.FindComponent<AttachmentLinkFields>().Instance;

        public void Save() => Cut.Find("#" + CalendarEventDialog.SaveButtonId).Click();

        public Task SetTitle(string title) =>
            Cut.InvokeAsync(() => Cut.FindComponents<Odyssey.Client.Components.OdsField>().Single(f => f.Instance.Label == "Title")
                .Instance.ValueChanged.InvokeAsync(title));

        public IElement Button(string label) => Buttons(label).Single();

        /// <summary>The buttons named <paramref name="label"/>: by their text, or by their aria-label (an icon button).</summary>
        public IReadOnlyList<IElement> Buttons(string label) =>
            [.. Cut.FindAll("button").Where(b => ImportCalendarDialogTests.Harness.Label(b) == label || b.GetAttribute("aria-label") == label)];

        /// <summary>The Photos field's tiles (the shared OdsPhotoTile), in order.</summary>
        public IReadOnlyList<Odyssey.Client.Components.OdsPhotoTile> Tiles =>
            [.. Cut.FindComponents<Odyssey.Client.Components.OdsPhotoTile>().Select(t => t.Instance)];

        /// <summary>The tile's ⋯ menu item labels, in order (dividers skipped).</summary>
        public IReadOnlyList<string?> MenuOf(string name) =>
            [.. Tiles.Single(t => t.Name == name).Actions!.Where(a => !a.Divider).Select(a => a.Label)];

        /// <summary>Removes a photo through its tile's ⋯ menu item (invoked directly: the menu is a MudBlazor popover).</summary>
        public Task RemovePhoto(string name) =>
            Cut.InvokeAsync(() => Tiles.Single(t => t.Name == name).Actions!.Single(a => a.Label == "Remove from event").OnClick.InvokeAsync());

        /// <summary>Every message the dialog's one announcer was asked to speak.</summary>
        public string Announced => Cut.FindAll("div.sr-only[aria-live=polite]").First().TextContent;

        /// <summary>Picks <paramref name="count"/> new library photos through the Add photos picker, as its OnSubmit hands them over.</summary>
        public async Task PickPhotosAsync(int count)
        {
            Button("Add photos").Click();
            var picker = Cut.WaitForComponent<AttachPhotosDialog>();
            IReadOnlyList<AttachPhotoItem> items =
                [.. Enumerable.Range(1, count).Select(i => new AttachPhotoItem(AttachDocumentSource.Library, Guid.NewGuid(), $"picked-{i}.jpg"))];
            await Cut.InvokeAsync(() => picker.Instance.OnSubmit.InvokeAsync(items));
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    internal static ExistingCalendarEvent Echo(NewCalendarEvent body) => new()
    {
        CalendarEventId = EventId,
        CalendarId = body.CalendarId,
        Title = body.Title,
        StartDateTime = body.StartDateTime,
        EndDateTime = body.EndDateTime,
        TimeZoneId = body.TimeZoneId,
    };

    private static string Text(IElement e) => ImportCalendarDialogTests.Harness.Label(e);

    // ── AC 1: create, edit, save ─────────────────────────────────────────────

    [Theory] // AC 1: create, standalone-edit and edit-series modes show both fields
    [InlineData(CalendarEventDialogMode.Create)]
    [InlineData(CalendarEventDialogMode.Edit)]
    [InlineData(CalendarEventDialogMode.Series)]
    public async Task Create_edit_and_series_modes_show_both_fields(CalendarEventDialogMode mode)
    {
        await using var h = new Harness();
        _ = mode switch
        {
            CalendarEventDialogMode.Edit => h.OpenEvent(Standalone()),
            CalendarEventDialogMode.Series => h.OpenSeries(Series()),
            _ => h.Open(mode),
        };

        Assert.Single(h.Buttons("Add photos"));
        Assert.Single(h.Buttons("Attach documents"));
        Assert.Contains(h.Cut.FindAll(".odc-field-label"), l => l.TextContent.Trim() == "Photos");
        Assert.Contains(h.Cut.FindAll(".odc-field-label"), l => l.TextContent.Trim() == "Attachments");
        Assert.Empty(h.Cut.FindAll(".cal-ce-fromseries"));
    }

    [Fact] // AC 1: a save without changes sends null arrays, through CalendarWrites
    public async Task An_unchanged_save_sends_null_arrays()
    {
        await using var h = new Harness();
        h.OpenEvent(Standalone([File("lease.pdf")], [Photo("Beach", 0)]));

        await h.SetTitle("Dentist (moved)");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
        Assert.Null(h.EventWrites[0].PhotoIds);
    }

    [Fact] // AC 1: a save with changes sends the changed kind as a Set array, and keeps the other
    public async Task A_removal_sends_that_kinds_remaining_ids_and_keeps_the_other_kind()
    {
        await using var h = new Harness();
        var keep = Photo("Keep", 0);
        var drop = Photo("Drop", 1);
        h.OpenEvent(Standalone([File("lease.pdf")], [keep, drop]));

        await h.RemovePhoto("Drop");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Equal([keep.PhotoId], h.EventWrites[0].PhotoIds!);
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
    }

    [Fact] // AC 1: picked photos on a new event are sent; an untouched kind is not
    public async Task Photos_picked_on_a_new_event_are_sent_with_the_create()
    {
        await using var h = new Harness();
        h.Open(CalendarEventDialogMode.Create);
        await h.SetTitle("Hike");

        await h.PickPhotosAsync(2);
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Equal(2, h.EventWrites[0].PhotoIds!.Length);
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
    }

    [Fact] // AC 1: edit-series mode sends the series' changed links on the PUT, and its file rows hydrate from the read
    public async Task Edit_series_mode_hydrates_the_rows_and_sends_a_changed_kind_on_the_put()
    {
        await using var h = new Harness();
        var lease = File("lease.pdf");
        var minutes = File("minutes.pdf");
        h.OpenSeries(Series([lease, minutes]));

        Assert.Equal(["lease.pdf", "minutes.pdf"], h.Cut.FindAll(".jec-file-name").Select(n => n.TextContent));
        var rows = h.Cut.FindComponent<JournalFileRows>();
        await h.Cut.InvokeAsync(() => rows.Instance.OnRemove!.Value.InvokeAsync(lease.FileId));
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.SeriesWrites));
        Assert.Equal([minutes.FileId], h.SeriesWrites[0].AttachmentFileIds!);
        Assert.Null(h.SeriesWrites[0].PhotoIds);
        h.Context.Calendar.Verify(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>()), Times.Once);
        // B5: the rows come from the hydrated read, with no per-file metadata call.
        Mock.Get((IFilesApiClient)h.Context.Ctx.Services.GetService(typeof(IFilesApiClient))!)
            .Verify(f => f.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── AC 2: keep on drag and resize ────────────────────────────────────────

    [Fact] // AC 2 (B1): a month-grid drag sends null arrays
    public void A_drop_keeps_the_links()
    {
        var update = CalendarPage.DropUpdate(Standalone([File("a.pdf")], [Photo("p", 0)]), new DateOnly(2026, 6, 6), new DateOnly(2026, 6, 4));

        Assert.Null(update.AttachmentFileIds);
        Assert.Null(update.PhotoIds);
    }

    [Fact] // AC 2 (B1): a time-grid resize sends null arrays
    public void A_resize_keeps_the_links()
    {
        var update = CalendarPage.ResizeUpdate(Standalone([File("a.pdf")], [Photo("p", 0)]),
            new DateTime(2026, 6, 4, 9, 0, 0), new DateTime(2026, 6, 4, 11, 0, 0), TimeZoneInfo.Utc);

        Assert.Null(update.AttachmentFileIds);
        Assert.Null(update.PhotoIds);
    }

    [Fact] // AC 2 (B1): a template-only series edit sends null arrays
    public async Task A_template_only_series_edit_keeps_the_links()
    {
        await using var h = new Harness();
        h.OpenSeries(Series([File("lease.pdf")], [Photo("Beach", 0)]));

        await h.SetTitle("Choir (new hall)");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.SeriesWrites));
        Assert.Null(h.SeriesWrites[0].AttachmentFileIds);
        Assert.Null(h.SeriesWrites[0].PhotoIds);
    }

    // ── AC 3: hidden but kept ────────────────────────────────────────────────

    [Theory] // AC 3 (B2, SX1): without a kind's read claim, the field is a count line and a rename keeps that kind
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Without_a_read_claim_a_rename_keeps_the_hidden_links(bool readFiles, bool readPhotos)
    {
        await using var h = new Harness(readFiles: readFiles, readPhotos: readPhotos);
        var source = Withheld(Standalone([File("a.pdf"), File("b.pdf")], [Photo("p", 0)]), files: !readFiles, photos: !readPhotos);
        h.OpenEvent(source);

        var lines = h.Cut.FindAll(".alf-countline").Select(Text).ToList();
        if (!readFiles)
        {
            Assert.Contains("2 documents attached. You don't have permission to view documents.", lines);
            Assert.Empty(h.Buttons("Attach documents"));
            Assert.Empty(h.Cut.FindAll(".jec-file"));
        }

        if (!readPhotos)
        {
            Assert.Contains("1 photo attached. You don't have permission to view photos.", lines);
            Assert.Empty(h.Buttons("Add photos"));
            Assert.Empty(h.Tiles);
        }

        await h.SetTitle("Renamed");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
        Assert.Null(h.EventWrites[0].PhotoIds);
    }

    // ── AC 4: occurrence ─────────────────────────────────────────────────────

    [Fact] // AC 4: the read-only section — h3 under an h2 title, a real list, alt text, no remove, and a save that keeps
    public async Task An_occurrence_shows_the_series_links_read_only_and_its_save_keeps_them()
    {
        await using var h = new Harness();
        h.OpenEvent(Occurrence([File("agenda.pdf")], [Photo("Stage", 0), Photo("Hall", 1)]));

        Assert.NotNull(h.Cut.Find(".mud-dialog h2"));
        var section = h.Cut.Find("section.cal-ce-fromseries");
        Assert.Equal("From the series", section.QuerySelector("h3")!.TextContent);
        Assert.Equal(CalendarEventLinks.HeadingId, section.GetAttribute("aria-labelledby"));
        Assert.Equal(["Open Stage", "Open Hall"], section.QuerySelectorAll("ul.odc-photogrid2 > li .odc-phototile-open").Select(i => i.GetAttribute("aria-label")));
        Assert.Equal(["Open"], h.MenuOf("Stage"));
        Assert.Equal(["Open"], h.MenuOf("Hall"));
        Assert.Equal("agenda.pdf", section.QuerySelector(".jec-file-name")!.TextContent);
        Assert.Contains("Attachments belong to the whole series.", section.QuerySelector(".cal-ce-fromseries-help")!.TextContent);
        Assert.Empty(h.Buttons("Add photos"));
        Assert.Empty(h.Buttons("Attach documents"));

        await h.SetTitle("Moved rehearsal");
        h.Save();
        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
        Assert.Null(h.EventWrites[0].PhotoIds);
    }

    [Fact] // AC 4: the section's Edit series… is the dialog's own control and opens the series
    public async Task The_sections_edit_series_link_opens_the_series()
    {
        await using var h = new Harness();
        h.OpenEvent(Occurrence([File("agenda.pdf")]));

        var links = h.Buttons("Edit series…");
        Assert.Equal(2, links.Count); // the hint's and the section's: one label, one handler
        await h.Cut.InvokeAsync(() => links[1].Click());

        Assert.Equal([PatternId], h.EditSeries);
    }

    [Fact] // AC 4: without a read claim the section shows the count line only
    public async Task An_occurrence_without_read_claims_shows_only_the_count_lines()
    {
        await using var h = new Harness(readFiles: false, readPhotos: false);
        h.OpenEvent(Withheld(Occurrence([File("a.pdf")], [Photo("p", 0), Photo("q", 1)]), files: true, photos: true));

        var section = h.Cut.Find("section.cal-ce-fromseries");
        Assert.Equal(
            ["2 photos attached. You don't have permission to view photos.", "1 document attached. You don't have permission to view documents."],
            section.QuerySelectorAll(".alf-countline").Select(Text));
        Assert.Empty(section.QuerySelectorAll("ul, .jec-file"));
    }

    // ── AC 5: cap ────────────────────────────────────────────────────────────

    [Fact] // AC 5: at the cap Add is aria-disabled (still focusable), described by the help line naming the server's number
    public async Task At_the_cap_add_is_aria_disabled_and_its_help_line_names_the_number()
    {
        await using var h = new Harness(cap: 2);
        h.OpenEvent(Standalone(photos: [Photo("a", 0), Photo("b", 1)]));

        var add = h.Button("Add photos");
        Assert.Equal("true", add.GetAttribute("aria-disabled"));
        Assert.False(add.HasAttribute("disabled"));
        var help = h.Cut.Find("#" + add.GetAttribute("aria-describedby"));
        Assert.Equal("Up to 2 photos per event or series.", help.TextContent);

        add.Click();
        Assert.Empty(h.Cut.FindComponents<AttachPhotosDialog>());

        var attach = h.Button("Attach documents");
        Assert.Null(attach.GetAttribute("aria-disabled"));
        Assert.Equal("Up to 2 documents per event or series.", h.Cut.Find("#" + attach.GetAttribute("aria-describedby")).TextContent);
    }

    [Fact] // AC 5: changing the setting changes the number once the cache is invalidated (a /settings save)
    public async Task The_number_follows_the_setting_after_the_cache_invalidates()
    {
        var api = new Mock<IImportLimitsApiClient>();
        var served = 3;
        api.Setup(a => a.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Ok(ImportLimitsCache.Fallback with { CalendarEventMaxLinksPerKind = served }));
        var cache = new ImportLimitsCache(api.Object);

        Assert.Equal(3, (await cache.GetAsync()).CalendarEventMaxLinksPerKind);
        served = 7;
        Assert.Equal(3, (await cache.GetAsync()).CalendarEventMaxLinksPerKind);
        cache.Invalidate();
        var limits = await cache.GetAsync();

        await using var h = new Harness(limits: limits);
        h.Open(CalendarEventDialogMode.Create);
        Assert.Equal("Up to 7 photos per event or series.", h.Cut.Find("#" + h.Button("Add photos").GetAttribute("aria-describedby")).TextContent);
    }

    [Fact] // AC 5: a failed cap load shows no number and leaves Add enabled — the fallback carries no cap
    public async Task A_failed_cap_load_shows_no_number_and_leaves_add_enabled()
    {
        var api = new Mock<IImportLimitsApiClient>();
        api.Setup(a => a.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ImportLimitsDto>.Failure(HttpStatusCode.ServiceUnavailable, new ApiProblem { Detail = "Degraded." }));
        var limits = await new ImportLimitsCache(api.Object).GetAsync();
        Assert.Null(limits.CalendarEventMaxLinksPerKind);

        await using var h = new Harness(limits: limits);
        h.OpenEvent(Standalone(photos: [.. Enumerable.Range(0, 60).Select(i => Photo($"p{i}", i))]));

        var add = h.Button("Add photos");
        Assert.Null(add.GetAttribute("aria-disabled"));
        Assert.Null(add.GetAttribute("aria-describedby"));
        Assert.Empty(h.Cut.FindAll(".alf-caphelp"));
    }

    [Fact] // AC 5: an event over a lowered cap says so, refuses adds, and still saves a title edit (keeping the links)
    public async Task A_lowered_cap_allows_a_title_edit()
    {
        await using var h = new Harness(cap: 1);
        h.OpenEvent(Standalone([File("a.pdf"), File("b.pdf")]));

        var attach = h.Button("Attach documents");
        Assert.Equal("true", attach.GetAttribute("aria-disabled"));
        Assert.Equal("This event has more than the current limit of 1. You can remove items but not add more.",
            h.Cut.Find("#" + attach.GetAttribute("aria-describedby")).TextContent);

        await h.SetTitle("Renamed");
        h.Save();
        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
    }

    [Fact] // AC 5 (§3.1): a pick that would go over adds up to the cap and says so
    public async Task A_pick_over_the_cap_adds_up_to_it_and_announces_the_limit()
    {
        await using var h = new Harness(cap: 3);
        h.OpenEvent(Standalone(photos: [Photo("a", 0), Photo("b", 1)]));

        await h.PickPhotosAsync(3);

        Assert.Equal(3, h.Tiles.Count);
        h.Cut.WaitForAssertion(() => Assert.Equal("Added 1. Photo limit reached: 3.", h.Announced));
    }

    // ── AC 1 (review): clearing the last item sends [] for that kind, null for the other ──

    [Fact] // AC 1: removing the only file sends an empty file list — a clear — and keeps the photos
    public async Task Removing_the_last_file_sends_an_empty_list_and_keeps_the_photos()
    {
        await using var h = new Harness();
        var only = File("lease.pdf");
        h.OpenEvent(Standalone([only], [Photo("Beach", 0)]));

        var rows = h.Cut.FindComponent<JournalFileRows>();
        await h.Cut.InvokeAsync(() => rows.Instance.OnRemove!.Value.InvokeAsync(only.FileId));
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.NotNull(h.EventWrites[0].AttachmentFileIds);
        Assert.Empty(h.EventWrites[0].AttachmentFileIds!);
        Assert.Null(h.EventWrites[0].PhotoIds);
    }

    [Fact] // AC 1: removing the only photo sends an empty photo list — a clear — and keeps the files
    public async Task Removing_the_last_photo_sends_an_empty_list_and_keeps_the_files()
    {
        await using var h = new Harness();
        h.OpenEvent(Standalone([File("lease.pdf")], [Photo("Beach", 0)]));

        await h.RemovePhoto("Beach");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Single(h.EventWrites));
        Assert.NotNull(h.EventWrites[0].PhotoIds);
        Assert.Empty(h.EventWrites[0].PhotoIds!);
        Assert.Null(h.EventWrites[0].AttachmentFileIds);
    }

    // ── v3 §C: the slow-load announcement ────────────────────────────────────

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> OpenPending(Harness h, TaskCompletionSource<ExistingCalendarEvent?> pending)
    {
        h.Calendar.Setup(c => c.GetEventAsync(EventId, It.IsAny<CancellationToken>())).Returns(pending.Task);
        return h.Context.Render<CalendarEventDialog>(new Dictionary<string, object?>
        {
            [nameof(CalendarEventDialog.Open)] = true,
            [nameof(CalendarEventDialog.Mode)] = CalendarEventDialogMode.Edit,
            [nameof(CalendarEventDialog.EventId)] = EventId,
            [nameof(CalendarEventDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(CalendarEventDialog.CanUpdate)] = true,
            [nameof(CalendarEventDialog.CanReadFiles)] = true,
            [nameof(CalendarEventDialog.CanReadPhotos)] = true,
        });
    }

    [Fact] // v3 §C: a load longer than the threshold says the attachments have arrived
    public async Task A_slow_load_announces_that_the_attachments_loaded()
    {
        await using var h = new Harness();
        var pending = new TaskCompletionSource<ExistingCalendarEvent?>();
        var cut = OpenPending(h, pending);
        cut.WaitForElement(".alf-skel");

        h.Time.Advance(CalendarEventDialog.SlowLinksThreshold + TimeSpan.FromMilliseconds(200));
        pending.SetResult(Standalone([File("lease.pdf")]));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".alf-skel")));
        cut.WaitForAssertion(() => Assert.Equal(CalendarEventCopy.LinksLoaded, cut.FindAll("div.sr-only[aria-live=polite]").First().TextContent));
    }

    [Fact] // v3 §C: a load inside the threshold stays silent
    public async Task A_fast_load_stays_silent()
    {
        await using var h = new Harness();
        var pending = new TaskCompletionSource<ExistingCalendarEvent?>();
        var cut = OpenPending(h, pending);
        cut.WaitForElement(".alf-skel");

        h.Time.Advance(CalendarEventDialog.SlowLinksThreshold - TimeSpan.FromMilliseconds(200));
        pending.SetResult(Standalone([File("lease.pdf")]));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".alf-skel")));
        await Task.Delay(100);
        Assert.DoesNotContain(cut.FindAll("[aria-live]"), r => r.TextContent.Contains(CalendarEventCopy.LinksLoaded, StringComparison.Ordinal));
    }

    // ── AC 6: announcements and focus ────────────────────────────────────────

    [Fact] // AC 6: an add is announced once, in a real plural
    public async Task An_add_is_announced_once()
    {
        await using var h = new Harness();
        h.Open(CalendarEventDialogMode.Create);

        await h.PickPhotosAsync(2);

        h.Cut.WaitForAssertion(() => Assert.Equal("Added 2 photos.", h.Announced));
    }

    [Fact] // AC 6: a removal is announced and focus goes to the next tile's remove button, else the previous, else Add
    public async Task A_removal_is_announced_and_focus_follows_the_chain()
    {
        await using var h = new Harness();
        var a = Photo("First", 0);
        var b = Photo("Second", 1);
        var c = Photo("Third", 2);
        h.OpenEvent(Standalone(photos: [a, b, c]));

        await h.RemovePhoto("Second");

        h.Cut.WaitForAssertion(() => Assert.Equal("Removed Second.", h.Announced));
        h.Cut.WaitForAssertion(() => Assert.NotEmpty(h.FocusReturn.Invocations["focusFirstLater"]));
        var chain = h.FocusReturn.Invocations["focusFirstLater"].Last().Arguments.Select(x => x as string).ToList();
        Assert.EndsWith($"{c.PhotoId:N} .odc-phototile-open", chain[0]);
        Assert.NotNull(h.Cut.Find(chain[0]!));
        Assert.StartsWith("#jef-add-photos-", chain[^1]);
    }

    [Fact] // design system 1795477868-c9a2: removing the last tile lands on the previous tile's image, the only one on Add
    public async Task Removing_the_last_tile_lands_on_the_previous_and_the_only_one_on_add()
    {
        await using var h = new Harness();
        var a = Photo("First", 0);
        h.OpenEvent(Standalone(photos: [a, Photo("Second", 1)]));

        await h.RemovePhoto("Second");
        h.Cut.WaitForAssertion(() => Assert.NotEmpty(h.FocusReturn.Invocations["focusFirstLater"]));
        var chain = h.FocusReturn.Invocations["focusFirstLater"].Last().Arguments.Select(x => x as string).ToList();
        Assert.EndsWith($"{a.PhotoId:N} .odc-phototile-open", chain[0]);

        await h.RemovePhoto("First");
        h.Cut.WaitForAssertion(() =>
        {
            var last = h.FocusReturn.Invocations["focusFirstLater"].Last().Arguments.Select(x => x as string).ToList();
            Assert.Null(last[0]);
            Assert.StartsWith("#jef-add-photos-", last[1]);
        });
    }

    [Fact] // design system 1795477868-c9a2: an editable field's tile menu is Open · — · Remove from event
    public async Task An_editable_tile_offers_open_and_remove_from_event()
    {
        await using var h = new Harness();
        h.OpenEvent(Standalone(photos: [Photo("Beach", 0), Photo("Dunes", 1)]));

        Assert.Equal(["Open", "Remove from event"], h.MenuOf("Beach"));
        Assert.True(h.Tiles.Single(t => t.Name == "Beach").Actions![1].Divider);
        Assert.Contains("dense", h.Cut.Find(".alf-photos ul.odc-photogrid2").ClassList);
        Assert.Equal(96, h.Cut.FindComponents<Odyssey.Client.Components.OdsPhotoGrid<JournalDraftPhoto>>().Single().Instance.EffectiveMinTile);
    }

    [Fact] // AC 6: removing a file announces its name; focus falls back to Attach documents when none is left
    public async Task Removing_the_only_file_announces_it_and_falls_back_to_attach()
    {
        await using var h = new Harness();
        var only = File("lease.pdf");
        h.OpenEvent(Standalone([only]));

        var rows = h.Cut.FindComponent<JournalFileRows>();
        Assert.Equal("Remove from event", rows.Instance.RemoveLabel);
        await h.Cut.InvokeAsync(() => rows.Instance.OnRemove!.Value.InvokeAsync(only.FileId));

        h.Cut.WaitForAssertion(() => Assert.Equal("Removed lease.pdf.", h.Announced));
        h.Cut.WaitForAssertion(() =>
        {
            var chain = h.FocusReturn.Invocations["focusFirstLater"].Last().Arguments.Select(x => x as string).ToList();
            Assert.Null(chain[0]);
            Assert.StartsWith("#jef-attach-", chain[1]);
        });
    }

    // ── AC 11: accessibility ─────────────────────────────────────────────────

    [Fact] // AC 11: every control and tile is named, the pickers carry the event subtitles
    public async Task Every_control_and_tile_has_an_accessible_name()
    {
        await using var h = new Harness();
        h.OpenEvent(Standalone([File("lease.pdf")], [Photo("Beach", 0)]));

        Assert.Equal("Open Beach", h.Cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.Equal("Actions for photo: Beach", h.Tiles.Single().ActionsLabel);
        var remove = h.Tiles.Single().Actions!.Single(a => a.Label == "Remove from event");
        Assert.Equal("Remove Beach from event", remove.AriaLabel);
        Assert.True(remove.Danger);
        Assert.Equal("1 photo", h.Cut.Find(".alf-photos ul.odc-photogrid2").GetAttribute("aria-label"));
        Assert.Equal("Actions for file lease.pdf", h.Cut.Find(".je-cardmenu [aria-label]").GetAttribute("aria-label"));

        await h.PickPhotosAsync(0);
        Assert.Equal("Pick photos to attach to this event.", h.Cut.FindComponent<AttachPhotosDialog>().Instance.Subtitle);
    }

    [Fact] // AC 11 / §7: while the detail loads the fields are aria-busy and Add is aria-disabled, not disabled
    public async Task While_loading_the_fields_are_busy_and_add_stays_focusable()
    {
        await using var h = new Harness();
        var pending = new TaskCompletionSource<ExistingCalendarEvent?>();
        h.Calendar.Setup(c => c.GetEventAsync(EventId, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cut = h.Context.Render<CalendarEventDialog>(new Dictionary<string, object?>
        {
            [nameof(CalendarEventDialog.Open)] = true,
            [nameof(CalendarEventDialog.Mode)] = CalendarEventDialogMode.Edit,
            [nameof(CalendarEventDialog.EventId)] = EventId,
            [nameof(CalendarEventDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(CalendarEventDialog.CanUpdate)] = true,
            [nameof(CalendarEventDialog.CanReadFiles)] = true,
            [nameof(CalendarEventDialog.CanReadPhotos)] = true,
        });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".odc-field[aria-busy=true], [aria-busy=true]").Count(e => e.QuerySelector(".alf-skel") is not null)));
        var adds = cut.FindAll("button").Where(b => Text(b) is "Add photos" or "Attach documents").ToList();
        Assert.Equal(2, adds.Count);
        Assert.All(adds, b => Assert.Equal("true", b.GetAttribute("aria-disabled")));
        Assert.All(adds, b => Assert.False(b.HasAttribute("disabled")));

        pending.SetResult(Standalone());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".alf-skel")));
    }

    // ── AC 14: read-only viewer ──────────────────────────────────────────────

    [Fact] // AC 14 (v4 C1): a calendar.read-only principal sees the lists, with download, and no add or remove control
    public async Task A_viewer_who_cannot_save_sees_the_lists_read_only()
    {
        await using var h = new Harness(canUpdate: false);
        h.OpenEvent(Standalone([File("lease.pdf")], [Photo("Beach", 0)]));

        Assert.Empty(h.Cut.FindAll(".cal-ce-fromseries h3"));
        Assert.Equal(["Photos", "Attachments"], h.Cut.FindAll(".alf-sublabel").Select(s => s.TextContent));
        // Read-only: the tile's ⋯ menu offers Open only.
        Assert.Equal("Open Beach", h.Cut.Find(".odc-phototile-open").GetAttribute("aria-label"));
        Assert.Equal(["Open"], h.MenuOf("Beach"));
        Assert.Empty(h.Buttons("Add photos"));
        Assert.Empty(h.Buttons("Attach documents"));
        Assert.Null(h.Cut.FindComponent<JournalFileRows>().Instance.OnRemove);
        Assert.True(h.Cut.FindComponent<JournalFileRows>().Instance.CanDownload);
    }

    [Fact] // v4 C1 (security note 1): a read-only viewer opening an occurrence gets "From the series"
    public async Task A_read_only_viewer_opening_an_occurrence_gets_the_series_section()
    {
        await using var h = new Harness(canUpdate: false);
        h.OpenEvent(Occurrence([File("agenda.pdf")]));

        Assert.Equal("From the series", h.Cut.Find("section.cal-ce-fromseries h3").TextContent);
    }

    // ── §7: refused links ────────────────────────────────────────────────────

    [Fact] // §7: a 422 keyed on a link shows the server's text in the dialog's alert, and the fields stay as edited
    public async Task A_refused_link_shows_in_the_alert_and_keeps_the_edit()
    {
        await using var h = new Harness();
        h.OpenEvent(Standalone(photos: [Photo("Keep", 0), Photo("Drop", 1)]));
        h.Calendar.Setup(c => c.UpdateEventWithResultAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingCalendarEvent>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Status = 422,
                Detail = "Unknown photo(s).",
                Errors = new Dictionary<string, string[]> { ["photoIds"] = ["Unknown photo(s)."] },
            }));

        await h.RemovePhoto("Drop");
        h.Save();

        h.Cut.WaitForAssertion(() => Assert.Contains("Unknown photo(s).", h.Cut.Find("#" + CalendarEventDialog.AlertId).TextContent));
        Assert.Single(h.Tiles);
    }

    // ── v4 "Defensive mapping" ───────────────────────────────────────────────

    [Fact] // v4: a null file field under files.read renders "Unknown" and never throws
    public void A_null_file_field_maps_to_unknown()
    {
        var row = CalendarLinkRows.File(new CalendarLinkFileDto { FileId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });

        Assert.Equal(JournalFileRows.Unknown, row.FileName);
        Assert.Equal(JournalFileRows.UnknownSize, row.SizeBytes);
        Assert.Equal(default, row.UploadedAtUtc);
    }

    [Fact] // §3.1 "Keeping links": Keep is null for both kinds; Set names only the kinds it is given
    public void Attachment_links_keep_is_null_and_set_names_only_the_given_kinds()
    {
        var id = Guid.NewGuid();
        Assert.Null(AttachmentLinks.Keep.FileIds);
        Assert.Null(AttachmentLinks.Keep.PhotoIds);
        Assert.Same(AttachmentLinks.Keep, AttachmentLinks.Set(null, null));
        var set = AttachmentLinks.Set([id], null);
        Assert.Equal([id], set.FileIds!);
        Assert.Null(set.PhotoIds);
        Assert.Empty(AttachmentLinks.Set([], null).FileIds!);
    }
}
