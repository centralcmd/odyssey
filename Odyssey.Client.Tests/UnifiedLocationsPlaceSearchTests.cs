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
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Place picker with Kartverket search (issue #383 v6 §3.2a, AC 2a): on events, series and journal entries the
/// picker lists saved places, Kartverket addresses and the create row; a Kartverket pick makes no request until
/// Save, which posts the location with its geocode and then writes the record with that id; a <c>409</c> keyed
/// <c>line1</c> links the existing place without posting again, and one keyed <c>name</c> retries with a suffix;
/// without <c>locations.create</c> there is no Kartverket group, no create row and no disclosure; the disclosure is
/// in the input's <c>aria-describedby</c>.
/// </summary>
public class UnifiedLocationsPlaceSearchTests
{
    private static readonly Guid CalendarId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid CreatedId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000c1");
    private static readonly Guid ExistingAddressId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000e1");

    // Hytta has no address here: a place mode picker offers every active place, not only address-complete ones.
    private static ExistingLocation PlainHytta() => Hytta() with { Line1 = null, PostalCode = null, City = null, CountryCode = null };

    // ── The picker (AC 2a: groups, create row, claims, disclosure) ─────────────

    private sealed record PickerHarness(
        BunitContext Ctx, IRenderedComponent<OdsLocationSelect> Cut, Mock<ILocationsApiClient> Api, List<LocationPick?> Picks)
    {
        public MudAutocomplete<OdsOption> Auto => Cut.FindComponent<MudAutocomplete<OdsOption>>().Instance;

        public OdsCombobox Combo => Cut.FindComponent<OdsCombobox>().Instance;

        public Task<IEnumerable<OdsOption>> Rows(string text) => Cut.InvokeAsync(async () => (await Auto.SearchFunc!(text, CancellationToken.None)!)!);
    }

    private static PickerHarness RenderPicker(bool allowCreate = true, LocationPick? value = null)
    {
        var ctx = Context();
        var api = Locations(PlainHytta(), Storgata());
        // The first candidate is the saved Storgata 1's own address, so it is not repeated; a place name is never listed.
        Answer(api, Candidate("Storgata 1", "0155", "OSLO", reference: "kv-same"), Candidate(),
            Candidate("Storgata", kind: LocationCandidateKind.PlaceName, reference: "kv-name"));
        ctx.Services.AddSingleton(api.Object);
        var picks = new List<LocationPick?>();
        var cut = ctx.Render<OdsLocationSelect>(p => p
            .Add(s => s.Id, "place")
            .Add(s => s.Value, value)
            .Add(s => s.AllowCreate, allowCreate)
            .Add(s => s.DebounceMs, 0)
            .Add(s => s.AddressDebounceMs, 0)
            .Add(s => s.ValueChanged, EventCallback.Factory.Create<LocationPick?>(new object(), v => picks.Add(v))));
        return new(ctx, cut, api, picks);
    }

    [Fact]
    public async Task The_place_picker_lists_saved_places_Kartverket_addresses_and_the_create_row()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(
            ["Saved places", "Hytta", "Storgata 1", "Addresses from Kartverket", "Storgata 3, 0155 OSLO", "Create place \"storgata\""],
            rows.Select(r => r.Label));
        Assert.True(OdsCombobox.IsHeadingRow(rows[0]) && OdsCombobox.IsHeadingRow(rows[3]));
        Assert.True(rows[5].IsCreate);
        Assert.Equal($"Create place \"storgata\" — {char.ToLowerInvariant(OdsLocationSelect.CreateDisclosure[0])}{OdsLocationSelect.CreateDisclosure[1..]}",
            rows[5].AriaLabel);
        Assert.Equal("Storgata 3, 0155 OSLO, address from Kartverket, new saved place added when you save", rows[4].AriaLabel);
        // The place picker keeps its own place rows — no "used by N" — and its own wording.
        Assert.Equal("Place", h.Cut.Find(".odc-field-label").TextContent.Trim());
        Assert.Equal("Clear place", h.Combo.ClearLabel);
        h.Api.Verify(a => a.SearchAsync(It.Is<LocationSearchRequest>(r => r.Query == "storgata" && r.Region == LocationSearchRegion.Norway),
            It.IsAny<CancellationToken>()), Times.Once);
        h.Api.Verify(a => a.ListAsync(1, OdsLocationSelect.PageSize, "storgata", It.Is<IReadOnlyCollection<string>>(s => s.Single() == "Active"),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Below_three_characters_only_saved_places_are_searched()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var rows = (await h.Rows("st")).ToList();

        Assert.Equal(["Saved places", "Hytta", "Storgata 1", "Create place \"st\""], rows.Select(r => r.Label));
        h.Api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_address_search_says_so_and_the_saved_places_and_create_row_still_work()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationSearchResponse>.Failure(HttpStatusCode.ServiceUnavailable, Problem(503)));

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(
            ["Saved places", "Hytta", "Storgata 1", "Addresses from Kartverket",
                "Address search is unavailable — pick a saved place or create one", "Create place \"storgata\""],
            rows.Select(r => r.Label));
        Assert.True(rows[4].IsNote);
        Assert.Equal("Address search is unavailable — pick a saved place or create one", OdsLocationSelect.PlaceAddressSearchUnavailable);
        Assert.DoesNotContain(rows, r => r.Label == OdsLocationSelect.AddressSearchUnavailable);
    }

    [Fact]
    public async Task Picking_a_Kartverket_address_stages_it_and_makes_no_request()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        var rows = (await h.Rows("storgata")).ToList();

        await h.Cut.InvokeAsync(() => h.Auto.ValueChanged.InvokeAsync(rows[4]));

        var pick = Assert.Single(h.Picks);
        Assert.True(pick!.IsStagedAddress);
        Assert.Equal("kv-1", pick.Candidate!.SourceReference);
        h.Api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);

        h.Cut.Render(p => p.Add(s => s.Value, pick));
        Assert.Equal("Storgata 3, 0155 OSLO (new)", h.Combo.Options.Single().Label);
    }

    [Fact]
    public async Task The_disclosure_is_visible_and_in_the_inputs_aria_describedby()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var disclosure = h.Cut.Find(".odc-locsel-disclose");
        Assert.Equal(OdsLocationSelect.AddressSearchDisclosure, disclosure.TextContent.Replace("info", "").Trim());
        Assert.Equal("place-disclose", disclosure.Id);
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsSetAttr" && Equals(i.Arguments[0], "place")
            && Equals(i.Arguments[1], "aria-describedby") && ((string)i.Arguments[2]!).Split(' ').Contains("place-disclose"));
    }

    [Fact]
    public async Task Without_locations_create_there_is_no_Kartverket_group_no_create_row_and_no_disclosure()
    {
        var h = RenderPicker(allowCreate: false);
        await using var _ = h.Ctx;

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(["Hytta", "Storgata 1"], rows.Select(r => r.Label));
        Assert.Null(h.Combo.Groups);
        Assert.Null(h.Combo.OnCreate);
        Assert.Null(h.Combo.FooterOption);
        Assert.Empty(h.Cut.FindAll(".odc-locsel-disclose"));
        Assert.DoesNotContain(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsSetAttr" && Equals(i.Arguments[1], "aria-describedby")
            && ((string?)i.Arguments[2] ?? "").Contains("disclose", StringComparison.Ordinal));
        h.Api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Save (AC 2a: no request until Save, POST with the geocode, 409 line1, 409 name) ──

    private sealed record DialogHarness(
        BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<ICalendarApiClient> Calendar, Mock<ILocationsApiClient> Locations,
        List<string> Calls, List<NewLocation> Posts);

    private static ApiResult<ExistingLocation> Created(Guid id) =>
        ApiResult<ExistingLocation>.Success(new ExistingLocation { LocationId = id, Name = "Storgata 3, 0155 OSLO, NO" }, HttpStatusCode.Created);

    private static ApiResult<ExistingLocation> ConflictOn(string field) =>
        ApiResult<ExistingLocation>.Failure(HttpStatusCode.Conflict, Problem(409, field, "Conflict."));

    private static DialogHarness RenderEventDialog(
        IEnumerable<ApiResult<ExistingLocation>> creates, ApiResult? writeResult = null, params ExistingLocation[] places)
    {
        var ctx = Context();
        var calls = new List<string>();
        var posts = new List<NewLocation>();
        var calendar = new Mock<ICalendarApiClient>();
        calendar.Setup(c => c.CreateEventAsync(It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("event"))
            .ReturnsAsync(writeResult ?? ApiResult.Success(HttpStatusCode.Created));
        var locations = Locations(places.Length == 0 ? [PlainHytta()] : places);
        var replies = new Queue<ApiResult<ExistingLocation>>(creates);
        locations.Setup(l => l.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .Callback((NewLocation body, CancellationToken _) => { calls.Add("place"); posts.Add(body); })
            .ReturnsAsync(() => replies.Dequeue());
        ctx.Services.AddSingleton(calendar.Object);
        ctx.Services.AddSingleton(locations.Object);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CalendarEventDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Mode, CalendarEventDialogMode.Create)
            .Add(x => x.Calendars, [new ExistingCalendar { CalendarId = CalendarId, Name = "Family", Color = "#4F8EF7" }])
            .Add(x => x.CanUpdate, true)
            .Add(x => x.CanReadLocations, true)
            .Add(x => x.CanCreateLocations, true)));
        return new(ctx, cut, calendar, locations, calls, posts);
    }

    private static async Task TitleAndPickAsync(IRenderedComponent<ProvidersHost> cut, LocationPick pick)
    {
        await cut.InvokeAsync(() => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Title").Instance.ValueChanged.InvokeAsync("Dinner"));
        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(pick));
    }

    private static void Click(IRenderedComponent<ProvidersHost> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task A_Kartverket_pick_makes_no_request_until_Save_which_posts_the_geocoded_place_then_the_event()
    {
        var h = RenderEventDialog([Created(CreatedId)]);
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Assert.Empty(h.Calls);
        h.Locations.Verify(l => l.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Cut.WaitForAssertion(() => Assert.Contains("Storgata 3, 0155 OSLO will be added when you save", h.Cut.Markup));

        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        var post = Assert.Single(h.Posts);
        Assert.Equal("Storgata 3, 0155 OSLO, NO", post.Name);
        Assert.Equal(("Storgata 3", "0155", "OSLO", "NO"), (post.Line1, post.PostalCode, post.City, post.CountryCode));
        Assert.Equal((59.91234m, 10.75012m, "Europe/Oslo"), (post.Latitude, post.Longitude, post.TimeZoneId));
        Assert.Equal((LocationSource.Kartverket, "kv-1"), (post.Source, post.SourceReference));
        h.Calendar.Verify(c => c.CreateEventAsync(It.Is<NewCalendarEvent>(e => e.LocationId == CreatedId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Kartverket_address_without_a_time_zone_is_posted_without_a_partial_geocode()
    {
        var h = RenderEventDialog([Created(CreatedId)]);
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate(zone: null)));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        var post = Assert.Single(h.Posts);
        Assert.Equal((null, null, null), (post.Latitude, post.Longitude, post.TimeZoneId));
        Assert.Equal(LocationSource.Kartverket, post.Source);
    }

    [Fact]
    public async Task A_409_on_line1_links_the_existing_place_by_its_address_key_without_posting_again()
    {
        // Two places come back for the search; only one has the candidate's address (any case, as the key folds it).
        var near = new ExistingLocation { LocationId = Guid.NewGuid(), Name = "Storgata 3B", Line1 = "Storgata 3B", PostalCode = "0155", City = "Oslo", CountryCode = "NO" };
        var same = new ExistingLocation { LocationId = ExistingAddressId, Name = "Mormor", Line1 = "STORGATA 3", PostalCode = "0155", City = "Oslo", CountryCode = "no" };
        var h = RenderEventDialog([ConflictOn("line1")], null, near, same);
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        h.Locations.Verify(l => l.ListAsync(It.IsAny<int>(), It.IsAny<int>(), "Storgata 3", It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Locations.Verify(l => l.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Locations.Verify(l => l.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateLocation>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Calendar.Verify(c => c.CreateEventAsync(It.Is<NewCalendarEvent>(e => e.LocationId == ExistingAddressId), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(PlaceDraft.AddressKeyOf(Candidate()),
            LocationAddressKey.Compute(same.CountryCode, same.PostalCode, same.City, same.Line1, null, null));
    }

    [Fact]
    public async Task A_409_on_name_retries_with_a_suffix_and_links_the_place_it_then_creates()
    {
        var h = RenderEventDialog([ConflictOn("name"), ConflictOn("name"), Created(CreatedId)]);
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "place", "place", "event"], h.Calls));
        Assert.Equal(["Storgata 3, 0155 OSLO, NO", "Storgata 3, 0155 OSLO, NO (2)", "Storgata 3, 0155 OSLO, NO (3)"], h.Posts.Select(p => p.Name));
        h.Calendar.Verify(c => c.CreateEventAsync(It.Is<NewCalendarEvent>(e => e.LocationId == CreatedId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Five_suffix_retries_that_all_conflict_stop_the_save_with_the_picker_error()
    {
        var h = RenderEventDialog(Enumerable.Repeat(ConflictOn("name"), 1 + PlaceDraft.NameSuffixRetries));
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(PlaceDraft.AddressAddFailed, h.Cut.FindComponent<OdsLocationSelect>().Instance.Error));
        Assert.Equal("Couldn’t add this address — pick a saved place or try again.", PlaceDraft.AddressAddFailed);
        Assert.Equal(6, h.Posts.Count);
        Assert.Equal("Storgata 3, 0155 OSLO, NO (6)", h.Posts[^1].Name);
        Assert.DoesNotContain("event", h.Calls);
    }

    [Fact]
    public async Task A_failed_event_write_reuses_the_created_place_on_retry()
    {
        var h = RenderEventDialog([Created(CreatedId)], ApiResult.Failure(HttpStatusCode.InternalServerError, Problem(500)));
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Click(h.Cut, "Create event");
        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event"], h.Calls));
        Click(h.Cut, "Create event");

        h.Cut.WaitForAssertion(() => Assert.Equal(["place", "event", "event"], h.Calls));
        h.Calendar.Verify(c => c.CreateEventAsync(It.Is<NewCalendarEvent>(e => e.LocationId == CreatedId), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Cancelling_after_a_Kartverket_pick_creates_nothing()
    {
        var h = RenderEventDialog([Created(CreatedId)]);
        await using var _ = h.Ctx;

        await TitleAndPickAsync(h.Cut, LocationPick.Address(Candidate()));
        Click(h.Cut, "Cancel");

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task The_event_dialogs_picker_offers_the_address_search_only_with_locations_create()
    {
        var withCreate = RenderEventDialog([]);
        await using (withCreate.Ctx)
        {
            Assert.Single(withCreate.Cut.FindAll(".odc-locsel-disclose"));
            Assert.NotNull(withCreate.Cut.FindComponent<OdsCombobox>().Instance.Groups);
        }

        await using var ctx = Context();
        ctx.Services.AddSingleton(Mock.Of<ICalendarApiClient>());
        ctx.Services.AddSingleton(Locations(PlainHytta()).Object);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CalendarEventDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Mode, CalendarEventDialogMode.Create)
            .Add(x => x.Calendars, [new ExistingCalendar { CalendarId = CalendarId, Name = "Family", Color = "#4F8EF7" }])
            .Add(x => x.CanUpdate, true)
            .Add(x => x.CanReadLocations, true)
            .Add(x => x.CanCreateLocations, false)));

        Assert.Empty(cut.FindAll(".odc-locsel-disclose"));
        var combo = cut.FindComponent<OdsCombobox>().Instance;
        Assert.Null(combo.Groups);
        Assert.Null(combo.OnCreate);
    }

    // ── The journal entry dialog ──────────────────────────────────────────────

    [Fact]
    public async Task The_entry_dialog_creates_a_staged_Kartverket_address_before_the_entry()
    {
        await using var ctx = Context(PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate);
        var journal = new Mock<IJournalApiClient>();
        var calls = new List<string>();
        journal.Setup(j => j.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateJournalEntry>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("entry")).ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        var locations = Locations(Hytta());
        locations.Setup(l => l.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("place"))
            .ReturnsAsync(Created(CreatedId));
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
            LocationId = HyttaId, LocationRef = new LocationRef { LocationId = HyttaId, Name = "Hytta" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<CreateJournalEntryDialog>(d => d.Add(x => x.Open, true).Add(x => x.Entry, entry)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".odc-locsel-disclose")));
        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Address(Candidate())));
        Assert.Empty(calls);
        Click(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["place", "entry"], calls));
        locations.Verify(l => l.CreateAsync(It.Is<NewLocation>(n => n.Source == LocationSource.Kartverket && n.SourceReference == "kv-1"
            && n.Latitude == 59.91234m && n.Line1 == "Storgata 3"), It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(j => j.UpdateAsync(entry.JournalEntryId, It.Is<UpdateJournalEntry>(u => u.LocationId == CreatedId), It.IsAny<CancellationToken>()));
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_long_address_name_is_cut_so_its_suffix_still_fits_the_column()
    {
        var name = PlaceDraft.SuffixedName(new string('x', 120), 3);

        Assert.Equal(LocationRules.NameMaxLength, name.Length);
        Assert.EndsWith(" (3)", name, StringComparison.Ordinal);
        Assert.Equal("Storgata 3, 0155 OSLO, NO", PlaceDraft.AddressPlaceName(Candidate()));
    }
}
