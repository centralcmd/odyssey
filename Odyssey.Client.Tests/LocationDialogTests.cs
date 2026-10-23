using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The New / Edit location dialog and its geocoding search (issue #350; design system ·
/// AddLocationModal.jsx). Provenance stays honest — a provider row never carries coordinates the provider
/// did not return — a missing time zone is asked for rather than guessed, the candidate's description is
/// never copied into the household's own note, and PUT round-trips the stored archived flag.
/// </summary>
public class LocationDialogTests
{
    private static readonly LocationAttribution Kartverket = new()
    {
        Text = "© Kartverket", License = "CC BY 4.0", Url = "https://www.kartverket.no/",
    };

    private static readonly LocationAttribution OpenMeteo = new()
    {
        Text = "© Open-Meteo", License = "CC BY 4.0", Url = "https://open-meteo.com/",
    };

    private static LocationCandidate Storgata(string? timeZone = "Europe/Oslo") => new()
    {
        Name = "Storgata 1",
        Description = "0155 Oslo",
        Kind = LocationCandidateKind.Address,
        AddressText = "Storgata 1, 0155 Oslo",
        Latitude = 59.91273m,
        Longitude = 10.75027m,
        AltitudeMeters = 12,
        TimeZoneId = timeZone,
        Source = LocationSource.Kartverket,
        SourceReference = "0301-12345-1",
    };

    private static ExistingLocation Cabin(bool archived = false) => new()
    {
        LocationId = Guid.NewGuid(),
        Name = "Cabin",
        Description = "Key under the mat",
        AddressText = "Fjellveien 4, Ringebu",
        Latitude = 61.52912m,
        Longitude = 10.13850m,
        AltitudeMeters = 640,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Kartverket,
        SourceReference = "3439-1-4",
        Attribution = Kartverket,
        Archived = archived ? new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static (IRenderedComponent<DialogHost> Cut, Mock<ILocationsApiClient> Api) Render(
        ExistingLocation? location = null,
        Func<LocationSearchRequest, ApiResult<LocationSearchResponse>>? search = null,
        ApiResult<ExistingLocation>? saveResult = null,
        bool canSearch = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var api = new Mock<ILocationsApiClient>();
        api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LocationSearchRequest r, CancellationToken _) => search?.Invoke(r)
                ?? ApiResult<LocationSearchResponse>.Success(new LocationSearchResponse
                {
                    Region = r.Region!.Value, Candidates = [Storgata()], Attribution = Kartverket,
                }, HttpStatusCode.OK));
        api.Setup(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewLocation body, CancellationToken _) => saveResult ?? ApiResult<ExistingLocation>.Success(new ExistingLocation
            {
                LocationId = Guid.NewGuid(), Name = body.Name, TimeZoneId = body.TimeZoneId!,
            }, HttpStatusCode.Created));
        api.Setup(a => a.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateLocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateLocation body, CancellationToken _) => saveResult ?? ApiResult<ExistingLocation>.Success(new ExistingLocation
            {
                LocationId = id, Name = body.Name, TimeZoneId = body.TimeZoneId!,
            }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Location, location)
            .Add(h => h.CanSearch, canSearch));
        return (cut, api);
    }

    private static void Type(IRenderedComponent<DialogHost> cut, string text) =>
        cut.Find(".loc-search input[role=combobox]").Input(text);

    private static void PickFirst(IRenderedComponent<DialogHost> cut)
    {
        cut.WaitForElement("li[role=option]", TimeSpan.FromSeconds(3));
        cut.Find("li[role=option]").MouseDown();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".loc-search")), TimeSpan.FromSeconds(3));
    }

    private static Task SetField(IRenderedComponent<DialogHost> cut, string label, string value)
    {
        var field = cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label);
        return cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private static string? FieldError(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindComponents<OdsField>().Single(f => f.Instance.Label == label).Instance.Error;

    private static void Click(IRenderedComponent<DialogHost> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    [Fact]
    public void Create_opens_on_the_search_step_and_names_the_one_provider_that_receives_the_text()
    {
        var (cut, _) = Render();

        Assert.NotNull(cut.Find(".loc-search input[role=combobox]"));
        Assert.Contains("sent to Kartverket (the Norwegian Mapping Authority)", cut.Find(".loc-search-help").TextContent);
        Assert.Empty(cut.FindComponents<OdsField>());
        Assert.Contains("Enter by hand", cut.Markup);
    }

    [Fact]
    public void Nothing_is_sent_below_the_regions_minimum()
    {
        var (cut, api) = Render();

        Type(cut, "Å");

        cut.WaitForAssertion(() => Assert.Contains("Type at least 2 characters to search in Norway.", cut.Markup));
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Two_characters_search_Norway()
    {
        var (cut, api) = Render();

        Type(cut, "Ål");

        cut.WaitForAssertion(() => api.Verify(a => a.SearchAsync(
            It.Is<LocationSearchRequest>(r => r.Query == "Ål" && r.Region == LocationSearchRegion.Norway),
            It.IsAny<CancellationToken>()), Times.Once), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Over_the_maximum_says_so_and_sends_nothing()
    {
        var (cut, api) = Render();

        Type(cut, new string('a', LocationRules.QueryMaxLength + 1));

        cut.WaitForAssertion(() => Assert.Contains($"Searches are limited to {LocationRules.QueryMaxLength} characters", cut.Markup));
        Assert.Equal("true", cut.Find(".loc-search input[role=combobox]").GetAttribute("aria-invalid"));
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_503_shows_the_pages_own_copy_and_no_provider_text()
    {
        var (cut, _) = Render(search: _ => ApiResult<LocationSearchResponse>.Failure(
            HttpStatusCode.ServiceUnavailable, new ApiProblem { Status = 503, Code = "geocoding-unavailable", Detail = "upstream said <b>no</b>" }));

        Type(cut, "Storgata 1");

        cut.WaitForAssertion(() => Assert.Contains("Kartverket isn’t answering right now.", cut.Markup), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("upstream said", cut.Markup);
        Assert.Contains("Try again", cut.Markup);
    }

    [Fact]
    public void A_429_reads_the_same_for_either_limit()
    {
        var (cut, _) = Render(search: _ => ApiResult<LocationSearchResponse>.Failure(
            HttpStatusCode.TooManyRequests, new ApiProblem { Status = 429 }));

        Type(cut, "Storgata 1");

        cut.WaitForAssertion(() => Assert.Contains("You’ve searched a lot in the last minute.", cut.Markup), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_partial_response_says_results_may_be_missing()
    {
        var (cut, _) = Render(search: r => ApiResult<LocationSearchResponse>.Success(new LocationSearchResponse
        {
            Region = LocationSearchRegion.Norway, Candidates = [Storgata()], Partial = true, Attribution = Kartverket,
        }, HttpStatusCode.OK));

        Type(cut, "Storgata 1");

        cut.WaitForAssertion(() => Assert.Contains("Some results may be missing", cut.Markup), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void No_matches_offers_the_other_region_and_entering_by_hand()
    {
        var (cut, _) = Render(search: r => ApiResult<LocationSearchResponse>.Success(new LocationSearchResponse
        {
            Region = LocationSearchRegion.Norway, Candidates = [], Attribution = Kartverket,
        }, HttpStatusCode.OK));

        Type(cut, "Nowhere");

        cut.WaitForAssertion(() => Assert.Contains("No matches in Norway for “Nowhere”.", cut.Markup), TimeSpan.FromSeconds(3));
        Assert.Contains("Search worldwide", cut.Find(".loc-search-empty").TextContent);
        Assert.Contains("Enter by hand", cut.Find(".loc-search-empty").TextContent);
    }

    [Fact]
    public void A_candidate_row_renders_its_kind_as_a_word()
    {
        var (cut, _) = Render();

        Type(cut, "Storgata 1");

        cut.WaitForElement("li[role=option]", TimeSpan.FromSeconds(3));
        Assert.Equal("Address", cut.Find(".loc-result-kind").TextContent);
        Assert.Equal("0155 Oslo", cut.Find(".loc-result-desc").TextContent);
    }

    [Fact]
    public async Task Picking_a_candidate_fills_the_place_but_never_the_description()
    {
        var (cut, api) = Render();
        Type(cut, "Storgata 1");
        PickFirst(cut);

        Assert.Equal("Storgata 1", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Name").Instance.Value);
        Assert.Equal("Storgata 1, 0155 Oslo", cut.FindComponents<OdsField>().Single(f => f.Instance.Label == "Address").Instance.Value);
        Assert.Null(cut.FindComponent<OdsNoteField>().Instance.Value);

        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n =>
            n.Name == "Storgata 1"
            && n.Description == null
            && n.Latitude == 59.91273m && n.Longitude == 10.75027m
            && n.AltitudeMeters == 12
            && n.TimeZoneId == "Europe/Oslo"
            && n.Source == LocationSource.Kartverket
            && n.SourceReference == "0301-12345-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Changing_a_coordinate_saves_the_place_as_entered_by_hand()
    {
        var (cut, api) = Render();
        Type(cut, "Storgata 1");
        PickFirst(cut);

        var coords = cut.FindComponent<OdsCoordinateField>();
        await cut.InvokeAsync(() => coords.Instance.LatChanged.InvokeAsync(59.9m));

        Assert.Contains("will be saved as entered by hand", cut.Markup);
        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n =>
            n.Source == LocationSource.Manual && n.SourceReference == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Putting_the_providers_coordinates_back_restores_the_source()
    {
        var (cut, api) = Render();
        Type(cut, "Storgata 1");
        PickFirst(cut);
        var coords = cut.FindComponent<OdsCoordinateField>();
        await cut.InvokeAsync(() => coords.Instance.LatChanged.InvokeAsync(59.9m));

        Click(cut, "Use Kartverket’s coordinates");
        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n =>
            n.Source == LocationSource.Kartverket && n.Latitude == 59.91273m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task More_than_five_decimals_rounding_to_the_candidates_value_keeps_the_source()
    {
        var (cut, api) = Render();
        Type(cut, "Storgata 1");
        PickFirst(cut);
        var coords = cut.FindComponent<OdsCoordinateField>();
        await cut.InvokeAsync(() => coords.Instance.LatChanged.InvokeAsync(59.912731m));

        Assert.Contains("Saved as 59.91273, 10.75027", cut.Markup);
        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n =>
            n.Source == LocationSource.Kartverket && n.Latitude == 59.91273m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_missing_time_zone_is_asked_for_never_guessed()
    {
        var (cut, api) = Render(search: r => ApiResult<LocationSearchResponse>.Success(new LocationSearchResponse
        {
            Region = LocationSearchRegion.World,
            Candidates = [Storgata(timeZone: null) with { Source = LocationSource.OpenMeteo, SourceReference = "3421319" }],
            Attribution = OpenMeteo,
        }, HttpStatusCode.OK));
        Type(cut, "Nuuk");
        PickFirst(cut);

        Assert.Contains("Open-Meteo didn’t return a time zone Odyssey recognises.", cut.Markup);
        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        Assert.Contains("Choose the time zone this place keeps.", cut.Markup);
        api.Verify(a => a.CreateAsync(It.IsAny<NewLocation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_name_conflict_lands_on_the_name_field()
    {
        const string message = "An archived location with this name exists — restore it or choose another name.";
        var (cut, _) = Render(saveResult: ApiResult<ExistingLocation>.Failure(HttpStatusCode.Conflict, new ApiProblem
        {
            Status = 409,
            Errors = new Dictionary<string, string[]> { ["name"] = [message] },
        }));
        Type(cut, "Storgata 1");
        PickFirst(cut);

        Click(cut, "Create location");

        cut.WaitForAssertion(() => Assert.Equal(message, FieldError(cut, "Name")));
    }

    [Fact]
    public async Task Entering_by_hand_skips_the_search_and_sends_manual()
    {
        var (cut, api) = Render();
        Click(cut, "Enter by hand");

        await SetField(cut, "Name", "Mooring");
        var coords = cut.FindComponent<OdsCoordinateField>();
        await cut.InvokeAsync(() => coords.Instance.LatChanged.InvokeAsync(59.1m));
        await cut.InvokeAsync(() => coords.Instance.LngChanged.InvokeAsync(10.2m));
        var zone = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => zone.Instance.ValueChanged.InvokeAsync("Europe/Oslo"));
        Click(cut, "Create location");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.CreateAsync(It.Is<NewLocation>(n =>
            n.Source == LocationSource.Manual && n.SourceReference == null && n.Name == "Mooring"), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Editing_round_trips_the_archived_flag_and_keeps_the_source()
    {
        var cabin = Cabin(archived: true);
        var (cut, api) = Render(cabin);

        Assert.Contains("This location is archived. Saving keeps it archived", cut.Markup);
        await SetField(cut, "Name", "The cabin");
        Click(cut, "Save changes");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.UpdateAsync(cabin.LocationId, It.Is<UpdateLocation>(u =>
            u.Name == "The cabin"
            && u.Archived
            && u.Description == "Key under the mat"
            && u.Source == LocationSource.Kartverket
            && u.SourceReference == "3439-1-4"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_new_address_in_edit_replaces_the_place_but_not_its_name_or_description()
    {
        var cabin = Cabin();
        var (cut, api) = Render(cabin);

        Click(cut, "Search for a new address");
        Type(cut, "Storgata 1");
        PickFirst(cut);

        Assert.Contains("The name and description are unchanged.", cut.Markup);
        Click(cut, "Save changes");

        await cut.InvokeAsync(() => { });
        api.Verify(a => a.UpdateAsync(cabin.LocationId, It.Is<UpdateLocation>(u =>
            u.Name == "Cabin"
            && u.Description == "Key under the mat"
            && u.AddressText == "Storgata 1, 0155 Oslo"
            && u.Latitude == 59.91273m
            && u.SourceReference == "0301-12345-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Without_locations_create_the_edit_dialog_offers_no_search()
    {
        var (cut, _) = Render(Cabin(), canSearch: false);

        Assert.DoesNotContain("Search for a new address", cut.Markup);
    }

    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingLocation? Location { get; set; }

        [Parameter] public bool CanSearch { get; set; } = true;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<LocationDialog>(2);
            builder.AddComponentParameter(3, nameof(LocationDialog.Open), true);
            builder.AddComponentParameter(4, nameof(LocationDialog.Location), Location);
            builder.AddComponentParameter(5, nameof(LocationDialog.CanSearch), CanSearch);
            builder.CloseComponent();
        }
    }
}
