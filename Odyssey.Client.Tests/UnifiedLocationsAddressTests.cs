using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Address field of a contact or property (issue #383 v5 §3.3a, AC 3a): the picker lists saved places and
/// Kartverket addresses in labelled groups; a Kartverket pick makes no request until Save, which sends the fields
/// plus the geocode; hand entry sends fields only; a fields write on an existing address relinks only this record
/// (<c>applyTo: ThisRecord</c> with <c>expectedOtherUses</c>) and the UI never sends <c>AllUses</c>; Edit address
/// opens the location dialog for the linked place; picking another place sends its id; the claims decide what is
/// offered; the disclosure is always visible.
/// </summary>
public class UnifiedLocationsAddressTests
{
    private static readonly LocationAttribution Kartverket = new() { Text = "© Kartverket", License = "CC BY 4.0", Url = "https://www.kartverket.no" };

    private static AddressFields Fields(string line1 = "Storgata 1", string? line2 = null, string? region = null) => new()
    {
        Line1 = line1, Line2 = line2, PostalCode = "0155", City = "Oslo", Region = region, CountryCode = "NO",
    };

    private static AddressBlockStored Stored(int otherUses = 2, AddressFields? fields = null) => new()
    {
        LocationId = StorgataId,
        Fields = fields ?? Fields(),
        OtherUses = otherUses,
    };

    private static AddressBlockModel Model(
        AddressBlockStored? stored = null, AddressBlockVariant variant = AddressBlockVariant.Contact, AddressBlockPermissions? permissions = null,
        AddressFields? initial = null) =>
        new(variant, stored, permissions ?? AddressBlockPermissions.Full, initial);

    private static LocationCandidate Candidate(
        string line1 = "Storgata 3", string postalCode = "0155", string city = "OSLO",
        LocationCandidateKind kind = LocationCandidateKind.Address, string reference = "kv-1", string? zone = "Europe/Oslo") => new()
    {
        Name = $"{line1}, {postalCode} {city}",
        Kind = kind,
        Line1 = kind == LocationCandidateKind.Address ? line1 : null,
        PostalCode = kind == LocationCandidateKind.Address ? postalCode : null,
        City = kind == LocationCandidateKind.Address ? city : null,
        CountryCode = kind == LocationCandidateKind.Address ? "NO" : null,
        Latitude = 59.91234m,
        Longitude = 10.75012m,
        TimeZoneId = zone,
        Source = LocationSource.Kartverket,
        SourceReference = reference,
    };

    private static void Answer(Mock<ILocationsApiClient> api, params LocationCandidate[] candidates) =>
        api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationSearchResponse>.Success(
                new LocationSearchResponse { Region = LocationSearchRegion.Norway, Candidates = candidates, Attribution = Kartverket }, HttpStatusCode.OK));

    // ── What each state sends (AC 3a) ─────────────────────────────────────────

    [Fact]
    public void Picking_a_saved_place_sends_its_id_and_nothing_else()
    {
        var model = Model();
        model.PickPlace(HyttaId, Hytta());

        var write = model.ToWrite();

        Assert.Equal(new AddressWrite(AddressWriteKind.LinkById, HyttaId, null, null, null), write);
    }

    [Fact]
    public void A_staged_Kartverket_address_sends_its_fields_and_its_geocode()
    {
        var model = Model();
        var said = model.Stage(Candidate());

        var write = model.ToWrite();

        Assert.Equal("Storgata 3, 0155 OSLO will be added when you save", said);
        Assert.Equal(AddressWriteKind.Typed, write.Kind);
        Assert.Null(write.LocationId);
        Assert.Null(write.ApplyTo);
        Assert.Equal(("Storgata 3", "0155", "OSLO", "NO"), (write.Fields!.Line1, write.Fields.PostalCode, write.Fields.City, write.Fields.CountryCode));
        Assert.Equal(new AddressGeocode(59.91234m, 10.75012m, "Europe/Oslo", "kv-1"), write.Geocode);
        Assert.Equal(LocationSource.Kartverket, write.Geocode!.Source);
    }

    [Fact]
    public void A_Kartverket_address_without_a_time_zone_sends_no_partial_geocode()
    {
        var model = Model();
        model.Stage(Candidate(zone: null));

        Assert.Null(model.ToWrite().Geocode);
    }

    [Fact]
    public void Hand_entry_sends_the_fields_only()
    {
        var model = Model();
        model.EnterByHand("Rue de Rivoli 1");
        model.Edit(AddressBlockModel.CityKey, " Paris ");
        model.Edit(AddressBlockModel.CountryCodeKey, "fr");

        var write = model.ToWrite();

        Assert.Equal(AddressWriteKind.Typed, write.Kind);
        Assert.Equal(("Rue de Rivoli 1", "Paris", "FR"), (write.Fields!.Line1, write.Fields.City, write.Fields.CountryCode));
        Assert.Null(write.Geocode);
        Assert.Null(write.LocationId);
    }

    [Fact]
    public void On_an_existing_address_a_fields_write_relinks_only_this_record_with_the_count_shown()
    {
        var staged = Model(Stored(otherUses: 3));
        staged.Stage(Candidate());
        var byHand = Model(Stored(otherUses: 3));
        byHand.EnterByHand("Storgata 2");
        byHand.Edit(AddressBlockModel.CityKey, "Oslo");
        byHand.Edit(AddressBlockModel.CountryCodeKey, "NO");

        foreach (var write in new[] { staged.ToWrite(), byHand.ToWrite() })
        {
            Assert.Equal(AddressWriteKind.ThisRecord, write.Kind);
            Assert.Equal(AddressApplyTo.ThisRecord, write.ApplyTo);
            Assert.Equal(3, write.ExpectedOtherUses);
            Assert.Null(write.LocationId);
        }
    }

    [Fact]
    public void An_unchanged_existing_address_resends_the_stored_id_and_another_place_sends_its_own()
    {
        var model = Model(Stored());
        Assert.Equal(new AddressWrite(AddressWriteKind.None, StorgataId, null, null, null), model.ToWrite());

        model.PickPlace(HyttaId, Hytta());
        Assert.Equal(new AddressWrite(AddressWriteKind.LinkById, HyttaId, null, null, null), model.ToWrite());

        model.PickPlace(StorgataId);
        Assert.Equal(new AddressWrite(AddressWriteKind.None, StorgataId, null, null, null), model.ToWrite());
    }

    [Fact]
    public void Hand_entry_that_matches_the_stored_address_resends_the_stored_id()
    {
        var model = Model(Stored(), permissions: new AddressBlockPermissions(false, false, false));

        Assert.True(model.Hand);
        model.Edit(AddressBlockModel.Line1Key, " Storgata  1 ");

        Assert.Equal(new AddressWrite(AddressWriteKind.None, StorgataId, null, null, null), model.ToWrite());
    }

    /// <summary>The UI never sends AllUses (§3.3a): a change for everyone goes through Edit address.</summary>
    [Fact]
    public void No_state_ever_sends_all_uses()
    {
        var writes = new List<AddressWrite>();
        foreach (var variant in new[] { AddressBlockVariant.Contact, AddressBlockVariant.Property })
        {
            var model = Model(Stored(otherUses: 0), variant);
            writes.Add(model.ToWrite());
            model.Stage(Candidate());
            writes.Add(model.ToWrite());
            model.EnterByHand("STORGATA 1");
            model.Edit(AddressBlockModel.CityKey, "OSLO");
            model.Edit(AddressBlockModel.CountryCodeKey, "no");
            writes.Add(model.ToWrite());
            model.SearchInstead();
            model.ClearPick();
            writes.Add(model.ToWrite());
        }

        Assert.DoesNotContain(writes, w => w.ApplyTo == AddressApplyTo.AllUses);
        Assert.False(Enum.IsDefined(typeof(AddressWriteKind), "AllUses"));
    }

    [Fact]
    public void Clearing_a_property_unlinks_it_and_says_the_place_stays()
    {
        var model = Model(Stored(), AddressBlockVariant.Property);

        model.ClearPick();

        Assert.True(model.Cleared);
        Assert.Equal("The address stays as a saved place on the Locations page.", model.PickerHelp);
        Assert.Same(AddressWrite.None, model.ToWrite());
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void A_property_never_sends_line_two_or_region()
    {
        var model = Model(variant: AddressBlockVariant.Property);
        model.EnterByHand("Storgata 1");
        model.Edit(AddressBlockModel.Line2Key, "Etg. 4");
        model.Edit(AddressBlockModel.RegionKey, "Oslo");

        var write = model.ToWrite();

        Assert.Null(write.Fields!.Line2);
        Assert.Null(write.Fields.Region);
    }

    [Fact]
    public void A_contact_needs_an_address_and_hand_entry_needs_line_one_city_and_a_two_letter_country()
    {
        var empty = Model();
        Assert.Equal(new Dictionary<string, string> { [AddressBlockModel.AddressKey] = "Choose an address, or enter one by hand." }, empty.Validate());

        var hand = Model();
        hand.EnterByHand(null);
        Assert.Equal(["city", "countryCode", "line1"], hand.Validate().Keys.Order().ToArray());
        hand.Edit(AddressBlockModel.Line1Key, "Storgata 1");
        hand.Edit(AddressBlockModel.CityKey, "Oslo");
        hand.Edit(AddressBlockModel.CountryCodeKey, "NOR");
        Assert.Equal("Two letters, e.g. NO.", hand.Validate()[AddressBlockModel.CountryCodeKey]);

        // A property's address is optional: an unlinked one needs nothing.
        Assert.Empty(Model(variant: AddressBlockVariant.Property).Validate());
    }

    [Fact]
    public void Entering_by_hand_prefills_line_one_and_search_instead_keeps_what_was_typed()
    {
        var model = Model();
        model.Stage(Candidate());

        model.EnterByHand("  rue de rivoli 1 ");
        Assert.True(model.Hand);
        Assert.Null(model.Staged);
        Assert.Equal("rue de rivoli 1", model.Fields.Line1);

        model.Edit(AddressBlockModel.CityKey, "Paris");
        model.SearchInstead();
        Assert.False(model.Hand);
        model.EnterByHand("");
        Assert.Equal(("rue de rivoli 1", "Paris"), (model.Fields.Line1, model.Fields.City));
    }

    [Fact]
    public void A_legacy_property_address_without_a_place_and_a_caller_without_locations_read_open_in_hand_entry()
    {
        var legacy = Model(variant: AddressBlockVariant.Property, initial: new AddressFields { Line1 = "Storgata 1", City = "Oslo", CountryCode = "NO" });
        Assert.True(legacy.Hand);
        Assert.Equal(AddressWriteKind.Typed, legacy.ToWrite().Kind);

        var noRead = Model(Stored(), permissions: new AddressBlockPermissions(false, true, true, true));
        Assert.True(noRead.Hand);
        Assert.False(noRead.CanSearchInstead);
        Assert.False(noRead.CanEditAddress);

        Assert.False(Model(Stored()).Hand);
    }

    [Fact]
    public void The_usage_line_follows_the_linked_place_and_the_visible_kind_rule()
    {
        Assert.Equal("Shared with 2 other records", Model(Stored(otherUses: 2)).UsageLine);
        Assert.Equal("Only this contact uses this address", Model(Stored(otherUses: 0)).UsageLine);
        Assert.Equal("Only this property uses this address", Model(Stored(otherUses: 0), AddressBlockVariant.Property).UsageLine);
        Assert.Equal("No other records you can see use this address",
            Model(Stored(otherUses: 0), permissions: new AddressBlockPermissions(true, true, false, true)).UsageLine);

        var picked = Model(Stored());
        picked.PickPlace(HyttaId, Hytta());
        Assert.StartsWith("Shared with 8 other records — 2 contacts", picked.UsageLine, StringComparison.Ordinal);

        var staged = Model(Stored());
        staged.Stage(Candidate());
        Assert.Null(staged.UsageLine);
        Assert.False(staged.ShowsLinked);
    }

    [Fact]
    public void Edit_address_needs_locations_update_and_counts_every_use_this_record_included()
    {
        Assert.True(Model(Stored(otherUses: 2)).CanEditAddress);
        Assert.Equal(3, Model(Stored(otherUses: 2)).SharedUses);
        Assert.False(Model(Stored(), permissions: new AddressBlockPermissions(true, false, true, true)).CanEditAddress);
        Assert.Null(Model(Stored(), permissions: new AddressBlockPermissions(true, true, false, true)).SharedUses);
        Assert.Equal("Changes apply to every record that uses this address (3)", LocationVisuals.SharedUsesLine(3));
        Assert.Equal("Changes apply to every record that uses this address.", LocationVisuals.SharedUsesLine(null));
    }

    [Theory]
    [InlineData(AddressWriteKind.LinkById, "You don’t have permission to choose a saved place.")]
    [InlineData(AddressWriteKind.Typed, "Adding a new address needs permission to create locations.")]
    [InlineData(AddressWriteKind.ThisRecord, "Moving this contact to a new address needs permission to create locations.")]
    public void A_403_is_explained_by_the_request_that_was_sent(AddressWriteKind kind, string message) =>
        Assert.Equal(message, AddressBlockModel.ForbiddenMessage(kind, "contact"));

    /// <summary>
    /// N3: the picker's 422 message comes from the picked place's completeness, never from the server's prose.
    /// </summary>
    [Fact]
    public void The_locationId_refusal_message_follows_the_picked_places_address_not_the_servers_wording()
    {
        var complete = Model();
        complete.PickPlace(HyttaId, Hytta());
        var incomplete = Model();
        incomplete.PickPlace(HyttaId, Hytta() with { Line1 = null });

        Assert.Equal(AddressBlockModel.PlaceUnusable, complete.LocationIdMessage);
        Assert.Equal(AddressBlockModel.PlaceNotAddressComplete, incomplete.LocationIdMessage);
    }

    // ── The picker in address mode ────────────────────────────────────────────

    private sealed record PickerHarness(
        BunitContext Ctx, IRenderedComponent<OdsLocationSelect> Cut, Mock<ILocationsApiClient> Api, List<LocationPick?> Picks, List<string> ByHand)
    {
        public MudAutocomplete<OdsOption> Auto => Cut.FindComponent<MudAutocomplete<OdsOption>>().Instance;

        public OdsCombobox Combo => Cut.FindComponent<OdsCombobox>().Instance;

        public Task<IEnumerable<OdsOption>> Rows(string text) => Cut.InvokeAsync(async () => (await Auto.SearchFunc!(text, CancellationToken.None)!)!);
    }

    private static PickerHarness RenderPicker(bool allowCreate = true, LocationPick? value = null, params ExistingLocation[] places)
    {
        var ctx = Context();
        var api = Locations(places.Length == 0 ? [Storgata()] : places);
        Answer(api, Candidate("Storgata 1", "0155", "OSLO", reference: "kv-same"), Candidate(),
            Candidate("Storgata", kind: LocationCandidateKind.PlaceName, reference: "kv-name"));
        ctx.Services.AddSingleton(api.Object);
        var picks = new List<LocationPick?>();
        var byHand = new List<string>();
        var cut = ctx.Render<OdsLocationSelect>(p => p
            .Add(s => s.Mode, LocationSelectMode.Address)
            .Add(s => s.Id, "addr")
            .Add(s => s.Value, value)
            .Add(s => s.AllowCreate, allowCreate)
            .Add(s => s.DebounceMs, 0)
            .Add(s => s.AddressDebounceMs, 0)
            .Add(s => s.ValueChanged, EventCallback.Factory.Create<LocationPick?>(new object(), v => picks.Add(v)))
            .Add(s => s.OnEnterByHand, EventCallback.Factory.Create<string>(new object(), t => byHand.Add(t))));
        return new(ctx, cut, api, picks, byHand);
    }

    [Fact]
    public async Task The_picker_lists_saved_places_and_Kartverket_addresses_in_labelled_groups_then_the_hand_entry_row()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(
            ["Saved places", "Storgata 1", "Addresses from Kartverket", "Storgata 3, 0155 OSLO", "Enter an address by hand…"],
            rows.Select(r => r.Label));
        Assert.True(OdsCombobox.IsHeadingRow(rows[0]) && OdsCombobox.IsHeadingRow(rows[2]));
        Assert.True(h.Auto.ItemDisabledFunc!(rows[0]) && h.Auto.ItemDisabledFunc(rows[2]));
        Assert.False(h.Auto.ItemDisabledFunc(rows[1]) || h.Auto.ItemDisabledFunc(rows[3]) || h.Auto.ItemDisabledFunc(rows[4]));
        Assert.True(OdsCombobox.IsFooterRow(rows[4]));
        Assert.Null(h.Auto.MaxItems);

        // The saved place reads "used by N"; the Kartverket row says where it is from and that it is new.
        Assert.Contains("used by 2", rows[1].Sub);
        Assert.Equal("Storgata 3, 0155 OSLO, address from Kartverket, new saved place added when you save", rows[3].AriaLabel);
        Assert.Equal("2 addresses found", h.Cut.Find("[role=status]").TextContent);

        // The search went to Kartverket, region Norway; a place name and the saved place's own address were dropped.
        h.Api.Verify(a => a.SearchAsync(It.Is<LocationSearchRequest>(r => r.Query == "storgata" && r.Region == LocationSearchRegion.Norway),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Below_three_characters_only_saved_places_are_searched_and_the_hand_entry_row_stays()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var rows = (await h.Rows("st")).ToList();

        h.Api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(["Saved places", "Storgata 1", "Enter an address by hand…"], rows.Select(r => r.Label));

        var shortRows = (await h.Rows("s")).ToList();
        Assert.Equal(["Enter an address by hand…"], shortRows.Select(r => r.Label));
        Assert.Equal("Type at least 2 characters", h.Cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public async Task A_failed_address_search_says_so_in_its_group_and_saved_places_still_work()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationSearchResponse>.Failure(HttpStatusCode.ServiceUnavailable, Problem(503)));

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(
            ["Saved places", "Storgata 1", "Addresses from Kartverket", "Address search is unavailable — enter the address by hand", "Enter an address by hand…"],
            rows.Select(r => r.Label));
        Assert.True(rows[3].IsNote);
        Assert.True(h.Auto.ItemDisabledFunc!(rows[3]));
        Assert.Equal("1 address found. Address search is unavailable — enter the address by hand", h.Cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public async Task A_superseded_address_search_is_silent()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationSearchResponse>.Failure(new TaskCanceledException()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Combo.SearchAsync!("storgata", cancelled.Token));
    }

    [Fact]
    public async Task Picking_a_Kartverket_address_stages_it_and_creates_nothing()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        var rows = (await h.Rows("storgata")).ToList();

        await h.Cut.InvokeAsync(() => h.Auto.ValueChanged.InvokeAsync(rows[3]));

        var pick = Assert.Single(h.Picks);
        Assert.True(pick!.IsStagedAddress);
        Assert.Equal("kv-1", pick.Candidate!.SourceReference);
        h.Api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);

        h.Cut.Render(p => p.Add(s => s.Value, pick));
        Assert.Equal("Storgata 3, 0155 OSLO (new)", h.Combo.Options.Single().Label);
    }

    [Fact]
    public async Task The_hand_entry_row_hands_over_the_typed_text_and_leaves_the_value()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;
        var footer = (await h.Rows("rue de rivoli 1")).Last();

        await h.Cut.InvokeAsync(() => h.Auto.ValueChanged.InvokeAsync(footer));

        Assert.Equal(["rue de rivoli 1"], h.ByHand);
        Assert.Empty(h.Picks);
    }

    [Fact]
    public async Task The_disclosure_is_always_visible_and_describes_the_input()
    {
        var h = RenderPicker();
        await using var _ = h.Ctx;

        var disclosure = h.Cut.Find(".odc-locsel-disclose");
        Assert.Equal("Address search sends what you type to Kartverket.", disclosure.TextContent.Replace("info", "").Trim());
        Assert.Equal("addr-disclose", disclosure.Id);
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsSetAttr" && Equals(i.Arguments[0], "addr")
            && Equals(i.Arguments[1], "aria-describedby") && ((string)i.Arguments[2]!).Split(' ').Contains("addr-disclose"));
        Assert.Equal("Address", h.Cut.Find("label.odc-field-label, .odc-field-label").TextContent.Trim().TrimEnd('*'));
    }

    [Fact]
    public async Task Without_locations_create_there_is_no_Kartverket_group_no_hand_entry_row_and_no_disclosure()
    {
        var h = RenderPicker(allowCreate: false);
        await using var _ = h.Ctx;

        var rows = (await h.Rows("storgata")).ToList();

        Assert.Equal(["Saved places", "Storgata 1"], rows.Select(r => r.Label));
        Assert.Null(h.Combo.FooterOption);
        Assert.Null(h.Combo.OnCreate);
        Assert.Empty(h.Cut.FindAll(".odc-locsel-disclose"));
        h.Api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_address_search_waits_its_own_400_ms_and_a_superseded_one_is_never_sent()
    {
        var ctx = Context();
        await using var _ = ctx;
        var api = Locations(Storgata());
        Answer(api, Candidate());
        ctx.Services.AddSingleton(api.Object);
        var cut = ctx.Render<OdsLocationSelect>(p => p
            .Add(s => s.Mode, LocationSelectMode.Address)
            .Add(s => s.AllowCreate, true)
            .Add(s => s.DebounceMs, 0)
            .Add(s => s.AddressDebounceMs, 400));
        var search = cut.FindComponent<OdsCombobox>().Instance.SearchAsync!;
        using var superseded = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search("storgata", superseded.Token));
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var rows = await search("storgata", CancellationToken.None);
        Assert.Contains(rows, r => r.Group == OdsLocationSelect.KartverketGroup);
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The Combobox's groups and footer row (design system · Combobox <c>groups</c>, <c>footerOption</c>).</summary>
    [Fact]
    public async Task The_combobox_lists_ungrouped_rows_first_then_each_group_and_always_ends_on_the_footer_row()
    {
        await using var ctx = Context();
        var footer = new List<string>();
        var values = new List<string?>();
        var cut = ctx.Render<OdsCombobox>(p => p
            .Add(c => c.Value, "a")
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<string?>(new object(), v => values.Add(v)))
            .Add(c => c.Options, [
                new OdsOption("b", "Beta") { Group = "two" }, new OdsOption("a", "Alpha") { Group = "one" },
                new OdsOption("u", "Unsorted"), new OdsOption("empty", "Nothing here") { Group = "three" }])
            .Add(c => c.Groups, [new OdsOptionGroup("one", "First"), new OdsOptionGroup("two", "Second"), new OdsOptionGroup("four", "Empty")])
            .Add(c => c.FooterOption, new OdsComboboxFooter("Enter by hand…", t => { footer.Add(t); return Task.CompletedTask; })));
        var auto = cut.FindComponent<MudAutocomplete<OdsOption>>().Instance;

        var all = (await cut.InvokeAsync(async () => (await auto.SearchFunc!(null!, CancellationToken.None)!)!)).ToList();
        Assert.Equal(["Unsorted", "Nothing here", "First", "Alpha", "Second", "Beta", "Enter by hand…"], all.Select(o => o.Label));

        var none = (await cut.InvokeAsync(async () => (await auto.SearchFunc!("zzz", CancellationToken.None)!)!)).ToList();
        Assert.Equal(["Enter by hand…"], none.Select(o => o.Label));

        await cut.InvokeAsync(() => auto.ValueChanged.InvokeAsync(none[0]));
        Assert.Equal(["zzz"], footer);
        Assert.Empty(values);
        Assert.Equal("a", cut.Instance.Value);
    }

    // ── Through the contact dialog ────────────────────────────────────────────

    private static ExistingAddress Address(int otherUses = 2) => new()
    {
        Id = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        Label = AddressLabel.Home,
        IsPrimary = true,
        LocationId = StorgataId,
        Line1 = "Storgata 1",
        PostalCode = "0155",
        City = "Oslo",
        CountryCode = "NO",
        OtherUses = otherUses,
    };

    private sealed record ContactHarness(
        BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Mock<IContactsApiClient> Contacts, Mock<ILocationsApiClient> Api, List<NewAddress> Sent)
    {
        public Task Pick(LocationPick? pick) =>
            Cut.InvokeAsync(() => Cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(pick));

        public Task EnterByHand(string text) =>
            Cut.InvokeAsync(() => Cut.FindComponent<OdsLocationSelect>().Instance.OnEnterByHand.InvokeAsync(text));
    }

    private static ContactHarness RenderContactDialog(
        ExistingAddress? stored, Func<NewAddress, ApiResult>? reply = null, AddressBlockPermissions? permissions = null)
    {
        var ctx = Context();
        var sent = new List<NewAddress>();
        var contacts = new Mock<IContactsApiClient>();
        contacts.Setup(c => c.UpdateAddressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NewAddress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, NewAddress body, CancellationToken _) => { sent.Add(body); return reply?.Invoke(body) ?? ApiResult.Success(HttpStatusCode.OK); });
        contacts.Setup(c => c.AddAddressAsync(It.IsAny<Guid>(), It.IsAny<NewAddress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NewAddress body, CancellationToken _) => { sent.Add(body); return reply?.Invoke(body) ?? ApiResult.Success(HttpStatusCode.Created); });
        contacts.Setup(c => c.ListAddressesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingAddress>>.Success([], HttpStatusCode.OK));
        ctx.Services.AddSingleton(contacts.Object);
        var api = Locations(Hytta(), Storgata(new LocationUsage { Contacts = 3, Properties = 0, CalendarEvents = 0, RecurringSeries = 0, JournalEntries = 0 }));
        Answer(api, Candidate());
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        var draft = stored is null
            ? new ContactMethodDraft { Label = nameof(AddressLabel.Home), IsPrimary = true }
            : ContactMethodDraft.FromAddress(stored);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<ContactMethodDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Kind, "address")
            .Add(x => x.Draft, draft)
            .Add(x => x.IsEdit, stored is not null)
            .Add(x => x.ContactType, ContactType.Person)
            .Add(x => x.ContactId, Guid.NewGuid())
            .Add(x => x.PickerDebounceMs, 0)
            .Add(x => x.AddressDebounceMs, 0)
            .Add(x => x.Permissions, permissions ?? AddressBlockPermissions.Full)));
        return new(ctx, cut, contacts, api, sent);
    }

    private static Task EditField(IRenderedComponent<ProvidersHost> cut, string label, string value) =>
        cut.InvokeAsync(() => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    private static void Submit(IRenderedComponent<ProvidersHost> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task The_dialog_shows_one_Address_field_and_no_line_fields_or_apply_to_group()
    {
        var h = RenderContactDialog(Address());
        await using var _ = h.Ctx;

        var picker = h.Cut.FindComponent<OdsLocationSelect>().Instance;
        Assert.Equal(LocationSelectMode.Address, picker.Mode);
        Assert.True(picker.Required);
        Assert.DoesNotContain(h.Cut.FindComponents<OdsField>(), f => f.Instance.Label is "Line 1" or "City");
        Assert.Empty(h.Cut.FindAll("[role=radiogroup], fieldset.odc-radiofield, .odc-addrblock-warn"));
        Assert.DoesNotContain("Also on the saved place", h.Cut.Markup);
        Assert.Contains("Shared with 2 other records", h.Cut.Find(".odc-addrblock-usage").TextContent);
        Assert.Contains("Address search sends what you type to Kartverket.", h.Cut.Find(".odc-locsel-disclose").TextContent);
    }

    [Fact]
    public async Task A_Kartverket_address_makes_no_request_until_Save_which_sends_the_fields_and_the_geocode()
    {
        var h = RenderContactDialog(null);
        await using var _ = h.Ctx;

        await h.Pick(LocationPick.Address(Candidate()));
        h.Api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(h.Sent);
        h.Cut.WaitForAssertion(() => Assert.Contains("Storgata 3, 0155 OSLO will be added when you save", h.Cut.FindComponent<OdsLiveAnnouncer>().Markup));

        Submit(h.Cut, "Create address");

        h.Cut.WaitForAssertion(() => Assert.Single(h.Sent));
        var body = h.Sent[0];
        Assert.Null(body.LocationId);
        Assert.Equal(("Storgata 3", "0155", "OSLO", "NO"), (body.Line1, body.PostalCode, body.City, body.CountryCode));
        Assert.Equal((59.91234m, 10.75012m, "Europe/Oslo"), (body.Latitude!.Value, body.Longitude!.Value, body.TimeZoneId));
        Assert.Equal((LocationSource.Kartverket, "kv-1"), (body.Source!.Value, body.SourceReference));
        Assert.Null(body.ApplyTo);
        h.Api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hand_entry_on_an_existing_address_sends_the_fields_only_for_this_record()
    {
        var h = RenderContactDialog(Address(otherUses: 2));
        await using var _ = h.Ctx;

        await h.EnterByHand("Rue de Rivoli 1");
        Assert.Equal("Rue de Rivoli 1", h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Line 1").Instance.Value);
        Assert.Equal("Address", h.Cut.Find("fieldset.odc-addrblock-hand > legend").FirstChild!.TextContent.Trim());
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && ((string)i.Arguments[0]!).EndsWith("-line1", StringComparison.Ordinal));
        await EditField(h.Cut, "City", "Paris");
        await EditField(h.Cut, "Country", "fr");
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Single(h.Sent));
        var body = h.Sent[0];
        Assert.Equal(("Rue de Rivoli 1", "Paris", "FR"), (body.Line1, body.City, body.CountryCode));
        Assert.Equal(AddressApplyTo.ThisRecord, body.ApplyTo);
        Assert.Equal(2, body.ExpectedOtherUses);
        Assert.Null(body.LocationId);
        Assert.Null(body.Latitude);
        Assert.Null(body.Source);
    }

    [Fact]
    public async Task Search_instead_returns_to_the_picker_and_focuses_it()
    {
        var h = RenderContactDialog(null);
        await using var _ = h.Ctx;

        await h.EnterByHand("Rue");
        h.Cut.FindAll("button").Single(b => b.TextContent.Trim() == "Search instead").Click();

        var picker = h.Cut.FindComponent<OdsLocationSelect>();
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && ((string)i.Arguments[0]!).EndsWith("-picker", StringComparison.Ordinal));
        Assert.Equal(LocationSelectMode.Address, picker.Instance.Mode);
    }

    [Fact]
    public async Task Picking_a_different_place_sends_its_id_without_apply_to()
    {
        var h = RenderContactDialog(Address());
        await using var _ = h.Ctx;

        await h.Pick(LocationPick.Existing(HyttaId));
        Assert.Contains("Shared with 8 other records", h.Cut.Find(".odc-addrblock-usage").TextContent);
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Equal(HyttaId, h.Sent.Single().LocationId));
        Assert.Null(h.Sent.Single().Line1);
        Assert.Null(h.Sent.Single().ApplyTo);
    }

    [Fact]
    public async Task Edit_address_opens_the_location_dialog_for_the_linked_place_with_the_shared_uses_line()
    {
        var h = RenderContactDialog(Address(otherUses: 2));
        await using var _ = h.Ctx;

        var edit = h.Cut.Find("button.odc-addrblock-edit");
        Assert.Equal("Edit address", edit.TextContent.Replace("edit_location_alt", "").Trim());
        Assert.Equal(h.Cut.Find(".odc-addrblock-usage").Id, edit.GetAttribute("aria-describedby"));
        await h.Cut.InvokeAsync(() => edit.Click());

        var dialog = h.Cut.WaitForElement(".loc-dialog, .loc-note");
        Assert.NotNull(dialog);
        var location = h.Cut.FindComponent<LocationDialog>().Instance;
        Assert.Equal(StorgataId, location.Location!.LocationId);
        Assert.Equal(edit.Id, location.ReturnFocusToId);
        Assert.Contains(h.Cut.FindAll(".loc-note"), n => n.TextContent.Contains("Changes apply to every record that uses this address (3)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_locations_update_there_is_no_Edit_address()
    {
        var h = RenderContactDialog(Address(), permissions: new AddressBlockPermissions(true, false, true, true));
        await using var _ = h.Ctx;

        Assert.Empty(h.Cut.FindAll("button.odc-addrblock-edit"));
        Assert.NotEmpty(h.Cut.FindAll(".odc-addrblock-usage"));
    }

    [Fact]
    public async Task Without_locations_create_the_picker_offers_saved_places_only()
    {
        var h = RenderContactDialog(Address(), permissions: new AddressBlockPermissions(true, true, true, false));
        await using var _ = h.Ctx;

        var picker = h.Cut.FindComponent<OdsLocationSelect>().Instance;
        Assert.False(picker.AllowCreate);
        Assert.Null(h.Cut.FindComponent<OdsCombobox>().Instance.FooterOption);
        Assert.Empty(h.Cut.FindAll(".odc-locsel-disclose"));
    }

    [Fact]
    public async Task Without_locations_read_there_is_no_picker_and_an_untouched_save_keeps_the_stored_place()
    {
        var h = RenderContactDialog(Address(), permissions: new AddressBlockPermissions(false, false, false, true));
        await using var _ = h.Ctx;

        Assert.Empty(h.Cut.FindComponents<OdsLocationSelect>());
        Assert.Empty(h.Cut.FindAll(".odc-addrblock-usage, button.odc-addrblock-edit"));
        Assert.DoesNotContain("Search instead", h.Cut.Markup);
        Assert.Equal("Storgata 1", h.Cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Line 1").Instance.Value);
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Equal(StorgataId, h.Sent.Single().LocationId));
        Assert.Null(h.Sent.Single().Line1);
    }

    /// <summary>Accessibility review 1: the summary counts and lists every error, the label first, the address linked to its picker.</summary>
    [Fact]
    public async Task Saving_a_new_address_with_none_chosen_shows_the_summary_linking_to_the_picker()
    {
        var h = RenderContactDialog(null);
        await using var _ = h.Ctx;

        await h.Cut.InvokeAsync(() => h.Cut.FindComponent<OdsContactMethodLabelSelect>().Instance.ValueChanged.InvokeAsync(null));
        Submit(h.Cut, "Create address");

        var summary = h.Cut.WaitForElement(".loc-err-summary");
        var items = summary.QuerySelectorAll("li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Equal([h.Cut.FindComponent<OdsContactMethodLabelSelect>().Instance.Error!, "Choose an address, or enter one by hand."], items);
        var link = summary.QuerySelectorAll("a").Single();
        Assert.EndsWith("-picker", link.GetAttribute("href"), StringComparison.Ordinal);
        await h.Cut.InvokeAsync(() => link.Click());
        Assert.Contains(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && ((string)i.Arguments[0]!).EndsWith("-picker", StringComparison.Ordinal));
        Assert.Equal("Choose an address, or enter one by hand.", h.Cut.FindComponent<OdsLocationSelect>().Instance.Error);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task A_refused_geocode_lands_on_the_picker()
    {
        var h = RenderContactDialog(null,
            reply: _ => ApiResult.Failure(HttpStatusCode.UnprocessableEntity, Problem(422, "latitude", "Send all three or none.")));
        await using var _ = h.Ctx;

        await h.Pick(LocationPick.Address(Candidate()));
        Submit(h.Cut, "Create address");

        h.Cut.WaitForAssertion(() => Assert.Equal(AddressBlockModel.StagedAddressUnusable, h.Cut.FindComponent<OdsLocationSelect>().Instance.Error));
    }

    [Fact]
    public async Task A_403_on_a_relink_says_choosing_a_saved_place_needs_permission()
    {
        var h = RenderContactDialog(Address(), reply: _ => ApiResult.Failure(HttpStatusCode.Forbidden, Problem(403)));
        await using var _ = h.Ctx;

        await h.Pick(LocationPick.Existing(HyttaId));
        Submit(h.Cut, "Save changes");

        h.Cut.WaitForAssertion(() => Assert.Contains("You don’t have permission to choose a saved place.", h.Cut.Markup));
    }

    // ── N2: nothing to save, and nothing to type into, before the stored place loads ─────

    private static Mock<ILocationsApiClient> PendingPlace(TaskCompletionSource<ApiResult<ExistingLocation>> pending)
    {
        var api = Locations(Storgata());
        api.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        return api;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<ProvidersHost> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task The_contact_address_dialog_disables_Save_until_its_field_has_loaded()
    {
        var ctx = Context();
        await using var _ = ctx;
        var contacts = new Mock<IContactsApiClient>(MockBehavior.Strict);
        ctx.Services.AddSingleton(contacts.Object);
        var pending = new TaskCompletionSource<ApiResult<ExistingLocation>>();
        ctx.Services.AddSingleton(PendingPlace(pending).Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<ContactMethodDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Kind, "address")
            .Add(x => x.Draft, ContactMethodDraft.FromAddress(Address()))
            .Add(x => x.IsEdit, true)
            .Add(x => x.ContactType, ContactType.Person)
            .Add(x => x.ContactId, Guid.NewGuid())
            .Add(x => x.Permissions, AddressBlockPermissions.Full)));

        Assert.Contains("Loading address…", cut.Markup);
        Assert.Empty(cut.FindComponents<LocationAddressField>());
        Assert.True(Button(cut, "Save changes").HasAttribute("disabled"));
        Button(cut, "Save changes").Click();
        // The strict mock proves nothing was sent.

        await cut.InvokeAsync(() => pending.SetResult(ApiResult<ExistingLocation>.Success(Storgata(), HttpStatusCode.OK)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<LocationAddressField>()));
        Assert.DoesNotContain("Loading address…", cut.Markup);
        Assert.False(Button(cut, "Save changes").HasAttribute("disabled"));
    }

    // ── Through the property dialog ────────────────────────────────────────────

    private static ExistingProperty Property(Guid? locationId = null) => new()
    {
        PropertyId = Guid.NewGuid(), Name = "Flat", Description = "Home", Type = PropertyType.RealEstate, CurrencyCode = "NOK",
        RealEstateDetails = new RealEstateDetailsDto
        {
            Kind = RealEstateKind.Apartment, LocationId = locationId, AddressLine = "Storgata 1", PostalCode = "0155", City = "Oslo",
            CountryCode = "NO", OtherUses = locationId is null ? null : 1, PlaceHasCoordinates = false,
        },
    };

    private static (BunitContext Ctx, IRenderedComponent<ProvidersHost> Cut, Func<NewProperty?> Sent) RenderPropertyDialog(
        ExistingProperty property, params string[] permissions)
    {
        var ctx = Context(permissions.Length == 0
            ? [PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsCreate]
            : permissions);
        var properties = new Mock<IPropertiesApiClient>();
        NewProperty? sent = null;
        properties.Setup(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<NewProperty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NewProperty body, CancellationToken _) => { sent = body; return ApiResult.Success(HttpStatusCode.OK); });
        ctx.Services.AddSingleton(properties.Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        var api = Locations(Storgata());
        Answer(api, Candidate());
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(new Mock<Odyssey.Client.Theme.IUserPreferenceService>().Object);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<PropertyDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Property, property).Add(x => x.PickerDebounceMs, 0).Add(x => x.AddressDebounceMs, 0)));
        return (ctx, cut, () => sent);
    }

    [Fact]
    public async Task The_property_dialog_offers_no_address_field_before_the_place_loads()
    {
        var ctx = Context(PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate);
        await using var _ = ctx;
        ctx.Services.AddSingleton(new Mock<IPropertiesApiClient>(MockBehavior.Strict).Object);
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        var pending = new TaskCompletionSource<ApiResult<ExistingLocation>>();
        ctx.Services.AddSingleton(PendingPlace(pending).Object);
        ctx.Services.AddSingleton(new Mock<Odyssey.Client.Theme.IUserPreferenceService>().Object);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<PropertyDialog>(d => d.Add(x => x.Open, true).Add(x => x.Property, Property(StorgataId))));

        Assert.Contains("Loading address…", cut.Markup);
        Assert.Empty(cut.FindComponents<LocationAddressField>());
        Assert.True(Button(cut, "Save changes").HasAttribute("disabled"));

        await cut.InvokeAsync(() => pending.SetResult(ApiResult<ExistingLocation>.Success(Storgata(), HttpStatusCode.OK)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<LocationAddressField>()));
        Assert.False(Button(cut, "Save changes").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Clearing_the_property_address_saves_it_unlinked_and_says_the_place_stays()
    {
        var (ctx, cut, sent) = RenderPropertyDialog(Property(StorgataId));
        await using var _ = ctx;
        Assert.False(cut.FindComponent<OdsLocationSelect>().Instance.Required);

        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(null));
        Assert.Contains("The address stays as a saved place on the Locations page.", cut.Markup);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent()));
        Assert.Null(sent()!.RealEstateDetails!.LocationId);
        Assert.Null(sent()!.RealEstateDetails!.AddressLine);
    }

    [Fact]
    public async Task A_property_Kartverket_address_sends_its_geocode_for_this_property_only()
    {
        var (ctx, cut, sent) = RenderPropertyDialog(Property(StorgataId));
        await using var _ = ctx;

        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Address(Candidate())));
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent()));
        var re = sent()!.RealEstateDetails!;
        Assert.Equal(("Storgata 3", "0155", "OSLO", "NO"), (re.AddressLine, re.PostalCode, re.City, re.CountryCode));
        Assert.Equal((59.91234m, LocationSource.Kartverket, "kv-1"), (re.Latitude!.Value, re.Source!.Value, re.SourceReference));
        Assert.Equal(AddressApplyTo.ThisRecord, re.ApplyTo);
        Assert.Equal(1, re.ExpectedOtherUses);
        Assert.Null(re.LocationId);
    }

    [Fact]
    public async Task A_legacy_property_address_opens_in_hand_entry_with_the_four_property_fields()
    {
        var (ctx, cut, sent) = RenderPropertyDialog(Property());
        await using var _ = ctx;

        Assert.Empty(cut.FindComponents<OdsLocationSelect>());
        Assert.Equal(["Line 1", "Postal code", "City", "Country"],
            cut.FindComponent<OdsAddressBlock>().FindComponents<OdsField>().Select(f => f.Instance.Label));
        Assert.Equal("Storgata 1", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Line 1").Instance.Value);
        Assert.Contains("Search instead", cut.Markup);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent()));
        Assert.Equal(("Storgata 1", "Oslo"), (sent()!.RealEstateDetails!.AddressLine, sent()!.RealEstateDetails!.City));
        Assert.Null(sent()!.RealEstateDetails!.ApplyTo);
    }

    // ── The location dialog (§3.3a, #380 §5.5a) ───────────────────────────────

    [Fact]
    public async Task A_Kartverket_address_candidate_fills_the_location_dialogs_Address_group_and_a_place_name_does_not()
    {
        await using var ctx = Context();
        ctx.Services.AddSingleton(Locations().Object);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationDialog>(d => d.Add(x => x.Open, true)));
        string? Value(string label) => cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.Value;

        await cut.InvokeAsync(() => cut.FindComponent<LocationSearch>().Instance.OnPick.InvokeAsync(
            new LocationCandidatePick(Candidate(kind: LocationCandidateKind.PlaceName), Kartverket)));
        Assert.Null(Value("Line 1"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Search again", StringComparison.Ordinal)).Click();
        await cut.InvokeAsync(() => cut.FindComponent<LocationSearch>().Instance.OnPick.InvokeAsync(new LocationCandidatePick(Candidate(), Kartverket)));

        Assert.Equal(("Storgata 3", "0155", "OSLO", "NO"), (Value("Line 1"), Value("Postal code"), Value("City"), Value("Country")));
    }
}
