using System.Net;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #383 §5.2, AC 1–3: every client write of an event, series or journal entry carries its place. The
/// server's PUT is a full replacement and an omitted <c>locationId</c> clears the link, so each existing path
/// keeps the stored id, a changed link is SET, and only <see cref="LocationLink.Clear"/> sends null.
/// </summary>
public class UnifiedLocationsWriteTests
{
    private static readonly Guid Stored = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static ExistingCalendarEvent Event(Guid? locationId) => new()
    {
        CalendarEventId = Guid.NewGuid(),
        CalendarId = Guid.NewGuid(),
        Title = "Dinner",
        Description = "Bring wine",
        Location = "Room 4B",
        LocationId = locationId,
        LocationRef = locationId is { } id ? new LocationRef { LocationId = id, Name = "Hytta" } : null,
        StartDateTime = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc),
        EndDateTime = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc),
    };

    private static ExistingJournalEntry Entry(Guid? locationId) => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "Hike",
        Content = "Summit.",
        EntryDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Location = "North ridge",
        LocationId = locationId,
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Attachments = [Attachment(), Attachment()],
    };

    private static JournalEntryAttachmentDto Attachment() => new()
    {
        JournalEntryAttachmentId = Guid.NewGuid(), FileId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
    };

    // ── LocationLink ────────────────────────────────────────────────────────────

    [Fact]
    public void Only_Clear_sends_a_null_link_for_a_record_that_had_a_place()
    {
        Assert.Equal(Stored, LocationLink.For(Stored, LocationPick.Existing(Stored), null, pickerShown: true).Id);
        Assert.Equal(LocationLinkKind.Keep, LocationLink.For(Stored, LocationPick.Existing(Stored), null, true).Kind);

        var cleared = LocationLink.For(Stored, null, null, pickerShown: true);
        Assert.Same(LocationLink.Clear, cleared);
        Assert.Null(cleared.Id);
    }

    [Fact]
    public void A_different_place_is_a_change_of_link()
    {
        var other = Guid.NewGuid();

        var link = LocationLink.For(Stored, LocationPick.Existing(other), null, pickerShown: true);

        Assert.Equal(LocationLinkKind.Set, link.Kind);
        Assert.Equal(other, link.Id);
    }

    [Fact]
    public void Without_the_picker_the_stored_id_is_kept_whatever_the_draft_says()
    {
        var link = LocationLink.For(Stored, null, null, pickerShown: false);

        Assert.Equal(LocationLinkKind.Keep, link.Kind);
        Assert.Equal(Stored, link.Id);
    }

    [Fact]
    public void A_staged_place_cannot_be_linked_before_it_is_created()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocationLink.For(null, LocationPick.Staged("Hytta"), null, pickerShown: true));
    }

    // ── Staged create (AC 3) ───────────────────────────────────────────────────

    [Fact]
    public async Task A_staged_place_is_created_once_and_reused_on_retry()
    {
        var created = Guid.NewGuid();
        var api = new Mock<ILocationsApiClient>();
        api.Setup(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingLocation>.Success(new ExistingLocation { LocationId = created, Name = "Hytta" }, HttpStatusCode.Created));
        var place = new PlaceDraft { Pick = LocationPick.Staged("Hytta") };

        Assert.True(await place.EnsureCreatedAsync(api.Object, pickerShown: true));
        // The record write failed and the user saves again: the place is reused, not posted twice.
        Assert.True(await place.EnsureCreatedAsync(api.Object, pickerShown: true));

        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n => n.Name == "Hytta" && n.Source == LocationSource.Manual),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(created, place.Link(pickerShown: true).Id);
        Assert.Equal(LocationLinkKind.Set, place.Link(pickerShown: true).Kind);
    }

    [Fact]
    public async Task A_failed_create_stops_the_save_with_the_servers_field_message()
    {
        var api = new Mock<ILocationsApiClient>();
        api.Setup(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingLocation>.Failure(HttpStatusCode.Conflict,
                Problem(409, "name", "A location with this name already exists.")));
        var place = new PlaceDraft { Pick = LocationPick.Staged("Hytta") };

        Assert.False(await place.EnsureCreatedAsync(api.Object, pickerShown: true));
        Assert.Equal("A location with this name already exists.", place.Error);
        Assert.Null(place.CreatedId);
    }

    [Fact]
    public async Task An_existing_pick_creates_nothing()
    {
        var api = new Mock<ILocationsApiClient>(MockBehavior.Strict);
        var place = PlaceDraft.For(Stored, null);

        Assert.True(await place.EnsureCreatedAsync(api.Object, pickerShown: true));
    }

    // ── Calendar: drag, resize, the event and series dialogs (AC 1) ─────────────

    [Fact]
    public void A_month_grid_drag_keeps_the_place_and_the_details()
    {
        var existing = Event(Stored);

        var update = CalendarPage.DropUpdate(existing, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 2));

        Assert.Equal(Stored, update.LocationId);
        Assert.Equal("Room 4B", update.Location);
        Assert.Equal(existing.StartDateTime.AddDays(3), update.StartDateTime);
    }

    [Fact]
    public void A_time_grid_resize_keeps_the_place()
    {
        var existing = Event(Stored);
        var start = existing.StartDateTime.AddHours(1);

        var update = CalendarPage.ResizeUpdate(existing, start, start.AddHours(3));

        Assert.Equal(Stored, update.LocationId);
        Assert.Equal(start.AddHours(3), update.EndDateTime);
    }

    [Fact]
    public void An_event_and_a_series_draft_seed_and_send_the_stored_place()
    {
        var draft = CalendarEventDraft.FromEvent(Event(Stored));
        Assert.Equal(Stored, draft.Place.StoredId);
        Assert.Equal(Stored, draft.ToNewCalendarEvent(draft.Place.Link(pickerShown: true)).LocationId);

        var pattern = new ExistingRecurrencePattern
        {
            RecurrencePatternId = Guid.NewGuid(), CalendarId = Guid.NewGuid(), Title = "Practice",
            StartDateTime = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc), EndDateTime = new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly, Interval = 1, DaysOfWeek = DaysOfWeekFlags.Monday, LocationId = Stored,
        };
        var series = CalendarEventDraft.FromPattern(pattern);
        Assert.Equal(Stored, series.ToNewRecurrencePattern(series.Place.Link(pickerShown: false)).LocationId);
    }

    // ── Journal: archive, attachment removal, the create/edit dialog (AC 1) ─────

    [Fact]
    public void Archiving_an_entry_keeps_its_place()
    {
        var update = JournalWrite.FromDetail(Entry(Stored), archived: true);

        Assert.Equal(Stored, update.LocationId);
        Assert.Equal("North ridge", update.Location);
        Assert.True(update.Archived);
    }

    [Fact]
    public void Removing_an_attachment_keeps_the_place()
    {
        var entry = Entry(Stored);

        var update = JournalWrite.WithoutAttachment(entry, entry.Attachments[0].FileId);

        Assert.Equal(Stored, update.LocationId);
        Assert.Single(update.AttachmentFileIds);
    }

    [Fact]
    public void An_entry_draft_seeds_the_stored_place_and_an_untouched_edit_resends_it()
    {
        var draft = JournalEntryDraft.From(Entry(Stored), [], []);

        var update = JournalWrite.ToUpdate(draft, archived: false, draft.Place.Link(pickerShown: true));

        Assert.Equal(Stored, update.LocationId);
    }

    // ── Calendar search (AC 14) ────────────────────────────────────────────────

    [Theory]
    [InlineData("hytta", true)]
    [InlineData("Room 4", true)]
    [InlineData("dinner", true)]
    [InlineData("cabin", false)]
    public void Calendar_search_matches_the_place_name_as_well_as_title_and_details(string term, bool matches)
    {
        Assert.Equal(matches, CalendarPage.MatchesSearch(Event(Stored), term));
    }

    [Fact]
    public void A_place_name_alone_is_found_once_the_text_location_was_converted()
    {
        var converted = Event(Stored) with { Location = null };

        Assert.True(CalendarPage.MatchesSearch(converted, "Hytta"));
        Assert.False(CalendarPage.MatchesSearch(Event(null) with { Location = null }, "Hytta"));
    }
}
