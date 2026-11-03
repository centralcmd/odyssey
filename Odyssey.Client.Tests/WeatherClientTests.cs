using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Weather in the client (issue #348; design system · Weather.jsx, weather-data.js): the symbol words, the
/// units, the Admin menu by claim and state, and the dashboard card's states.
/// </summary>
[Collection(nameof(WeatherWidgetSeam))]
public class WeatherClientTests : IDisposable
{
    public void Dispose()
    {
        WeatherWidget.InteractiveCheck = OperatingSystem.IsBrowser;
        WeatherWidget.LocalNow = static () => DateTime.Now;
    }

    // ── Symbols and units ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("clearsky_day", "Clear sky", "wb_sunny")]
    [InlineData("clearsky_night", "Clear sky", "bedtime")]
    [InlineData("partlycloudy_polartwilight", "Partly cloudy", "wb_twilight")]
    [InlineData("cloudy", "Cloudy", "cloud")]
    [InlineData("lightrain", "Light rain", "grain")]
    [InlineData("heavysleetshowersandthunder_day", "Heavy sleet showers and thunder", "thunderstorm")]
    [InlineData("tornado_day", "Unknown conditions", "cloud")]
    public void Every_symbol_has_words_and_an_unknown_one_is_never_guessed(string code, string label, string icon)
    {
        var symbol = WeatherVisuals.SymbolOf(code)!;
        Assert.Equal(label, symbol.Label);
        Assert.Equal(icon, symbol.Icon);
    }

    [Theory]
    [InlineData("lightssleetshowersandthunder_day", "Light sleet showers and thunder")]
    [InlineData("lightssnowshowersandthunder_polartwilight", "Light snow showers and thunder")]
    public void Mets_doubled_s_thunder_codes_resolve_to_their_base(string code, string label)
    {
        var symbol = WeatherVisuals.SymbolOf(code)!;
        Assert.True(symbol.Known);
        Assert.Equal(label, symbol.Label);
        Assert.Equal("thunderstorm", symbol.Icon);
    }

    [Fact]
    public void Every_met_legend_code_is_a_known_symbol()
    {
        var unknown = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "met-symbol-codes.txt"))
            .Where(line => line.Length > 0 && line[0] != '#')
            .Where(code => !WeatherVisuals.SymbolOf(code)!.Known)
            .ToList();

        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData(-3.4, "−3°")]
    [InlineData(-0.4, "0°")]
    [InlineData(2.5, "3°")]
    [InlineData(null, "—")]
    public void Temperatures_are_whole_degrees_with_a_true_minus(double? value, string expected) =>
        Assert.Equal(expected, WeatherVisuals.Temp((decimal?)value));

    [Theory]
    [InlineData(0, "N", "north")]
    [InlineData(225, "SW", "south-west")]
    [InlineData(350, "N", "north")]
    [InlineData(-90, "W", "west")]
    public void Wind_direction_reads_as_a_compass_point_with_its_name(int degrees, string point, string words)
    {
        Assert.Equal(point, WeatherVisuals.CompassOf(degrees));
        Assert.Equal(words, WeatherVisuals.CompassWordsOf(degrees));
    }

    [Fact]
    public void The_fingerprint_is_the_shared_rule()
    {
        var location = Location(59.91273m, 10.75027m);
        Assert.Equal("v1:59.913:10.750:12:Europe/Oslo", WeatherVisuals.Fingerprint(location));
    }

    // ── Row menus by claim (issue #388 §3) ───────────────────────────────────────────────────────

    private static IReadOnlyList<string?> LinkLabels(ExistingLocation l, bool canLink) =>
        [.. LocationsPage.BuildWeatherLinkActions(l, canLink, default, default).Select(i => i.Label)];

    [Fact]
    public void Linking_is_offered_to_a_linker_only_and_never_on_a_merged_away_row()
    {
        Assert.Equal(["Link weather point…"], LinkLabels(Location(), canLink: true));
        Assert.Equal(["Change weather point…", "Unlink weather point"], LinkLabels(Linked(Location()), canLink: true));
        Assert.Empty(LinkLabels(Location(), canLink: false));
        Assert.Empty(LinkLabels(Linked(Location()) with { MergedIntoLocationId = Guid.NewGuid() }, canLink: true));
    }

    [Fact]
    public void The_link_items_sit_directly_after_copy_id()
    {
        var labels = LocationsPage.BuildRowActions(Location(), new LocationsPage.LocationRowClaims(true, true),
                default, default, default, default, default, default,
                LocationsPage.BuildWeatherLinkActions(Location(), true, default, default))
            .Where(i => !i.Divider).Select(i => i.Label).ToList();

        Assert.Equal(labels.IndexOf("Copy ID") + 1, labels.IndexOf("Link weather point…"));
        Assert.True(labels.IndexOf("Merge into…") > labels.IndexOf("Link weather point…"));
    }

    private static IReadOnlyList<string?> PointLabels(ExistingWeatherPoint p, bool canUpdate = true, bool canDelete = true) =>
        [.. WeatherPointsPage.BuildRowActions(p, canUpdate, canDelete, default, default, default, default).Where(i => !i.Divider).Select(i => i.Label)];

    [Fact]
    public void The_point_menu_follows_state_and_claims()
    {
        var point = Row(Location());
        Assert.Equal(["Weather settings…", "Refresh forecast"], PointLabels(point));
        Assert.Equal(["Weather settings…", "Delete weather history…"], PointLabels(point with { ForecastEnabled = false, HistoryEnabled = false, IsHouseholdDefault = true }).Take(2));
        Assert.Equal(["Weather settings…", "Delete weather history…", "Delete weather point…"], PointLabels(point with { ForecastEnabled = false, HistoryEnabled = false }));
        Assert.Equal(["Weather settings…", "Refresh forecast", "Delete weather history…"], PointLabels(point with { HistoryEnabled = false }));
        Assert.Equal(["Continue deleting…"], PointLabels(point with { PurgeInProgress = true }));
        // Absent without the claim, never dimmed (AC 22).
        Assert.Empty(PointLabels(point, canUpdate: false, canDelete: false));
        Assert.Equal(["Delete weather history…", "Delete weather point…"], PointLabels(point with { ForecastEnabled = false, HistoryEnabled = false }, canUpdate: false));
        Assert.Equal(["Weather settings…"], PointLabels(point with { ForecastEnabled = false, HistoryEnabled = false }, canDelete: false));
        Assert.All(WeatherPointsPage.BuildRowActions(point, true, true, default, default, default, default).Where(i => !i.Divider),
            i => Assert.Contains(point.Name, i.AriaLabel, StringComparison.Ordinal));
    }

    // ── Dashboard card ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ok_shows_now_the_days_and_the_attribution_from_the_response()
    {
        var home = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Home" };
        // The card's own clock, pinned, so a run that straddles midnight cannot disagree with itself.
        WeatherWidget.LocalNow = static () => new DateTime(2026, 10, 23, 12, 0, 0);
        var today = new DateOnly(2026, 10, 23);
        using var ctx = Context(weather =>
        {
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherCurrentResponse
            {
                Point = home,
                Status = WeatherStatus.Ok,
                Step = new WeatherStep { SymbolCode = "rain", AirTemperatureC = -2.6m, WindSpeedMps = 4, WindFromDirectionDeg = 225, IssuedAtUtc = DateTime.UtcNow },
                Attribution = new WeatherAttribution { Text = "Served attribution", License = "NLOD 2.0 / CC BY 4.0", Url = "https://api.met.no/" },
            }));
            weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherDailyResponse
            {
                Point = home,
                Status = WeatherStatus.Ok,
                Days =
                [
                    new WeatherDaySummary { Date = today, Forecast = new WeatherDayForecast { SymbolCode = "rain", MinTempC = 4, MaxTempC = 11, CoverageHours = 14, PrecipitationMm = 6.2m } },
                    new WeatherDaySummary { Date = today.AddDays(1), Forecast = new WeatherDayForecast { SymbolCode = "cloudy", MinTempC = 3, MaxTempC = 9, CoverageHours = 24 } },
                ],
            }));
        });

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains("−3°", card.Markup));
        Assert.Contains("Weather · Home", card.Find(".wx-ttl").TextContent.Replace(" ", " "));
        Assert.Contains("Rain", card.Markup);
        Assert.Contains("from <abbr title=\"south-west\"><span aria-hidden=\"true\">SW</span><span class=\"sr-only\">south-west</span></abbr>", card.Markup);
        Assert.Contains("Rain, high 11°, low 4°", card.Markup);
        Assert.Contains("14 h of forecast", card.Markup);
        Assert.Equal(2, card.FindAll(".wx-day").Count);
        Assert.Contains("Served attribution", card.Find(".wx-attr").TextContent);
        Assert.Empty(card.FindAll("button:contains('Refresh')"));
    }

    [Theory]
    [InlineData(WeatherStatus.NoPlace, true, "Go to Weather points")]
    [InlineData(WeatherStatus.NoPlace, false, "An administrator can create a weather point")]
    [InlineData(WeatherStatus.NotConfigured, true, "Open System settings")]
    [InlineData(WeatherStatus.NotConfigured, false, "Weather isn’t set up yet")]
    public void Each_empty_state_names_its_way_out(WeatherStatus status, bool admin, string expected)
    {
        using var ctx = Context(
            weather => weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherCurrentResponse { Status = status })),
            admin ? [PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate, PermissionClaims.SystemSettingsUpdate] : null);

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains(expected, card.Markup));
    }

    [Fact]
    public async Task Choosing_a_place_saves_it_and_keeps_the_picker_mounted_and_enabled()
    {
        var home = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Home" };
        var cabin = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Cabin" };
        Mock<IWeatherApiClient>? client = null;
        await using var ctx = Context(weather =>
        {
            client = weather;
            weather.Setup(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherPointOptions { Points = [Option(home), Option(cabin)], HouseholdDefaultPointId = home.WeatherPointId }));
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherCurrentResponse
            {
                Point = home, Status = WeatherStatus.NoData,
            }));
            weather.Setup(w => w.SetPreferenceAsync(cabin.WeatherPointId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        });

        var card = ctx.Render<WeatherWidget>();
        card.WaitForAssertion(() => Assert.Single(card.FindComponents<Odyssey.Client.Components.OdsSelect>()));
        var picker = card.FindComponent<Odyssey.Client.Components.OdsSelect>();

        await card.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(cabin.WeatherPointId.ToString("D")));

        card.WaitForAssertion(() => client!.Verify(w => w.SetPreferenceAsync(cabin.WeatherPointId, It.IsAny<CancellationToken>()), Times.Once));
        // The same component instance survives the reload, never disabled: focus has nowhere to fall.
        Assert.Same(picker.Instance, card.FindComponent<Odyssey.Client.Components.OdsSelect>().Instance);
        Assert.False(picker.Instance.Disabled);
        Assert.Empty(card.FindAll(".wx-skel"));
    }

    [Fact]
    public async Task A_failed_reload_after_a_save_keeps_the_picker_and_says_the_choice_is_saved()
    {
        var home = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Home" };
        var cabin = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Cabin" };
        var currentCalls = 0;
        await using var ctx = Context(weather =>
        {
            weather.Setup(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherPointOptions { Points = [Option(home), Option(cabin)], HouseholdDefaultPointId = home.WeatherPointId }));
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(() => ++currentCalls == 1
                ? Ok(new WeatherCurrentResponse { Point = home, Status = WeatherStatus.NoData })
                : ApiResult<WeatherCurrentResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
            weather.Setup(w => w.SetPreferenceAsync(cabin.WeatherPointId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        });
        var card = ctx.Render<WeatherWidget>();
        card.WaitForAssertion(() => Assert.Single(card.FindComponents<Odyssey.Client.Components.OdsSelect>()));
        var picker = card.FindComponent<Odyssey.Client.Components.OdsSelect>();

        await card.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(cabin.WeatherPointId.ToString("D")));

        // The picker the viewer is on stays mounted (WCAG 2.4.3); the failure is said beside it.
        card.WaitForAssertion(() => Assert.Contains("Your choice is saved, but the weather couldn’t be reloaded.", card.Markup));
        Assert.Same(picker.Instance, card.FindComponent<Odyssey.Client.Components.OdsSelect>().Instance);
        Assert.DoesNotContain("Weather couldn’t be loaded", card.Markup);
        Assert.DoesNotContain("Weather now shows Cabin", card.Markup);
    }

    [Fact]
    public async Task Someone_who_cannot_choose_a_place_never_loads_the_picker_options()
    {
        Mock<IWeatherApiClient>? client = null;
        await using var ctx = Context(weather =>
        {
            client = weather;
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherCurrentResponse
            {
                Point = new WeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Home" }, Status = WeatherStatus.NoData,
            }));
        }, claims: [PermissionClaims.WeatherRead]);

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains("Weather", card.Markup));
        card.WaitForAssertion(() => client!.Verify(w => w.CurrentAsync(null, It.IsAny<CancellationToken>()), Times.Once));
        client!.Verify(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(w => w.PreferencesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(card.FindAll("h2.wx-ttl"));
    }

    [Fact]
    public void A_429_rests_with_try_again()
    {
        using var ctx = Context(weather => weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherCurrentResponse>.Failure(HttpStatusCode.TooManyRequests, new ApiProblem { Status = 429 })));

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains("Weather is resting for a moment", card.Markup));
        Assert.Contains("Try again", card.Markup);
    }

    [Fact]
    public void The_set_up_link_needs_read_as_well_as_update()
    {
        using var ctx = Context(
            weather => weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherCurrentResponse { Status = WeatherStatus.NoPlace })),
            [PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsUpdate]);

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains("An administrator can create a weather point", card.Markup));
        Assert.DoesNotContain("Go to Weather points", card.Markup);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    private static ExistingLocation Location(decimal latitude = 59.91273m, decimal longitude = 10.75027m, bool archived = false) => new()
    {
        LocationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "Home",
        Latitude = latitude,
        Longitude = longitude,
        AltitudeMeters = 12,
        TimeZoneId = "Europe/Oslo",
        Archived = archived ? DateTime.UtcNow : null,
    };

    private static ExistingLocation Linked(ExistingLocation l) => l with
    {
        WeatherPoint = new LinkedWeatherPointRef { WeatherPointId = Guid.NewGuid(), Name = "Oslo", ForecastEnabled = true, HistoryEnabled = true },
        WeatherPointDistanceKm = 2,
    };

    private static WeatherPointOption Option(WeatherPointRef point) =>
        new() { WeatherPointId = point.WeatherPointId, Name = point.Name, ForecastEnabled = true, HistoryEnabled = true };

    private static ExistingWeatherPoint Row(ExistingLocation l) => new()
    {
        WeatherPointId = l.LocationId,
        Name = l.Name,
        ForecastEnabled = true,
        HistoryEnabled = true,
        IsTracked = true,
        Site = new WeatherPointSiteDto
        {
            Version = 1,
            Latitude = WeatherRules.RoundCoordinate(l.Latitude!.Value),
            Longitude = WeatherRules.RoundCoordinate(l.Longitude!.Value),
            AltitudeMeters = l.AltitudeMeters,
            TimeZoneId = l.TimeZoneId!,
        },
    };

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private static BunitContext Context(Action<Mock<IWeatherApiClient>> setup, IReadOnlyList<string>? claims = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var weather = new Mock<IWeatherApiClient>();
        weather.Setup(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions()));
        weather.Setup(w => w.PreferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPreferences()));
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherDailyResponse()));
        setup(weather);

        ctx.Services.AddSingleton(weather.Object);
        var auth = new Claims(claims ?? [PermissionClaims.WeatherRead, PermissionClaims.WeatherPreferencesUpdate]);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(auth);
        ctx.Services.AddSingleton<Odyssey.Client.Services.IWeatherPointOptionsCache>(new Odyssey.Client.Services.WeatherPointOptionsCache(weather.Object, auth));
        WeatherWidget.InteractiveCheck = static () => true;
        return ctx;
    }

    private sealed class Claims(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(p => new Claim(PermissionClaims.Type, p))], "test"))));
    }
}

/// <summary>Serialises the tests that set the widget's static interactivity seam.</summary>
[CollectionDefinition(nameof(WeatherWidgetSeam), DisableParallelization = true)]
public sealed class WeatherWidgetSeam;
