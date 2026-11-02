using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.UnifiedLocationsTestKit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The design-system additions of issue #383 §4: <c>OdsCombobox</c>'s async mode, <c>OdsLocationSelect</c>,
/// the <c>OdsRadioGroup</c> rebuild and the <c>OdsModal</c> role (AC 3, 5, 9, 10, 12, 13, 19).
/// </summary>
public class UnifiedLocationsComponentTests
{
    // ── OdsAsyncSearch: minimum length, debounce, latest-wins (AC 13) ──────────

    private static OdsOption[] Rows(params string[] names) => [.. names.Select(n => new OdsOption(n, n))];

    [Fact]
    public async Task Below_two_characters_nothing_is_sent_and_the_field_says_so()
    {
        var calls = 0;
        using var search = new OdsAsyncSearch((_, _) => { calls++; return Task.FromResult<IReadOnlyList<OdsOption>>([]); }) { DebounceMs = 0 };

        var rows = await search.RunAsync("H");

        Assert.Empty(rows!);
        Assert.Equal(0, calls);
        Assert.Equal(OdsAsyncSearchStatus.Short, search.Status);
    }

    [Fact]
    public async Task Typing_within_the_pause_sends_only_the_last_query()
    {
        var sent = new List<string>();
        using var search = new OdsAsyncSearch((q, _) => { sent.Add(q); return Task.FromResult<IReadOnlyList<OdsOption>>(Rows(q)); })
        {
            DebounceMs = 120,
        };

        var first = search.RunAsync("Hy");
        var second = search.RunAsync("Hyt");
        var third = search.RunAsync("Hytta");

        Assert.Null(await first);
        Assert.Null(await second);
        Assert.Equal(["Hytta"], (await third)!.Select(o => o.Value));
        Assert.Equal(["Hytta"], sent);
        Assert.Equal(OdsAsyncSearchStatus.Done, search.Status);
    }

    [Fact]
    public async Task An_in_flight_request_is_cancelled_and_its_answer_never_renders()
    {
        var release = new TaskCompletionSource();
        CancellationToken firstToken = default;
        using var search = new OdsAsyncSearch(async (q, ct) =>
        {
            if (q == "Hy")
            {
                firstToken = ct;
                await release.Task;
            }

            return Rows(q);
        })
        { DebounceMs = 0 };

        var slow = search.RunAsync("Hy");
        var fast = await search.RunAsync("Hytta");
        release.SetResult();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Null(await slow);
        Assert.Equal("Hytta", fast!.Single().Value);
        Assert.Equal("Hytta", search.Results.Single().Value);
    }

    [Fact]
    public async Task A_failed_search_says_it_could_not_load()
    {
        using var search = new OdsAsyncSearch((_, _) => throw new InvalidOperationException("500")) { DebounceMs = 0 };

        await search.RunAsync("Hytta");

        Assert.Equal(OdsAsyncSearchStatus.Failed, search.Status);
    }

    // ── OdsLocationSelect (AC 3, 13) ────────────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<OdsLocationSelect> Cut, Mock<ILocationsApiClient> Api, List<LocationPick?> Picks)
        RenderSelect(LocationPick? value = null, bool allowCreate = true, LocationPickFilter filter = LocationPickFilter.All, params ExistingLocation[] places)
    {
        var ctx = Context();
        var api = Locations(places);
        ctx.Services.AddSingleton(api.Object);
        var picks = new List<LocationPick?>();
        var cut = ctx.Render<OdsLocationSelect>(p => p
            .Add(s => s.Value, value)
            .Add(s => s.AllowCreate, allowCreate)
            .Add(s => s.Filter, filter)
            .Add(s => s.DebounceMs, 0)
            .Add(s => s.ValueChanged, EventCallback.Factory.Create<LocationPick?>(new object(), v => picks.Add(v))));
        return (ctx, cut, api, picks);
    }

    [Fact]
    public async Task Picking_create_place_stages_it_and_calls_no_api()
    {
        var (ctx, cut, api, _) = RenderSelect();
        await using var _ctx = ctx;
        var combobox = cut.FindComponent<OdsCombobox>().Instance;

        var staged = combobox.OnCreate!("Hytta", null);

        Assert.Equal("new:Hytta", staged!.Value);
        Assert.Equal("Hytta (new)", staged.Label);
        api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(LocationPick.Staged("Hytta"), OdsLocationSelect.FromComboValue(staged.Value));
    }

    [Fact]
    public async Task Without_locations_create_there_is_no_create_row()
    {
        var (ctx, cut, _, _) = RenderSelect(allowCreate: false);
        await using var _ctx = ctx;

        Assert.Null(cut.FindComponent<OdsCombobox>().Instance.OnCreate);
    }

