using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Locations page and the copy defined once in <see cref="LocationVisuals"/> (issue #383 §3.6–§3.10,
/// AC 7, 8, 11, 15, 17, 19).
/// </summary>
public class UnifiedLocationsPageTests
{
    // ── Controls by claim (AC 8, 10; SEC F7) ───────────────────────────────────

    /// <summary>The roles' location claims (#380 §6): Admin, Owner and User hold all four; Guest reads only.</summary>
    public static TheoryData<string, string[]> Roles => new()
    {
        { "Admin", [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsDelete] },
        { "Owner", [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsDelete] },
        { "User", [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsDelete] },
        { "Guest", [PermissionClaims.LocationsRead] },
        { "Read-only", [PermissionClaims.LocationsRead] },
    };

    private static IReadOnlyList<OdsMenuItem> Menu(ExistingLocation row, string[] claims) =>
        [.. LocationsPage.BuildRowActions(row,
            new LocationsPage.LocationRowClaims(claims.Contains(PermissionClaims.LocationsUpdate), claims.Contains(PermissionClaims.LocationsDelete)),
            default, default, default, default, default, default)
            .Where(i => !i.Divider)];

    [Theory]
    [MemberData(nameof(Roles))]
    public void The_row_menu_follows_the_claim_table(string role, string[] claims)
    {
        var canUpdate = claims.Contains(PermissionClaims.LocationsUpdate);
        var canDelete = claims.Contains(PermissionClaims.LocationsDelete);
        var labels = Menu(Hytta(), claims).Select(i => i.Label).ToList();

        Assert.Equal(canUpdate, labels.Contains("Edit location"));
        Assert.Equal(canUpdate, labels.Contains("Merge into…"));
        Assert.Equal(canUpdate, labels.Contains("Archive"));
        Assert.Equal(canDelete, labels.Contains("Delete"));
        Assert.Contains("Copy ID", labels);
        Assert.Contains("Copy coordinates", labels);
        Assert.Equal(canUpdate, LocationsPage.ShowsFindCoordinates(Hytta(coordinates: false), canUpdate));
        if (role == "Guest")
            Assert.DoesNotContain("Delete", labels);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void A_merged_away_row_offers_only_copy_id_and_delete(string role, string[] claims)
    {
        _ = role;
        var labels = Menu(Hytta(archived: true, mergedInto: StorgataId), claims).Select(i => i.Label).ToList();

        var expected = claims.Contains(PermissionClaims.LocationsDelete) ? new[] { "Copy ID", "Delete" } : ["Copy ID"];
        Assert.Equal(expected, labels);
        Assert.False(LocationsPage.ShowsFindCoordinates(Hytta(coordinates: false, mergedInto: StorgataId), canUpdate: true));
    }

    [Fact]
    public void An_archived_row_cannot_be_merged_but_can_be_restored()
    {
        var labels = Menu(Hytta(archived: true), [PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate]).Select(i => i.Label).ToList();

        Assert.DoesNotContain("Merge into…", labels);
        Assert.Contains("Restore", labels);
    }

    [Fact]
    public void Copy_coordinates_is_offered_only_with_coordinates()
    {
        Assert.DoesNotContain("Copy coordinates", Menu(Hytta(coordinates: false), [PermissionClaims.LocationsRead]).Select(i => i.Label));
    }

    [Fact]
    public void Every_item_names_the_place_and_starts_with_its_visible_label()
    {
        var items = Menu(Hytta(), [PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsDelete]);

        Assert.All(items, i =>
        {
            Assert.Contains("Hytta", i.AriaLabel);
            Assert.StartsWith(i.Label!.TrimEnd('…'), i.AriaLabel!, StringComparison.Ordinal);
        });
        Assert.Contains(items, i => i.AriaLabel == "Delete Hytta");
    }

    // ── Delete focus (AC 10, FE m6) ────────────────────────────────────────────

    [Fact]
    public void After_a_delete_focus_goes_to_the_next_row_else_the_previous_else_the_heading()
    {
        var a = Hytta() with { LocationId = Guid.NewGuid() };
        var b = Hytta() with { LocationId = Guid.NewGuid() };
        var c = Hytta() with { LocationId = Guid.NewGuid() };
        ExistingLocation[] rows = [a, b, c];

        Assert.Equal(c.LocationId, LocationsPage.NeighbourOf(rows, b.LocationId));
        Assert.Equal(b.LocationId, LocationsPage.NeighbourOf(rows, c.LocationId));
        Assert.Null(LocationsPage.NeighbourOf([a], a.LocationId));
    }

    // ── Archive keeps the address (#380 full replacement) ───────────────────────

    [Fact]
    public void Archiving_round_trips_the_structured_address()
    {
        var body = LocationsPage.WithArchived(Storgata(), archived: true);

        Assert.Equal(("Storgata 1", "Etg. 4", "0155", "Oslo", "Oslo", "NO"),
            (body.Line1, body.Line2, body.PostalCode, body.City, body.Region, body.CountryCode));
        Assert.Equal(LocationSource.ContactAddress, body.Source);
    }

    // ── Filters (AC 8) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { LocationVisuals.HasCoordinatesKey }, true)]
    [InlineData(new[] { LocationVisuals.NeedsCoordinatesKey }, false)]
    [InlineData(new[] { LocationVisuals.HasCoordinatesKey, LocationVisuals.NeedsCoordinatesKey }, null)]
    public void The_coordinates_filter_sends_a_parameter_only_for_exactly_one_choice(string[] selected, bool? expected)
    {
        Assert.Equal(expected, LocationVisuals.FilterValue(selected, LocationVisuals.HasCoordinatesKey, LocationVisuals.NeedsCoordinatesKey));
    }

    [Theory]
    [InlineData(new[] { LocationVisuals.UsedKey }, true)]
    [InlineData(new[] { LocationVisuals.NotUsedKey }, false)]
    [InlineData(new[] { LocationVisuals.UsedKey, LocationVisuals.NotUsedKey }, null)]
    public void The_use_filter_sends_referenced(string[] selected, bool? expected)
    {
        Assert.Equal(expected, LocationVisuals.FilterValue(selected, LocationVisuals.UsedKey, LocationVisuals.NotUsedKey));
    }

    [Theory]
    [InlineData(@"Locations\.ListAllAsync\([^;]*HasCoordinatesQuery,\s*ReferencedQuery")]
    [InlineData(@"ArchivedRowMatchesAsync[\s\S]*?hasCoordinates: HasCoordinatesQuery, referenced: ReferencedQuery")]
    [InlineData(@"CoordinatesFilter = \[\.\. _coordinatesFilter\]")]
    [InlineData(@"UseFilter = \[\.\. _useFilter\]")]
    [InlineData(@"_coordinatesFilter = LocationVisuals\.CoordinatesOptions\.KnownValues\(state\.CoordinatesFilter\)")]
    [InlineData(@"_useFilter = LocationVisuals\.UseOptions\.KnownValues\(state\.UseFilter\)")]
    public void Both_filters_reach_the_list_the_archived_hint_and_the_page_state(string pattern)
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Journal", "LocationsPage.razor.cs"));

        Assert.Matches(new Regex(pattern), code);
    }

    // ── N4: merge-target names ────────────────────────────────────────────────

    [Fact]
    public async Task Merge_target_names_are_fetched_in_parallel_under_the_cap()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var inFlight = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();

        var fetching = LocationsPage.FetchNamesAsync(ids, async (id, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await gate.Task;
            Interlocked.Decrement(ref inFlight);
            return id.ToString();
        }, LocationsPage.MergeTargetConcurrency, CancellationToken.None);

        await Task.Delay(50);
        Assert.Equal(LocationsPage.MergeTargetConcurrency, Volatile.Read(ref inFlight));
        gate.SetResult();
        var names = await fetching;

        Assert.Equal(LocationsPage.MergeTargetConcurrency, peak);
        Assert.Equal(ids.Count, names.Count);
    }

    [Fact]
    public async Task A_failed_or_empty_merge_target_lookup_is_left_out_not_thrown()
    {
        var ok = Guid.NewGuid();
        var failing = Guid.NewGuid();
        var gone = Guid.NewGuid();

        var names = await LocationsPage.FetchNamesAsync([ok, failing, gone], (id, _) =>
            id == failing ? throw new HttpRequestException("500")
            : Task.FromResult(id == ok ? "Hytta" : null), 4, CancellationToken.None);

        Assert.Equal(new Dictionary<Guid, string> { [ok] = "Hytta" }, names);
    }

    [Fact]
    public async Task A_superseded_merge_target_lookup_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var fetching = LocationsPage.FetchNamesAsync([Guid.NewGuid()], async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, 4, cts.Token);

        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
    }

    [Fact]
    public void The_merge_targets_are_awaited_by_the_load_and_cancelled_on_dispose()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Journal", "LocationsPage.razor.cs"));

        Assert.DoesNotContain("_ = ResolveMergeTargetsAsync", code, StringComparison.Ordinal);
        Assert.Contains("await ResolveMergeTargetsAsync(_locations);", code, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"public void Dispose\(\)\s*\{\s*CancelMergeTargets\(\);"), code);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public void The_two_filters_are_separate_multi_selects_beside_status()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Journal", "LocationsPage.razor"));

        Assert.Contains(@"Label=""Coordinates""", markup);
        Assert.Contains(@"Label=""Use""", markup);
        Assert.Contains(@"FilteredEmptyTitle=""@LocationVisuals.NoFilterMatches""", markup);
        Assert.Equal("No places match these filters.", LocationVisuals.NoFilterMatches);
    }

    // ── Sources and chips (AC 8; FE m1) ────────────────────────────────────────

    [Theory]
    [InlineData(LocationSource.Converted, "Converted from a text location")]
    [InlineData(LocationSource.ContactAddress, "From a contact address")]
    [InlineData(LocationSource.PropertyAddress, "From a property address")]
    [InlineData(LocationSource.Imported, "From an import")]
    [InlineData(LocationSource.Manual, "Entered by hand")]
    [InlineData((LocationSource)99, "Other source")]
    public void Every_source_reads_in_words_and_an_unknown_one_is_never_entered_by_hand(LocationSource source, string label)
    {
        Assert.Equal(label, LocationVisuals.SourceOf(source).Label);
    }

    [Fact]
    public void The_chips_are_words()
    {
        Assert.Equal("No coordinates", LocationVisuals.NoCoordinates);
        Assert.Equal("Merged into Hytta", LocationVisuals.MergedInto("Hytta"));
    }

    // ── Usage copy (AC 7, AC 19) ───────────────────────────────────────────────

    private static readonly LocationUsage AllReadable = new() { Contacts = 2, Properties = 1, CalendarEvents = 5, RecurringSeries = 0, JournalEntries = 0 };
    private static readonly LocationUsage SomeHidden = new() { Contacts = 2, Properties = null, CalendarEvents = 5, RecurringSeries = 0, JournalEntries = null };

    [Fact]
    public void The_collapsed_sub_line_sums_visible_counts_only()
    {
        Assert.Equal("Used by 8", LocationVisuals.UsageSubLine(AllReadable));
        Assert.Equal("Used by 7 you can see", LocationVisuals.UsageSubLine(SomeHidden));
    }

    [Fact]
    public void Not_used_is_qualified_whenever_a_kind_is_hidden()
    {
        var zero = new LocationUsage { Contacts = 0, Properties = 0, CalendarEvents = 0, RecurringSeries = 0, JournalEntries = 0 };
        var hiddenZero = zero with { JournalEntries = null };

        Assert.Equal("Not used", LocationVisuals.UsageSubLine(zero));
        Assert.Equal("Not used in anything you can see", LocationVisuals.UsageSubLine(hiddenZero));
        Assert.Equal("Not used", LocationVisuals.UsageTile(zero));
        Assert.Equal("Not used in anything you can see", LocationVisuals.UsageTile(hiddenZero));
    }

    [Fact]
    public void The_tile_lists_kinds_in_words_and_events_include_repeats()
    {
        Assert.Equal("2 contacts · 1 property · 5 events (including repeats)", LocationVisuals.UsageTile(AllReadable));
        Assert.Equal("1 event (including repeats)", LocationVisuals.UsageBreakdown(new LocationUsage { CalendarEvents = 1 }));
    }

    [Fact]
    public void The_shared_line_says_you_can_see_when_a_read_claim_is_missing()
    {
        Assert.Equal("Shared with 3 other records", LocationVisuals.SharedWith(3, allReadable: true));
        Assert.Equal("Shared with 1 other record you can see", LocationVisuals.SharedWith(1, allReadable: false));
    }

    [Fact]
    public void The_address_usage_line_has_its_four_variants()
    {
        Assert.Equal("Shared with 3 other records — 2 contacts, 1 event (including repeats)",
            LocationVisuals.AddressUsageLine(3, new LocationUsage { Contacts = 2, CalendarEvents = 1 }, true, "contact", picked: false));
        Assert.Equal("Only this contact uses this address", LocationVisuals.AddressUsageLine(0, null, true, "contact", false));
        Assert.Equal("No other records you can see use this address", LocationVisuals.AddressUsageLine(0, null, false, "property", false));
        Assert.Equal("Shared with 2 other records you can see", LocationVisuals.AddressUsageLine(2, null, false, "contact", false));
    }

    [Fact]
    public void The_address_block_and_the_record_line_need_every_read_claim_to_drop_you_can_see()
    {
        static System.Security.Claims.ClaimsPrincipal User(params string[] claims) => new(new System.Security.Claims.ClaimsIdentity(
            claims.Select(c => new System.Security.Claims.Claim(PermissionClaims.Type, c)), "test"));

        var all = AddressBlockPermissions.For(User(PermissionClaims.LocationsRead, PermissionClaims.ContactsRead,
            PermissionClaims.PropertiesRead, PermissionClaims.CalendarRead, PermissionClaims.JournalRead));
        var noJournal = AddressBlockPermissions.For(User(PermissionClaims.LocationsRead, PermissionClaims.ContactsRead,
            PermissionClaims.PropertiesRead, PermissionClaims.CalendarRead));

        Assert.True(all.AllKindsReadable);
        Assert.False(noJournal.AllKindsReadable);
        var address = new ExistingAddress { Id = Guid.NewGuid(), ContactId = Guid.NewGuid(), Line1 = "Storgata 1", City = "Oslo", CountryCode = "NO", OtherUses = 2 };
        Assert.Equal("Shared with 2 other records you can see", ContactDetailPanel.SharedLineFor(address, noJournal));
        Assert.Null(ContactDetailPanel.SharedLineFor(address, new AddressBlockPermissions(false, false, true)));
        Assert.Null(ContactDetailPanel.SharedLineFor(address with { OtherUses = 0 }, all));
        Assert.Equal("Shared with 1 other record",
            PropertiesCard.SharedLineFor(new Odyssey.Dtos.Finance.RealEstateDetailsDto { LocationId = HyttaId, OtherUses = 1 }, all));
    }

    // ── Record deletes (AC 11) ─────────────────────────────────────────────────

    [Fact]
    public void Every_record_delete_confirmation_says_the_place_stays()
    {
        const string sentence = "Its address (or place) stays as a saved place on the Locations page.";

        Assert.Equal(sentence, LocationVisuals.RecordDeleteSentence);
        Assert.EndsWith(sentence, ContactsCard.DeleteMessage("Ada"));
        Assert.EndsWith(sentence, JournalCard.DeleteMessage("Hike"));
        Assert.EndsWith(sentence, CalendarEventDialog.DeleteMessage("event", "Dinner"));
        Assert.Contains("LocationVisuals.RecordDeleteSentence",
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "DeletePropertyDialog.razor")));
    }

    // ── Import result lines (AC 17) ─────────────────────────────────────────────

    [Fact]
    public void The_import_lines_count_linked_and_new_places()
    {
        Assert.Equal("3 places linked · 1 new place added", LocationVisuals.PlacesImportLine(3, 1));
        Assert.Null(LocationVisuals.PlacesImportLine(0, 0));
        Assert.Equal("2 addresses linked to saved places · 2 new places added", LocationVisuals.AddressesImportLine(2, 2));
        // Without locations.read the created count is null and is left out (v8-L3).
        Assert.Equal("1 address linked to saved places", LocationVisuals.AddressesImportLine(1, null));
        Assert.Equal("4 addresses skipped — adding places needs permission", LocationVisuals.SkippedAddressesLine(4));
        Assert.Null(LocationVisuals.SkippedAddressesLine(0));
    }

    [Theory]
    [InlineData("Calendar/ImportCalendarDialog.razor", "PlacesImportLine(_result.PlacesLinked, _result.PlacesCreated)")]
    [InlineData("Journal/ImportJournalEntriesDialog.razor", "PlacesImportLine(_result.PlacesLinked, _result.PlacesCreated)")]
    [InlineData("Finance/ContactImportDialog.razor", "AddressesImportLine(_result.AddressesLinked, _result.PlacesCreated)")]
    [InlineData("Finance/ContactImportDialog.razor", "SkippedAddressesLine(_result.SkippedAddressCount)")]
    public void Each_import_dialog_shows_its_result_lines(string file, string call)
    {
        Assert.Contains(call, File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", file)));
    }

    // ── Null coordinates (AC 15) ───────────────────────────────────────────────

    [Fact]
    public void Every_coordinate_formatter_handles_a_place_without_coordinates()
    {
        var place = Hytta(coordinates: false);

        Assert.Equal("No coordinates", LocationVisuals.FormatPair(place.Latitude, place.Longitude));
        Assert.Equal("No coordinates", LocationVisuals.FormatHemispheres(place.Latitude, place.Longitude));
        Assert.Null(LocationVisuals.NowThere(place.TimeZoneId, DateTime.UtcNow));
        Assert.False(WeatherVisuals.HasPosition(place));
        Assert.True(WeatherVisuals.HasPosition(Hytta()));
        Assert.Equal(WeatherRules.NoCoordinatesFingerprint, WeatherVisuals.Fingerprint(place));
    }
}
