using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Layout;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Weather;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Weather points in the client (issue #388): the Weather points page, its dialogs, linking a location to a point,
/// a linked location's weather sections, the options cache, the bounded claims refresh and the quiet toast.
/// </summary>
[Collection(nameof(WeatherWidgetSeam))]
public class WeatherPointsUiTests : IDisposable
{
    private static readonly Guid OsloId = Guid.Parse("0510a000-0000-0000-0000-000000000001");
    private static readonly Guid BergenId = Guid.Parse("0510a000-0000-0000-0000-000000000002");
    private static readonly Guid StorgataId = Guid.Parse("0510a000-0000-0000-0000-000000000010");

    private static readonly string[] AdminClaims =
    [
        PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate,
        PermissionClaims.WeatherPointsDelete, PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate,
        PermissionClaims.SystemSettingsRead,
    ];

    public void Dispose()
    {
        WeatherPointsPage.InteractiveCheck = OperatingSystem.IsBrowser;
        LocationsPage.InteractiveCheck = OperatingSystem.IsBrowser;
    }

    // ── Navigation and the page gate (AC 1, 22) ───────────────────────────────────────────────────

    [Fact]
    public void The_nav_item_follows_locations_in_journal_and_needs_weather_points_read()
    {
        var journal = NavModel.All.Single(m => m.Key == "journal");
        var keys = journal.Groups.SelectMany(g => g.Items).Select(p => p.Key).ToList();
        Assert.Equal(keys.IndexOf("locations") + 1, keys.IndexOf("weather-points"));
        var item = journal.Groups.SelectMany(g => g.Items).Single(p => p.Key == "weather-points");
        Assert.Equal(("Weather points", "wb_sunny", "weather-points", PermissionClaims.WeatherPointsRead),
            (item.Label, item.Icon, item.Href, item.Claim));

        var page = typeof(WeatherPointsPage);
        Assert.Equal("/weather-points", ((RouteAttribute)Attribute.GetCustomAttribute(page, typeof(RouteAttribute))!).Template);
        Assert.Equal(PermissionClaims.WeatherPointsRead,
            ((Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(page, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!).Policy);
    }

    [Fact]
    public async Task The_page_shows_the_list_title_and_new_weather_point_to_an_admin()
    {
        var weather = Weather(Point("Oslo", OsloId), Point("Bergen", BergenId) with { ForecastEnabled = false, HistoryEnabled = false, LinkedLocationCount = null });
        await using var ctx = PageContext(weather, AdminClaims);

        var page = ctx.Render<WeatherPointsPage>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".odc-record").Count));
        Assert.Contains("<PageTitle>Weather points — Odyssey</PageTitle>",
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Weather", "WeatherPointsPage.razor")), StringComparison.Ordinal);
        Assert.Contains("New weather point", page.Markup);
        Assert.Contains("2 points · 1 of 5 on a schedule", page.Markup);
        Assert.Contains("Used by 2 locations", page.Markup);
        // A null count is omitted, never "0" (AC 23).
        Assert.DoesNotContain("No linked locations", page.Markup);
        // A Weather off row says so in words and is never dimmed (AC 28).
        Assert.Contains("Weather off", page.Markup);
        Assert.Empty(page.FindAll(".odc-record.dimmed"));
    }

    [Fact]
    public async Task Without_update_there_is_no_new_weather_point_and_without_locations_read_no_linked_sort()
    {
        var weather = Weather(Point("Oslo", OsloId));
        await using var ctx = PageContext(weather, [PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead]);

        var page = ctx.Render<WeatherPointsPage>();

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".odc-record")));
        Assert.DoesNotContain("New weather point", page.Markup);
        Assert.DoesNotContain(WeatherPointsPage.SortFieldsFor(false), f => f.Key == "linked");
        Assert.Contains(WeatherPointsPage.SortFieldsFor(true), f => f.Key == "linked");
    }

    [Fact]
    public async Task An_empty_list_offers_new_weather_point_and_a_failed_load_says_so()
    {
        var empty = Weather();
        await using (var ctx = PageContext(empty, AdminClaims))
        {
            var page = ctx.Render<WeatherPointsPage>();
            page.WaitForAssertion(() => Assert.Contains("No weather points yet", page.Markup));
            Assert.Contains("A weather point is a place weather is fetched for; locations link to it.", page.Markup);
        }

        var failing = new Mock<IWeatherApiClient>();
        failing.Setup(w => w.PointsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingWeatherPoint>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        await using (var ctx = PageContext(failing, AdminClaims))
        {
            var page = ctx.Render<WeatherPointsPage>();
            page.WaitForAssertion(() => Assert.Contains("Couldn't load weather points.", page.Markup));
            Assert.Contains("Weather points couldn’t be loaded. Your other data is unaffected.", page.Markup);
        }
    }

    [Fact]
    public async Task A_deep_link_outside_the_list_is_fetched_and_pinned_and_an_unknown_one_is_announced()
    {
        var hidden = Point("Tromsø", Guid.NewGuid());
        var weather = Weather(Point("Oslo", OsloId));
        weather.Setup(w => w.PointAsync(hidden.WeatherPointId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(hidden));
        await using (var ctx = PageContext(weather, AdminClaims))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"weather-points?open={hidden.WeatherPointId}");
            var page = ctx.Render<WeatherPointsPage>();
            page.WaitForAssertion(() => Assert.Contains("Showing one weather point", page.Markup));
            Assert.Contains("Tromsø", page.Markup);
            Assert.Equal("true", page.Find($"#{WeatherPointsPage.CardId(hidden.WeatherPointId)} button[aria-expanded]").GetAttribute("aria-expanded"));
        }

        var unknown = Weather(Point("Oslo", OsloId));
        unknown.Setup(w => w.PointAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.NotFound, new ApiProblem { Status = 404 }));
        await using (var ctx = PageContext(unknown, AdminClaims))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"weather-points?open={Guid.NewGuid()}");
            var page = ctx.Render<WeatherPointsPage>();
            page.WaitForAssertion(() => Assert.Contains("That weather point no longer exists.", page.Markup));
        }
    }

    [Fact]
    public async Task Expanding_a_point_loads_its_linked_locations_by_id_and_name_only()
    {
        var weather = Weather(Point("Oslo", OsloId));
        weather.Setup(w => w.LinkedLocationsAsync(OsloId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<WeatherPointLinkedLocation>
        {
            new() { LocationId = StorgataId, Name = "Storgata 1", DistanceKm = 0 },
            new() { LocationId = Guid.NewGuid(), Name = "Cabin", DistanceKm = 3 },
        }));
        await using var ctx = PageContext(weather, AdminClaims);
        var page = ctx.Render<WeatherPointsPage>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".odc-record")));
        weather.Verify(w => w.LinkedLocationsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        page.Find(".odc-record-trigger").Click();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".wx-linked > li").Count));
        Assert.Equal("Cabin", page.FindAll(".wx-linked-name")[0].TextContent);
        Assert.Equal($"/locations?open={StorgataId}", page.FindAll(".wx-linked-name")[1].GetAttribute("href"));
        Assert.Contains("less than 1 km", page.Markup);
    }

    // ── The create / settings dialog (AC 3–7, 30) ───────────────────────────────────────────────────

    [Fact]
    public async Task Creating_with_coordinates_catches_a_missing_field_and_sends_the_values()
    {
        var weather = Weather();
        NewWeatherPoint? sent = null;
        weather.Setup(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((NewWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(Point("Oslo", OsloId)));
        await using var ctx = PageContext(weather, [PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate]);
        (ExistingWeatherPoint Point, bool FromLocation)? created = null;
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Cap, 5).Add(x => x.TrackedCount, 3)
            .Add(x => x.OnCreated, EventCallback.Factory.Create<(ExistingWeatherPoint, bool)>(new object(), c => created = c))));

        // Without locations.read only Enter coordinates is offered.
        Assert.Empty(cut.FindComponents<OdsCardSelect>());
        Assert.Contains("4 of 5 scheduled points in use", cut.Markup);
        await Press(cut, "Create weather point");
        cut.WaitForAssertion(() => Assert.Contains("Enter both latitude and longitude.", cut.Markup));
        Assert.Contains("Choose the time zone.", cut.Markup);
        Assert.Contains("Enter a name for the weather point.", cut.Markup);
        weather.Verify(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()), Times.Never);

        await cut.InvokeAsync(() => cut.FindComponent<OdsTextInputField>().Instance.ValueChanged.InvokeAsync("Oslo"));
        var coordinates = cut.FindComponent<OdsCoordinateField>().Instance;
        await cut.InvokeAsync(() => coordinates.LatChanged.InvokeAsync(59.9139m));
        await cut.InvokeAsync(() => coordinates.LngChanged.InvokeAsync(10.7522m));
        var zone = cut.FindComponents<OdsCombobox>().Single(c => c.Instance.InputId!.StartsWith("wp-tz", StringComparison.Ordinal));
        await cut.InvokeAsync(() => zone.Instance.ValueChanged.InvokeAsync("Europe/Oslo"));
        await Press(cut, "Create weather point");

        cut.WaitForAssertion(() => Assert.NotNull(created));
        Assert.Equal(("Oslo", 59.9139m, 10.7522m, "Europe/Oslo", (Guid?)null), (sent!.Name, sent.Latitude, sent.Longitude, sent.TimeZoneId, sent.FromLocationId));
        Assert.True(sent.ForecastEnabled);
        Assert.False(created!.Value.FromLocation);
    }

    [Fact]
    public async Task Creating_from_a_location_lists_places_with_coordinates_and_sends_its_fingerprint()
    {
        var weather = Weather();
        NewWeatherPoint? sent = null;
        weather.Setup(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((NewWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(Point("Storgata 1", OsloId)));
        var place = Place() with { WeatherFingerprint = "v1:fp" };
        var locations = UnifiedLocationsTestKit.Locations(place);
        await using var ctx = PageContext(weather, AdminClaims, locations);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.CanReadLocations, true).Add(x => x.LocationDebounceMs, 0)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsCardSelect>()));
        Assert.Contains("Only places with coordinates are listed.", cut.Markup);
        locations.Verify(a => a.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // The help line warns that the name is seen by everyone who can see weather.
        Assert.Contains("Use a place name, not a street address.", cut.Markup);

        var select = cut.FindComponent<OdsLocationSelect>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(LocationPick.Existing(place.LocationId)));
        cut.WaitForAssertion(() => Assert.Contains("Copied from Storgata 1 now.", cut.Markup));
        Assert.Equal("Storgata 1", cut.FindComponent<OdsTextInputField>().Instance.Value);
        await Press(cut, "Create weather point");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal((place.LocationId, "v1:fp"), (sent!.FromLocationId!.Value, sent.ExpectedLocationFingerprint));
        Assert.Null(sent.Latitude);
        locations.Verify(a => a.SetWeatherPointAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task With_no_eligible_place_the_source_field_says_so()
    {
        var locations = UnifiedLocationsTestKit.Locations();
        await using var ctx = PageContext(Weather(), AdminClaims, locations);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true).Add(x => x.CanReadLocations, true)));

        cut.WaitForAssertion(() => Assert.Contains("No places have coordinates yet. Add them on the Locations page.", cut.Markup));
    }

    [Theory]
    [InlineData("duplicate-name", "Another weather point is already called Oslo.")]
    [InlineData("stale", "Someone else changed this weather point. Reload to see their changes.")]
    [InlineData("purge-in-progress", "Finish deleting first; nothing else can be saved until it completes.")]
    public async Task A_conflict_on_save_lands_where_it_belongs(string code, string expected)
    {
        var point = Point("Oslo", OsloId);
        var weather = Weather(point);
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherPointUpdateResult>.Failure(HttpStatusCode.Conflict, new ApiProblem { Status = 409, Code = code }));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Point, point).Add(x => x.OnContinueDeleting, () => { })));

        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Markup));
        if (code != "duplicate-name")
        {
            var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal));
            Assert.Equal("true", save.GetAttribute("aria-disabled"));
            Assert.Contains(code == "stale" ? "Reload" : "Continue deleting", cut.Markup);
        }

        // The user's input is kept.
        Assert.Equal("Oslo", cut.FindComponent<OdsTextInputField>().Instance.Value);
    }

    [Fact]
    public async Task A_place_moved_while_creating_warns_rereads_the_source_and_sends_the_new_fingerprint()
    {
        var weather = Weather();
        var fingerprints = new List<string?>();
        weather.Setup(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((NewWeatherPoint r, CancellationToken _) => fingerprints.Add(r.ExpectedLocationFingerprint))
            .ReturnsAsync(() => fingerprints.Count == 1
                ? ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.Conflict, new ApiProblem { Status = 409, Code = "location-changed" })
                : Ok(Point("Storgata 1", OsloId)));
        var place = Place() with { WeatherFingerprint = "v1:before" };
        var moved = Place() with { Latitude = 60.1m, Longitude = 11.2m, WeatherFingerprint = "v1:after" };
        var locations = UnifiedLocationsTestKit.Locations(place);
        // The place moves on the server between the pick and the first create.
        locations.Setup(a => a.GetAsync(StorgataId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Ok(fingerprints.Count == 0 ? place : moved));
        await using var ctx = PageContext(weather, AdminClaims, locations);
        var created = false;
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.CanReadLocations, true).Add(x => x.LocationDebounceMs, 0)
            .Add(x => x.OnCreated, EventCallback.Factory.Create<(ExistingWeatherPoint, bool)>(new object(), _ => created = true))));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsLocationSelect>()));
        await cut.InvokeAsync(() => cut.FindComponent<OdsLocationSelect>().Instance.ValueChanged.InvokeAsync(LocationPick.Existing(StorgataId)));
        cut.WaitForAssertion(() => Assert.Contains("59.913, 10.739", cut.Markup));

        await Press(cut, "Create weather point");

        // The warning alert sits above the position card, the card now shows where the place is, and nothing closed.
        const string warning = "The location’s position changed while this dialog was open. Check the position below, then create the weather point again.";
        cut.WaitForAssertion(() => Assert.Contains(warning, cut.Markup));
        Assert.Contains(warning, cut.FindComponents<OdsAlert>().Single(a => a.Instance.Severity == MudBlazor.Severity.Warning).Markup);
        Assert.Contains("60.100, 11.200", cut.Markup);
        Assert.DoesNotContain("59.913, 10.739", cut.Markup);
        Assert.False(created);

        // Re-reviewed: the next create carries the fingerprint of the position the user has now seen.
        await Press(cut, "Create weather point");
        cut.WaitForAssertion(() => Assert.True(created));
        Assert.Equal(["v1:before", "v1:after"], fingerprints);
    }

    [Fact]
    public async Task Moving_a_point_shows_old_and_new_with_changed_in_words()
    {
        var point = Point("Oslo", OsloId);
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true).Add(x => x.Point, point)));

        Assert.DoesNotContain("After saving", cut.Markup);
        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LatChanged.InvokeAsync(60.1m));

        cut.WaitForAssertion(() => Assert.Contains("After saving", cut.Markup));
        Assert.Contains("Weather uses now", cut.Markup);
        Assert.Single(cut.FindAll(".wx-changed"));
        Assert.Contains("2 linked locations use the new position too", cut.Markup);
    }

    [Fact]
    public void The_move_advisory_counts_the_places_or_says_its()
    {
        Assert.Equal("2 linked locations now use the new position.",
            WeatherVisuals.AdvisoryText(new WeatherAdvisory { Code = WeatherAdvisoryCode.LinkedLocationsFollowMove, Count = 2 }, "Oslo"));
        Assert.Equal("Its linked locations now use the new position.",
            WeatherVisuals.AdvisoryText(new WeatherAdvisory { Code = WeatherAdvisoryCode.LinkedLocationsFollowMove }, "Oslo"));
    }

    // ── The delete dialog (AC 8, 23, 31) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_typed_confirmation_is_labelled_described_and_aria_disabled_until_it_matches()
    {
        var point = Point("Oslo", OsloId) with { ForecastEnabled = false, HistoryEnabled = false };
        var weather = Weather(point);
        weather.Setup(w => w.DeletePointAsync(OsloId, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        await using var ctx = PageContext(weather, AdminClaims);
        var done = false;
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointPurgeDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Point, point).Add(x => x.Mode, WeatherPurgeMode.Full).Add(x => x.LinkedLocationCount, 2)
            .Add(x => x.OnDone, () => done = true)));

        var field = cut.Find("input.odc-input");
        Assert.Contains("Type Oslo to confirm", cut.Find($"label[for='{field.Id}']").TextContent);
        var effects = cut.Find("ul.wx-effects");
        Assert.Contains(effects.Id!, field.GetAttribute("aria-describedby")!, StringComparison.Ordinal);
        Assert.Contains("2 locations use this point and will be unlinked.", cut.Markup);
        var danger = DangerButton(cut);
        Assert.Equal("true", danger.GetAttribute("aria-disabled"));
        Assert.Null(danger.GetAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Find(".wx-confirm").Click());
        cut.WaitForAssertion(() => Assert.Contains("Type Oslo exactly to confirm.", cut.Markup));
        weather.Verify(w => w.DeletePointAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        await cut.InvokeAsync(() => cut.FindComponent<OdsTextInputField>().Instance.ValueChanged.InvokeAsync("Oslo"));
        Assert.Null(DangerButton(cut).GetAttribute("aria-disabled"));
        await cut.InvokeAsync(() => cut.Find(".wx-confirm").Click());

        cut.WaitForAssertion(() => Assert.True(done));
    }

    [Theory]
    [InlineData(null, "Locations that use this point will be unlinked.")]
    [InlineData(0, null)]
    [InlineData(1, "1 location uses this point and will be unlinked.")]
    public async Task A_null_count_is_never_zero(int? count, string? expected)
    {
        var point = Point("Oslo", OsloId) with { ForecastEnabled = false, HistoryEnabled = false };
        await using var ctx = PageContext(Weather(point), AdminClaims);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointPurgeDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Point, point).Add(x => x.Mode, WeatherPurgeMode.Full).Add(x => x.LinkedLocationCount, count)));

        if (expected is null)
            Assert.DoesNotContain("unlinked", cut.Markup);
        else
            Assert.Contains(expected, cut.Markup);
        Assert.DoesNotContain("0 locations", cut.Markup);
    }

    // ── Linking a location (AC 9–13) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_link_picker_lists_points_nearest_first_in_words_and_preselects_the_current_one()
    {
        var weather = Weather();
        weather.Setup(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions
        {
            Points =
            [
                new() { WeatherPointId = OsloId, Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true, DistanceKm = 0 },
                new() { WeatherPointId = BergenId, Name = "Bygdøy", ForecastEnabled = false, HistoryEnabled = true, DistanceKm = 4 },
                new() { WeatherPointId = Guid.NewGuid(), Name = "Bergen", ForecastEnabled = false, HistoryEnabled = false, DistanceKm = null },
            ],
        }));
        await using var ctx = PageContext(weather, AdminClaims);

        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointLinkDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Location, Linked(Place(), OsloId))));

        var picker = cut.FindComponent<OdsCombobox>();
        cut.WaitForAssertion(() => Assert.Equal(3, picker.Instance.Options.Count));
        Assert.Equal(OsloId.ToString("D"), picker.Instance.Value);
        Assert.Equal(["less than 1 km away", "4 km away · history only", "distance unknown · weather off"], picker.Instance.Options.Select(o => o.Sub));
        Assert.Equal("linked now", picker.Instance.Options[0].Note);
        Assert.Equal("Oslo, less than 1 km away, linked now", picker.Instance.Options[0].AriaLabel);
        Assert.Contains("Change weather point", cut.Markup);
        weather.Verify(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_point_gone_while_linking_errs_on_the_picker_keeps_the_choice_and_reloads()
    {
        var weather = Weather();
        weather.Setup(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions
        {
            Points = [new() { WeatherPointId = OsloId, Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true, DistanceKm = 2 }],
        }));
        var locations = UnifiedLocationsTestKit.Locations(Place());
        locations.Setup(a => a.SetWeatherPointAsync(StorgataId, OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationWeatherPointLink>.Failure(HttpStatusCode.UnprocessableEntity, UnifiedLocationsTestKit.Problem(422, "weatherPointId")));
        await using var ctx = PageContext(weather, AdminClaims, locations);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointLinkDialog>(d => d.Add(x => x.Open, true).Add(x => x.Location, Place())));
        var picker = cut.FindComponent<OdsCombobox>();
        cut.WaitForAssertion(() => Assert.Single(picker.Instance.Options));
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(OsloId.ToString("D")));

        await Press(cut, "Link");

        cut.WaitForAssertion(() => Assert.Contains("That weather point is no longer available. Choose another one.", cut.Markup));
        Assert.True(cut.FindComponent<OdsCombobox>().Instance.Error);
        Assert.Equal(OsloId.ToString("D"), cut.FindComponent<OdsCombobox>().Instance.Value);
        weather.Verify(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_place_merged_away_while_linking_closes_the_dialog_for_the_page_to_warn()
    {
        var weather = Weather();
        weather.Setup(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions
        {
            Points = [new() { WeatherPointId = OsloId, Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true }],
        }));
        var locations = UnifiedLocationsTestKit.Locations(Place());
        locations.Setup(a => a.SetWeatherPointAsync(StorgataId, OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationWeatherPointLink>.Failure(HttpStatusCode.NotFound, UnifiedLocationsTestKit.Problem(404)));
        await using var ctx = PageContext(weather, AdminClaims, locations);
        var gone = false;
        var open = true;
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointLinkDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Location, Linked(Place(), OsloId))
            .Add(x => x.OpenChanged, v => open = v).Add(x => x.OnGone, () => gone = true)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponent<OdsCombobox>().Instance.Options));

        await Press(cut, "Link");

        cut.WaitForAssertion(() => Assert.True(gone));
        Assert.False(open);
    }

    [Fact]
    public async Task With_no_points_an_admin_is_offered_to_create_one_and_link_stays_aria_disabled()
    {
        var weather = Weather();
        weather.Setup(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions()));
        await using var ctx = PageContext(weather, AdminClaims);

        var admin = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointLinkDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Location, Place()).Add(x => x.CanCreatePoints, true)));
        admin.WaitForAssertion(() => Assert.Contains("There are no weather points yet.", admin.Markup));
        Assert.Contains("Create a weather point", admin.Markup);
        Assert.Equal("true", admin.FindAll("button").Single(b => b.TextContent.Contains("Link", StringComparison.Ordinal)).GetAttribute("aria-disabled"));

        await using var other = PageContext(weather, AdminClaims);
        var user = other.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointLinkDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Location, Place())));
        user.WaitForAssertion(() => Assert.Contains("An administrator creates weather points", user.Markup));
        Assert.DoesNotContain("Create a weather point", user.Markup);
    }

    [Fact]
    public void A_link_is_one_announcement_with_its_advisories()
    {
        var link = new LocationWeatherPointLink
        {
            LocationId = StorgataId,
            WeatherPoint = new WeatherPointRef { WeatherPointId = OsloId, Name = "Oslo" },
            DistanceKm = 2,
            Warnings = [new WeatherAdvisory { Code = WeatherAdvisoryCode.TimeZoneMismatch }],
        };

        Assert.Equal(
            "Storgata 1 now uses weather from Oslo (2 km). Oslo uses a different time zone from this place, so a journal day may differ by one.",
            LocationsPage.LinkAnnouncement("Storgata 1", link));
        Assert.Equal("Storgata 1 now uses weather from Oslo.", LocationsPage.LinkAnnouncement("Storgata 1", link with { DistanceKm = null, Warnings = [] }));
    }

    // ── A linked location's sections (AC 11, 14, 22) ───────────────────────────────────────────────

    [Theory]
    [InlineData(true, "No weather point. Link one to see forecasts and weather history for this place.")]
    [InlineData(false, "No weather point is linked to this place.")]
    public async Task An_unlinked_place_says_so_by_whether_the_caller_can_link(bool canLink, string expected)
    {
        await using var ctx = PageContext(Weather(), AdminClaims);

        var section = ctx.Render<LocationWeatherPointSection>(p => p.Add(s => s.Location, Place()).Add(s => s.CanLink, canLink));

        Assert.Contains(expected, section.Markup);
        Assert.Empty(section.FindAll("button"));
    }

    [Fact]
    public async Task A_linked_place_shows_the_point_and_distance_and_the_open_link_only_to_a_points_reader()
    {
        await using var ctx = PageContext(Weather(), AdminClaims);
        var place = Linked(Place(), OsloId);

        var admin = ctx.Render<LocationWeatherPointSection>(p => p.Add(s => s.Location, place).Add(s => s.CanOpenPoints, true));
        Assert.Contains("forecast and history", admin.Markup);
        Assert.Contains("2 km", admin.Markup);
        Assert.Equal($"/weather-points?open={OsloId}", admin.Find(".wx-open-link").GetAttribute("href"));
        Assert.Equal("Open Oslo in Weather points", admin.Find(".wx-open-link").TextContent);

        var user = ctx.Render<LocationWeatherPointSection>(p => p.Add(s => s.Location, place with { WeatherPointDistanceKm = null }));
        Assert.Empty(user.FindAll(".wx-open-link"));
        Assert.Contains("Distance unknown", user.Markup);
        Assert.Contains("this place has no coordinates", user.Markup);
    }

    [Fact]
    public async Task Advisories_render_as_info_without_aria_invalid_and_the_forecast_off_one_is_not_repeated()
    {
        await using var ctx = PageContext(Weather(), AdminClaims);
        var place = Linked(Place(), OsloId);
        place.WeatherPoint = place.WeatherPoint! with { ForecastEnabled = false };

        var section = ctx.Render<LocationWeatherPointSection>(p => p.Add(s => s.Location, place).Add(s => s.Advisories,
        [
            new WeatherAdvisory { Code = WeatherAdvisoryCode.NoLocationCoordinates },
            new WeatherAdvisory { Code = WeatherAdvisoryCode.PointNotSelectable },
        ]));

        Assert.Contains("This place has no coordinates, so its distance to Oslo is unknown.", section.Markup);
        Assert.Contains("Oslo has no forecast turned on, so only stored history shows here.", section.Markup);
        Assert.DoesNotContain("only stored history shows for this place", section.Markup);
        Assert.Empty(section.FindAll("[aria-invalid]"));
    }

    [Fact]
    public async Task The_flags_alone_decide_which_sections_draw_without_an_options_read()
    {
        var weather = Weather();
        weather.Setup(w => w.CurrentAsync(OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherCurrentResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        await using var ctx = PageContext(weather, AdminClaims);
        var reads = new WeatherPointReads(weather.Object);

        IRenderedComponent<LocationWeatherSections> Render(LinkedWeatherPointRef? point) =>
            ctx.Render<LocationWeatherSections>(p => p.Add(s => s.Location, Place() with { WeatherPoint = point, WeatherPointDistanceKm = 2 }).Add(s => s.Reads, reads));

        var both = Render(Ref() with { HasStoredHistory = true });
        // A failed forecast leaves the history section working (AC 14).
        both.WaitForAssertion(() => Assert.Contains("The forecast couldn’t be loaded", both.Markup));
        Assert.Single(both.FindComponents<WeatherHistorySection>());
        Assert.Equal(2, both.FindComponents<OdsRecordSection>().Count(s => s.Instance.Meta == "from Oslo · 2 km"));

        var historyOnly = Render(Ref() with { ForecastEnabled = false, HasStoredHistory = true });
        Assert.Empty(historyOnly.FindComponents<LocationWeatherForecastSection>());
        Assert.Single(historyOnly.FindComponents<WeatherHistorySection>());

        var nothing = Render(Ref() with { ForecastEnabled = false, HistoryEnabled = false, HasStoredHistory = false });
        Assert.Empty(nothing.FindComponents<WeatherHistorySection>());

        var purging = Render(Ref() with { PurgeInProgress = true });
        Assert.Contains("Weather for Oslo is being deleted.", purging.Markup);
        Assert.Empty(purging.FindComponents<LocationWeatherForecastSection>());
        Assert.Empty(purging.FindComponents<WeatherHistorySection>());

        var unlinked = Render(null);
        Assert.Empty(unlinked.FindComponents<LocationWeatherForecastSection>());
        Assert.Empty(unlinked.FindComponents<WeatherHistorySection>());

        weather.Verify(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Two_records_on_one_point_share_one_current_and_one_daily_read()
    {
        var weather = Weather();
        var reads = new WeatherPointReads(weather.Object);

        await Task.WhenAll(reads.CurrentAsync(OsloId), reads.CurrentAsync(OsloId), reads.DailyAsync(OsloId), reads.DailyAsync(OsloId));
        await reads.CurrentAsync(BergenId);

        weather.Verify(w => w.CurrentAsync(OsloId, It.IsAny<CancellationToken>()), Times.Once);
        weather.Verify(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), OsloId, It.IsAny<CancellationToken>()), Times.Once);
        weather.Verify(w => w.CurrentAsync(BergenId, It.IsAny<CancellationToken>()), Times.Once);

        reads.Forget(OsloId);
        await reads.CurrentAsync(OsloId);
        weather.Verify(w => w.CurrentAsync(OsloId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── The options cache (AC 25) and the claims refresh (AC 33) ────────────────────────────────────

    [Fact]
    public async Task The_options_cache_is_shared_and_emptied_by_any_authentication_change()
    {
        var weather = Weather();
        var auth = new Switchable();
        var cache = new WeatherPointOptionsCache(weather.Object, auth);

        await cache.GetAsync();
        await cache.GetAsync();
        weather.Verify(w => w.OptionsAsync(null, It.IsAny<CancellationToken>()), Times.Once);

        auth.Announce(signedIn: false);
        auth.Announce(signedIn: true, user: "another");
        await cache.GetAsync();
        weather.Verify(w => w.OptionsAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(2));

        cache.Invalidate();
        await cache.GetAsync();
        weather.Verify(w => w.OptionsAsync(null, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Five_parallel_403s_cause_exactly_one_refresh_and_repeats_none()
    {
        var auth = new Switchable();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>();
        var refresher = new WeatherClaimsRefresher(auth, () => { Interlocked.Increment(ref calls); return gate.Task; });

        var parallel = Enumerable.Range(0, 5).Select(_ => refresher.OnResponseAsync(HttpStatusCode.Forbidden)).ToList();
        gate.SetResult(true);
        // The refresh's own announcement keeps the same signed-in user: it must not reset the flag.
        auth.Announce(signedIn: true);
        await Task.WhenAll(parallel);

        Assert.Equal(1, calls);
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.OK));
        Assert.Equal(1, calls);

        // A sign-out and sign-in starts a new session, which may refresh once more.
        auth.Announce(signedIn: false);
        auth.Announce(signedIn: true, user: "another");
        await refresher.OnResponseAsync(HttpStatusCode.Forbidden);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_refresh_that_changes_nothing_still_counts_and_a_non_403_never_refreshes()
    {
        var auth = new Switchable();
        var calls = 0;
        var refresher = new WeatherClaimsRefresher(auth, () => { calls++; return Task.FromResult(false); });

        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.NotFound));
        Assert.Equal(0, calls);
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.Equal(1, calls);
    }

    // ── The quiet toast (AC 35) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_quiet_toast_is_not_a_live_region_and_has_a_close_button()
    {
        await using var ctx = new BunitContext();
        var quiet = ctx.Render<OdsToast>(p => p.Add(t => t.Message, LocationsPage.GoneMessage).Add(t => t.Severity, OdsToastSeverity.Warning)
            .Add(t => t.Live, false).Add(t => t.OnClose, () => { }));
        var toast = quiet.Find(".odc-toast");
        Assert.Null(toast.GetAttribute("role"));
        Assert.Equal("off", toast.GetAttribute("aria-live"));
        Assert.NotNull(quiet.Find("button.odc-toast-close[aria-label='Dismiss']"));
        Assert.Contains("This place was merged or removed. Reload Locations.", toast.TextContent);

        var live = ctx.Render<OdsToast>(p => p.Add(t => t.Message, "Saved."));
        Assert.Equal("status", live.Find(".odc-toast").GetAttribute("role"));
    }

    // ── Focus (AC 3, 26, 29; §6.4) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Focus_lands_on_the_new_row_after_create()
    {
        var created = Point("Oslo", OsloId);
        var weather = Weather(Point("Bergen", BergenId), created);
        await using var ctx = PageContext(weather, AdminClaims);
        var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointsPage>());
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".odc-record").Count));
        await page.InvokeAsync(() => page.FindAll("button").First(b => b.TextContent.Contains("New weather point", StringComparison.Ordinal)).Click());
        var dialog = page.FindComponent<WeatherPointDialog>();

        await page.InvokeAsync(() => dialog.Instance.OnCreated.InvokeAsync((created, false)));

        page.WaitForAssertion(() => Assert.Equal([WeatherPointsPage.CardId(OsloId)], LastFocusChain(ctx)));
        Assert.Equal("true", page.Find($"#{WeatherPointsPage.CardId(OsloId)} button[aria-expanded]").GetAttribute("aria-expanded"));
        // The new row's Linked locations section loads as it mounts; its loading and empty lines must not replace this.
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".wx-section"),
            e => e.TextContent.Contains("No location uses this weather point yet", StringComparison.Ordinal)));
        Assert.Equal("Weather point Oslo created.", PageAnnouncement(page));
    }

    [Fact]
    public async Task A_deep_link_focuses_the_record_header_and_an_unknown_one_the_h1()
    {
        await using (var ctx = PageContext(Weather(Point("Bergen", BergenId), Point("Oslo", OsloId)), AdminClaims))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"weather-points?open={OsloId}");
            var page = ctx.Render<WeatherPointsPage>();

            page.WaitForAssertion(() => Assert.Equal([WeatherPointsPage.CardId(OsloId)], LastFocusChain(ctx)));
            Assert.Equal("true", page.Find($"#{WeatherPointsPage.CardId(OsloId)} button[aria-expanded]").GetAttribute("aria-expanded"));
            Assert.DoesNotContain("Showing one weather point", page.Markup);
        }

        var unknown = Weather(Point("Oslo", OsloId));
        unknown.Setup(w => w.PointAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.NotFound, new ApiProblem { Status = 404 }));
        await using (var ctx = PageContext(unknown, AdminClaims))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"weather-points?open={Guid.NewGuid()}");
            var page = ctx.Render<WeatherPointsPage>();

            // An empty chain is the page h1.
            page.WaitForAssertion(() => Assert.Equal([], LastFocusChain(ctx)));
            Assert.Contains("That weather point no longer exists.", page.Markup);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(0, null)]
    public async Task Deleting_a_point_focuses_the_next_rows_menu_else_the_previous_else_the_h1(int deleted, int? focused)
    {
        var rows = focused is null
            ? new[] { Point("Oslo", OsloId) with { ForecastEnabled = false, HistoryEnabled = false } }
            : [Point("Oslo", OsloId) with { ForecastEnabled = false, HistoryEnabled = false }, Point("Bergen", BergenId) with { ForecastEnabled = false, HistoryEnabled = false }];
        var weather = Weather(rows);
        weather.Setup(w => w.DeletePointAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        await using var ctx = PageContext(weather, AdminClaims);
        var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointsPage>());
        page.WaitForAssertion(() => Assert.Equal(rows.Length, page.FindAll(".odc-record").Count));
        var target = rows[deleted];

        await page.InvokeAsync(() => MenuItem(page, $"Delete weather point {target.Name}").OnClick.InvokeAsync());
        await page.InvokeAsync(() => ConfirmField(page, target.Name).ValueChanged.InvokeAsync(target.Name));
        await page.InvokeAsync(() => page.Find(".wx-confirm").Click());

        var expected = focused is { } index ? new[] { WeatherPointsPage.MenuId(rows[index].WeatherPointId) } : [];
        page.WaitForAssertion(() => Assert.Equal(expected, LastFocusChain(ctx)));
        Assert.Contains($"Weather point {target.Name} deleted.", page.Markup);
    }

    [Fact]
    public async Task Deleting_history_returns_focus_to_the_points_own_menu()
    {
        var point = Point("Oslo", OsloId) with { HistoryEnabled = false };
        var weather = Weather(point, Point("Bergen", BergenId));
        weather.Setup(w => w.PurgePointHistoryAsync(OsloId, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        await using var ctx = PageContext(weather, AdminClaims);
        var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointsPage>());
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".odc-record").Count));

        await page.InvokeAsync(() => MenuItem(page, "Delete weather history of Oslo").OnClick.InvokeAsync());
        await page.InvokeAsync(() => ConfirmField(page, "Oslo").ValueChanged.InvokeAsync("Oslo"));
        await page.InvokeAsync(() => page.Find(".wx-confirm").Click());

        page.WaitForAssertion(() => Assert.Equal([WeatherPointsPage.MenuId(OsloId)], LastFocusChain(ctx)));
    }

    // ── The Locations page: link, unlink and its deep link (AC 13, 26, 29) ─────────────────────────────

    [Fact]
    public async Task Unlinking_confirms_sends_null_announces_and_returns_focus_to_the_rows_menu()
    {
        var place = Linked(Place(), OsloId);
        var locations = LocationsFor(place);
        Guid? sent = OsloId;
        locations.Setup(a => a.SetWeatherPointAsync(StorgataId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid? id, CancellationToken _) => sent = id)
            .ReturnsAsync(Ok(new LocationWeatherPointLink { LocationId = StorgataId }));
        await using var ctx = LocationsPageContext(locations);
        var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationsPage>());
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".odc-record")));

        await page.InvokeAsync(() => MenuItem(page, "Unlink weather point from Storgata 1").OnClick.InvokeAsync());

        var dialog = page.FindComponent<WeatherPointUnlinkDialog>();
        Assert.Contains("Unlink weather point?", page.Markup);
        Assert.Contains("Storgata 1 will stop showing weather from Oslo. Nothing is deleted", page.Markup);
        Assert.Equal(LocationsPage.MenuId(StorgataId), dialog.FindComponent<OdsModal>().Instance.ReturnFocusToId);
        locations.Verify(a => a.SetWeatherPointAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        await page.InvokeAsync(() => page.FindAll("button").Last(b => b.TextContent.Trim().EndsWith("Unlink", StringComparison.Ordinal)).Click());

        page.WaitForAssertion(() => Assert.Contains("Weather point unlinked from Storgata 1.", page.Markup));
        Assert.Null(sent);
        locations.Verify(a => a.SetWeatherPointAsync(StorgataId, null, It.IsAny<CancellationToken>()), Times.Once);
        page.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i =>
            i.Identifier == "odsRestoreFocusOwner" && i.Arguments.Count == 1 && Equals(i.Arguments[0], LocationsPage.MenuId(StorgataId))));
    }

    [Fact]
    public async Task Closing_the_link_dialog_returns_focus_to_the_rows_menu()
    {
        var weather = Weather();
        weather.Setup(w => w.OptionsAsync(StorgataId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions
        {
            Points = [new() { WeatherPointId = OsloId, Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true, DistanceKm = 2 }],
        }));
        // No coordinates, so the record a link expands draws no map: bUnit has no WebGL, and the map's failure
        // would speak through the same page region.
        var locations = LocationsFor(Place() with { Latitude = null, Longitude = null, HasCoordinates = false });
        locations.Setup(a => a.SetWeatherPointAsync(StorgataId, OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new LocationWeatherPointLink
            {
                LocationId = StorgataId,
                WeatherPoint = new WeatherPointRef { WeatherPointId = OsloId, Name = "Oslo" },
                DistanceKm = 2,
            }));
        await using var ctx = LocationsPageContext(locations, weather);
        var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationsPage>());
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".odc-record")));

        await page.InvokeAsync(() => MenuItem(page, "Link a weather point to Storgata 1").OnClick.InvokeAsync());
        var dialog = page.FindComponent<WeatherPointLinkDialog>();
        Assert.Equal(LocationsPage.MenuId(StorgataId), dialog.FindComponent<OdsModal>().Instance.ReturnFocusToId);
        var picker = page.FindComponent<OdsCombobox>();
        page.WaitForAssertion(() => Assert.Single(picker.Instance.Options));
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(OsloId.ToString("D")));

        await page.InvokeAsync(() => page.FindAll("button").Last(b => b.TextContent.Trim().EndsWith("Link", StringComparison.Ordinal)).Click());

        page.WaitForAssertion(() => Assert.Equal("Storgata 1 now uses weather from Oslo (2 km).",
            page.FindComponent<LocationsPage>().FindComponents<OdsLiveAnnouncer>()[0].Instance.Message));
        page.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i =>
            i.Identifier == "odsRestoreFocusOwner" && i.Arguments.Count == 1 && Equals(i.Arguments[0], LocationsPage.MenuId(StorgataId))));
    }

    [Fact]
    public async Task A_locations_deep_link_focuses_the_record_header_and_an_unknown_one_the_h1()
    {
        await using (var ctx = LocationsPageContext(LocationsFor(Place())))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"locations?open={StorgataId}");
            var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationsPage>());

            page.WaitForAssertion(() => Assert.Equal([$"loc-{StorgataId}"], LastFocusChain(ctx)));
            Assert.Equal("true", page.Find($"#loc-{StorgataId} button[aria-expanded]").GetAttribute("aria-expanded"));
        }

        await using (var ctx = LocationsPageContext(LocationsFor(Place())))
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"locations?open={Guid.NewGuid()}");
            var page = ctx.Render<ProvidersHost>(p => p.AddChildContent<LocationsPage>());

            page.WaitForAssertion(() => Assert.Equal([], LastFocusChain(ctx)));
            Assert.Contains("That place no longer exists.", page.Markup);
        }
    }

    // ── Source lints (AC 19, 24) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_markup_string_renders_a_weather_surface()
    {
        var files = Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "Pages", "Weather"), "*.*")
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count >= 20, $"The lint's scan set shrank to {files.Count} files.");
        var offenders = files.Where(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("MarkupString", StringComparison.Ordinal) || text.Contains("innerHTML", StringComparison.Ordinal);
        }).ToList();
        Assert.True(offenders.Count == 0, $"MarkupString or innerHTML in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void No_old_weather_claim_survives_in_the_client()
    {
        var offenders = Directory.EnumerateFiles(ClientSource.Root, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return new[] { "weather.locations.read", "weather.locations.update", "weather.history.delete", "WeatherLocationsRead", "WeatherLocationsUpdate", "WeatherHistoryDelete" }
                    .Any(old => text.Contains(old, StringComparison.Ordinal));
            })
            .ToList();
        Assert.Empty(offenders);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static ExistingWeatherPoint Point(string name, Guid id) => new()
    {
        WeatherPointId = id,
        Name = name,
        SettingsVersion = Guid.NewGuid(),
        ForecastEnabled = true,
        HistoryEnabled = true,
        IsTracked = true,
        HistoryFrom = new DateOnly(2025, 1, 1),
        LinkedLocationCount = 2,
        Site = new WeatherPointSiteDto { Version = 1, Latitude = 59.914m, Longitude = 10.752m, TimeZoneId = "Europe/Oslo", AppliedAt = new DateTime(2026, 3, 2) },
        EnabledAt = new DateTime(2026, 3, 2),
    };

    private static ExistingLocation Place() => new()
    {
        LocationId = StorgataId,
        Name = "Storgata 1",
        Latitude = 59.9133m,
        Longitude = 10.7389m,
        HasCoordinates = true,
        TimeZoneId = "Europe/Oslo",
    };

    private static LinkedWeatherPointRef Ref() => new() { WeatherPointId = OsloId, Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true };

    private static ExistingLocation Linked(ExistingLocation l, Guid pointId) => l with
    {
        WeatherPoint = Ref() with { WeatherPointId = pointId },
        WeatherPointDistanceKm = 2,
    };

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private static Mock<IWeatherApiClient> Weather(params ExistingWeatherPoint[] points)
    {
        var weather = new Mock<IWeatherApiClient>();
        weather.Setup(w => w.PointsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PagedResult<ExistingWeatherPoint> { Items = [.. points], TotalCount = points.Length }));
        weather.Setup(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions()));
        weather.Setup(w => w.CurrentAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherCurrentResponse { Status = WeatherStatus.NoData }));
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherDailyResponse { History = new WeatherHistoryBounds() }));
        weather.Setup(w => w.LinkedLocationsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<WeatherPointLinkedLocation>()));
        return weather;
    }

    private static BunitContext PageContext(Mock<IWeatherApiClient> weather, IReadOnlyList<string> claims, Mock<ILocationsApiClient>? locations = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var auth = new SignedInWith(claims);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(auth);
        ctx.Services.AddSingleton(weather.Object);
        ctx.Services.AddSingleton((locations ?? UnifiedLocationsTestKit.Locations()).Object);
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new SystemSettingsDto { WeatherMaxTrackedPoints = 5 }));
        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton<IWeatherPointOptionsCache>(new WeatherPointOptionsCache(weather.Object, auth));
        ctx.Services.AddSingleton<IWeatherClaimsRefresher>(new WeatherClaimsRefresher(auth, () => Task.FromResult(false)));
        WeatherPointsPage.InteractiveCheck = static () => true;
        return ctx;
    }

    /// <summary>The ids the page last asked <c>odsFocusChainOrHeading</c> to try; an empty chain is the page h1.</summary>
    private static string[] LastFocusChain(BunitContext ctx) =>
        ctx.JSInterop.Invocations.LastOrDefault(i => i.Identifier == "odsFocusChainOrHeading").Arguments?[0] as string[] ?? ["(no focus call)"];

    /// <summary>A row-menu item, by the accessible name that says which record it acts on.</summary>
    /// <summary>What the page's own polite region says: the first announcer in its tree, ahead of every section's and dialog's.</summary>
    private static string? PageAnnouncement(IRenderedComponent<ProvidersHost> page) =>
        page.FindComponent<WeatherPointsPage>().FindComponents<OdsLiveAnnouncer>()[0].Instance.Message;

    /// <summary>The delete dialog's typed confirmation — not the page's search field, which is a text input too.</summary>
    private static OdsTextInputField ConfirmField(IRenderedComponent<ProvidersHost> page, string name) =>
        page.FindComponents<OdsTextInputField>().Single(f => f.Instance.Label == $"Type {name} to confirm").Instance;

    private static OdsMenuItem MenuItem<TPage>(IRenderedComponent<TPage> page, string ariaLabel) where TPage : class, IComponent =>
        page.FindComponents<OdsMenu>().SelectMany(m => m.Instance.Items).Single(i => i.AriaLabel == ariaLabel);

    private static Mock<ILocationsApiClient> LocationsFor(params ExistingLocation[] places)
    {
        var locations = UnifiedLocationsTestKit.Locations(places);
        locations.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(places.ToList()));
        return locations;
    }

    /// <summary>The Locations page as a User holding the link claims (<c>locations.update</c> + <c>weather.read</c>).</summary>
    private static BunitContext LocationsPageContext(Mock<ILocationsApiClient> locations, Mock<IWeatherApiClient>? weather = null)
    {
        var ctx = PageContext(weather ?? Weather(), [PermissionClaims.LocationsRead, PermissionClaims.LocationsUpdate, PermissionClaims.WeatherRead], locations);
        ctx.Services.AddSingleton(new Mock<IClipboardService>().Object);
        ctx.Services.AddSingleton(new Mock<IMapStylesCache>().Object);
        ctx.Services.AddSingleton(Mock.Of<Odyssey.Client.Theme.IUserPreferenceService>(p => p.Current == new Odyssey.Client.Theme.UserPreferencesPage(true, "NOK", "NOK")));
        LocationsPage.InteractiveCheck = static () => true;
        return ctx;
    }

    private static async Task Press(IRenderedComponent<ProvidersHost> cut, string label)
    {
        var button = cut.FindAll("button").Last(b => b.TextContent.Contains(label, StringComparison.Ordinal));
        await cut.InvokeAsync(() => button.Click());
    }

    private static AngleSharp.Dom.IElement DangerButton(IRenderedComponent<ProvidersHost> cut) =>
        cut.Find(".wx-confirm button");

    /// <summary>An authentication provider the test can announce a sign-in or sign-out on.</summary>
    private sealed class Switchable : AuthenticationStateProvider
    {
        private AuthenticationState state = For(true, "first");

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state);

        public void Announce(bool signedIn, string user = "first")
        {
            state = For(signedIn, user);
            NotifyAuthenticationStateChanged(Task.FromResult(state));
        }

        private static AuthenticationState For(bool signedIn, string user) => new(signedIn
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"))
            : new ClaimsPrincipal(new ClaimsIdentity()));
    }
}