    [Fact]
    public async Task The_search_asks_for_one_page_of_twenty_active_places_and_never_the_unpaged_list()
    {
        var (ctx, cut, api, _) = RenderSelect(places: [Hytta(), Hytta(archived: true) with { LocationId = Guid.NewGuid() }]);
        await using var _ctx = ctx;

        var rows = await cut.FindComponent<OdsCombobox>().Instance.SearchAsync!("Hyt", CancellationToken.None);

        Assert.Single(rows);
        api.Verify(a => a.ListAsync(1, OdsLocationSelect.PageSize, "Hyt", It.Is<IReadOnlyCollection<string>>(s => s.Single() == "Active"),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(20, OdsLocationSelect.PageSize);
    }

    [Fact]
    public void Archived_merged_excluded_and_address_incomplete_places_are_never_offered()
    {
        var hytta = Hytta();
        Assert.True(OdsLocationSelect.Offerable(hytta, LocationPickFilter.All, null));
        Assert.False(OdsLocationSelect.Offerable(Hytta(archived: true), LocationPickFilter.All, null));
        Assert.False(OdsLocationSelect.Offerable(Hytta(mergedInto: StorgataId), LocationPickFilter.All, null));
        Assert.False(OdsLocationSelect.Offerable(hytta, LocationPickFilter.All, [HyttaId]));
        Assert.False(OdsLocationSelect.Offerable(hytta with { CountryCode = null }, LocationPickFilter.AddressComplete, null));
        Assert.True(OdsLocationSelect.Offerable(hytta with { CountryCode = null }, LocationPickFilter.All, null));
    }

    [Fact]
    public async Task A_stored_archived_link_outside_the_loaded_rows_still_renders_as_archived()
    {
        var (ctx, cut, api, _) = RenderSelect(LocationPick.Existing(HyttaId), places: [Hytta(archived: true)]);
        await using var _ctx = ctx;

        var resolved = await cut.FindComponent<OdsCombobox>().Instance.ResolveAsync!(HyttaId.ToString());

        Assert.Equal("Hytta (archived)", resolved!.Label);
        api.Verify(a => a.GetAsync(HyttaId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public void An_option_is_named_with_its_address_and_states_in_words()
    {
        var option = LocationVisuals.OptionFor(Hytta(coordinates: false, archived: true));

        Assert.Equal("Hytta, Fjellveien 12, 2630 Ringebu, NO, no coordinates, archived", option.AriaLabel);
        Assert.Equal("Fjellveien 12, 2630 Ringebu, NO · No coordinates", option.Sub);
        Assert.Equal("Hytta, Fjellveien 12, 2630 Ringebu, NO", LocationVisuals.OptionFor(Hytta()).AriaLabel);
    }

    [Fact]
    public async Task The_create_row_shows_the_disclosure_and_its_accessible_name_contains_it()
    {
        await using var ctx = Context();
        var row = OdsCombobox.CreateRow("Create place", OdsLocationSelect.CreateDisclosure, " create \u001fHytta", "Hytta");
        var cut = ctx.Render<OdsComboboxOptionText>(p => p.Add(t => t.Option, row));

        Assert.True(row.IsCreate);
        Assert.Equal(
            "Create place \"Hytta\" — adds a saved place that everyone who can see locations can see",
            cut.Find(".odc-sr-only").TextContent);
        Assert.Equal(OdsLocationSelect.CreateDisclosure, cut.Find(".odc-combo-opt-sub").TextContent);
        Assert.Equal("true", cut.Find(".odc-combo-opt-text").GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task The_async_status_line_is_one_polite_region_outside_the_popover()
    {
        await using var ctx = Context();
        ctx.Services.AddSingleton(Locations().Object);
        var cut = ctx.Render<OdsLocationSelect>(p => p.Add(s => s.DebounceMs, 0));

        var status = cut.Find("span.odc-sr-only[role=status]");
        Assert.Equal("polite", status.GetAttribute("aria-live"));

        await cut.InvokeAsync(() => cut.FindComponent<OdsCombobox>().Instance.SearchAsync!("H", CancellationToken.None));
        var combobox = cut.FindComponent<OdsCombobox>().Instance;
        Assert.Equal("Type at least 2 characters", combobox.MinCharsText);
        Assert.Equal("Couldn’t load places", combobox.ErrorText);
        Assert.Equal("3 places found", combobox.ResultsText!(3));
        Assert.Equal("1 place found", combobox.ResultsText!(1));
    }

    // ── OdsRadioGroup (A11Y 1, 9; AC 5, 19) ─────────────────────────────────────

    private static IReadOnlyList<OdsRadioOption> ApplyOptions =>
    [
        new("AllUses", "All 4 uses of this address") { Description = "Every contact, property and event at this place is updated." },
        new("ThisRecord", "Only this contact") { Description = "This contact moves to the new address; the others keep the old one." },
    ];

    [Fact]
    public async Task A_labelled_group_is_a_fieldset_with_a_legend_and_native_radios()
    {
        await using var ctx = Context();
        var cut = ctx.Render<OdsRadioGroup>(p => p
            .Add(r => r.Label, "Apply this change to")
            .Add(r => r.Required, true)
            .Add(r => r.Options, ApplyOptions));

        var legend = System.Text.RegularExpressions.Regex.Replace(cut.Find("fieldset > legend").TextContent, @"\s+", "");
        Assert.Equal("Applythischangeto*Required", legend);
        Assert.Equal("Required", cut.Find("legend .odc-radio-reqword").TextContent);
        var group = cut.Find("[role=radiogroup]");
        Assert.Equal("true", group.GetAttribute("aria-required"));
        Assert.Equal(cut.Find("legend").Id, group.GetAttribute("aria-labelledby"));
        var radios = cut.FindAll("input[type=radio]");
        Assert.Equal(2, radios.Count);
        // One name: the browser gives the group one tab stop and arrow-key movement.
        Assert.Single(radios.Select(r => r.GetAttribute("name")).Distinct());
        Assert.All(radios, r => Assert.False(r.HasAttribute("checked")));
    }

    [Fact]
    public async Task Each_option_is_named_by_its_label_and_described_by_its_consequence()
    {
        await using var ctx = Context();
        var cut = ctx.Render<OdsRadioGroup>(p => p.Add(r => r.Label, "Apply this change to").Add(r => r.Options, ApplyOptions));

        var radio = cut.FindAll("input[type=radio]")[1];
        var description = cut.Find($"#{radio.GetAttribute("aria-describedby")}");
        Assert.Equal("This contact moves to the new address; the others keep the old one.", description.TextContent);
        Assert.Equal("Only this contact", radio.ParentElement!.QuerySelector(".odc-radio-label")!.TextContent);
    }

    [Fact]
    public async Task An_error_sets_aria_invalid_and_is_in_the_groups_description()
    {
        await using var ctx = Context();
        var cut = ctx.Render<OdsRadioGroup>(p => p
            .Add(r => r.Label, "Apply this change to")
            .Add(r => r.Options, ApplyOptions)
            .Add(r => r.Error, "Choose who this change applies to")
            .Add(r => r.DescribedById, "warn"));

        var group = cut.Find("[role=radiogroup]");
        Assert.Equal("true", group.GetAttribute("aria-invalid"));
        var ids = group.GetAttribute("aria-describedby")!.Split(' ');
        Assert.Equal("Choose who this change applies to", cut.Find($"#{ids[0]}").TextContent);
        Assert.Equal("alert", cut.Find($"#{ids[0]}").GetAttribute("role"));
        Assert.Contains("warn", ids);
    }

    [Fact]
    public async Task Choosing_a_radio_selects_it_and_FocusAsync_targets_the_group()
    {
        await using var ctx = Context();
        string? chosen = null;
        var cut = ctx.Render<OdsRadioGroup>(p => p
            .Add(r => r.Label, "Apply this change to")
            .Add(r => r.Id, "apply")
            .Add(r => r.Options, ApplyOptions)
            .Add(r => r.ValueChanged, EventCallback.Factory.Create<string>(new object(), v => chosen = v)));

        cut.FindAll("input[type=radio]")[1].Change(true);
        await cut.InvokeAsync(() => cut.Instance.FocusAsync());

        Assert.Equal("ThisRecord", chosen);
        Assert.Equal("apply", cut.Find("fieldset").Id);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], "apply"));
    }

    // ── OdsModal role (AC 9, 10) ────────────────────────────────────────────────

    [Fact]
    public void The_modal_role_is_dialog_or_alertdialog()
    {
        Assert.Equal("dialog", OdsModal.RoleAttribute(OdsModalRole.Dialog));
        Assert.Equal("alertdialog", OdsModal.RoleAttribute(OdsModalRole.AlertDialog));
    }

    // ── The place line on the journal card (§3.5) ───────────────────────────────

    [Fact]
    public async Task The_place_line_reads_the_place_then_the_details_and_archived_in_words()
    {
        await using var ctx = Context();
        var cut = ctx.Render<LocationPlaceLine>(p => p
            .Add(l => l.Place, new LocationRef { LocationId = HyttaId, Name = "Hytta", Archived = true })
            .Add(l => l.Details, "<b>Room 4</b>"));

        Assert.Equal("placeHytta (archived)", cut.Find(".loc-place-line-name").TextContent);
        // Plain text, never markup (SEC F9).
        Assert.Equal("<b>Room 4</b>", cut.Find(".loc-place-line-details").TextContent);
        Assert.Empty(cut.FindAll(".loc-place-line-details b"));
    }

    [Fact]
    public async Task Without_a_readable_place_only_the_details_show()
    {
        await using var ctx = Context();
        var cut = ctx.Render<LocationPlaceLine>(p => p.Add(l => l.Place, null).Add(l => l.Details, "Room 4"));

        Assert.Equal("placeRoom 4", cut.Find(".loc-place-line").TextContent.Trim());
        Assert.DoesNotContain("archived", cut.Markup);
    }
}
