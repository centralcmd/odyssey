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
    public void Dispose() => WeatherWidget.InteractiveCheck = OperatingSystem.IsBrowser;

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
    public void The_fingerprint_and_moved_check_are_the_shared_rule()
    {
        var location = Location(59.91273m, 10.75027m);
        Assert.Equal("v1:59.913:10.750:12:Europe/Oslo", WeatherVisuals.Fingerprint(location));

        var row = Row(location);
        Assert.False(WeatherVisuals.Moved(row, location));
        Assert.False(WeatherVisuals.Moved(row, Location(59.91301m, 10.75027m)));
        Assert.True(WeatherVisuals.Moved(row, Location(60.39299m, 10.75027m)));
    }

    // ── Admin menu ─────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string?> Labels(ExistingLocation l, ExistingWeatherLocation? row, bool canUpdate = true, bool canPurge = true) =>
        [.. LocationsPage.BuildWeatherActions(l, row, canUpdate, canPurge, default, default, default, default)
            .Where(i => !i.Divider).Select(i => i.Label)];

    [Fact]
    public void A_place_without_weather_offers_turn_on_unless_archived()
    {
        Assert.Equal(["Turn on weather…"], Labels(Location(), null));
        Assert.Empty(Labels(Location(archived: true), null));
        Assert.Empty(Labels(Location(), null, canUpdate: false));
    }

    [Fact]
    public void Weather_on_offers_settings_and_refresh_and_apply_only_when_moved()
    {
        var l = Location();
        Assert.Equal(["Weather settings…", "Refresh forecast"], Labels(l, Row(l)));
        Assert.Equal(["Weather settings…", "Refresh forecast", "Apply new position…"], Labels(Location(60.39299m), Row(l)));
    }

    [Fact]
    public void Delete_history_is_offered_only_when_off_and_not_the_household_default()
    {
        var l = Location();
        Assert.Equal(["Weather settings…", "Delete weather history…"], Labels(l, Row(l) with { Enabled = false }));
        Assert.Equal(["Weather settings…"], Labels(l, Row(l) with { Enabled = false, IsHouseholdDefault = true }));
        Assert.Equal(["Weather settings…"], Labels(l, Row(l) with { Enabled = false }, canPurge: false));
        Assert.Equal(["Continue deleting weather history…"], Labels(l, Row(l) with { Enabled = false, PurgeInProgress = true }));
    }

    // ── Dashboard card ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ok_shows_now_the_days_and_the_attribution_from_the_response()
    {
        var home = new WeatherLocationRef { LocationId = Guid.NewGuid(), Name = "Home" };
        var today = DateOnly.FromDateTime(DateTime.Now);
        using var ctx = Context(weather =>
        {
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherCurrentResponse
            {
                Location = home,
                Status = WeatherStatus.Ok,
                Step = new WeatherStep { SymbolCode = "rain", AirTemperatureC = -2.6m, WindSpeedMps = 4, WindFromDirectionDeg = 225, IssuedAtUtc = DateTime.UtcNow },
                Attribution = new WeatherAttribution { Text = "Served attribution", License = "NLOD 2.0 / CC BY 4.0", Url = "https://api.met.no/" },
            }));
            weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherDailyResponse
            {
                Location = home,
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
    [InlineData(WeatherStatus.NoPlace, true, "Go to Locations")]
    [InlineData(WeatherStatus.NoPlace, false, "An administrator can turn weather on")]
    [InlineData(WeatherStatus.NotConfigured, true, "Open System settings")]
    [InlineData(WeatherStatus.NotConfigured, false, "Weather isn’t set up yet")]
    public void Each_empty_state_names_its_way_out(WeatherStatus status, bool admin, string expected)
    {
        using var ctx = Context(
            weather => weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherCurrentResponse { Status = status })),
            admin ? [PermissionClaims.WeatherRead, PermissionClaims.WeatherLocationsUpdate, PermissionClaims.SystemSettingsUpdate] : null);

        var card = ctx.Render<WeatherWidget>();

        card.WaitForAssertion(() => Assert.Contains(expected, card.Markup));
    }

    [Fact]
    public async Task Choosing_a_place_saves_it_and_keeps_the_picker_mounted_and_enabled()
    {
        var home = new WeatherLocationRef { LocationId = Guid.NewGuid(), Name = "Home" };
        var cabin = new WeatherLocationRef { LocationId = Guid.NewGuid(), Name = "Cabin" };
        Mock<IWeatherApiClient>? client = null;
        await using var ctx = Context(weather =>
        {
            client = weather;
            weather.Setup(w => w.OptionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new WeatherLocationOptions { Locations = [home, cabin], HouseholdDefaultLocationId = home.LocationId }));
            weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherCurrentResponse
            {
                Location = home, Status = WeatherStatus.NoData,
            }));
            weather.Setup(w => w.SetPreferenceAsync(cabin.LocationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        });

        var card = ctx.Render<WeatherWidget>();
        card.WaitForAssertion(() => Assert.Single(card.FindComponents<Odyssey.Client.Components.OdsSelect>()));
        var picker = card.FindComponent<Odyssey.Client.Components.OdsSelect>();

        await card.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(cabin.LocationId.ToString("D")));

        card.WaitForAssertion(() => client!.Verify(w => w.SetPreferenceAsync(cabin.LocationId, It.IsAny<CancellationToken>()), Times.Once));
        // The same component instance survives the reload, never disabled: focus has nowhere to fall.
        Assert.Same(picker.Instance, card.FindComponent<Odyssey.Client.Components.OdsSelect>().Instance);
        Assert.False(picker.Instance.Disabled);
        Assert.Empty(card.FindAll(".wx-skel"));
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

    [Theory]
    [InlineData(true, false, "Weather is turned off. Turn it on to see forecasts and historical weather data for this location.")]
    [InlineData(false, false, "Weather is off for this place.")]
    [InlineData(true, true, "Weather is off for this place.")]
    public async Task A_place_without_weather_says_so_and_leaves_turning_it_on_to_the_row_menu(bool canUpdate, bool archived, string expected)
    {
        await using var ctx = Context(_ => { });

        var section = ctx.Render<LocationWeatherSection>(p => p
            .Add(s => s.Location, Location(archived: archived)).Add(s => s.Row, null).Add(s => s.CanUpdate, canUpdate));

        Assert.Contains(expected, section.Markup);
        Assert.Empty(section.FindAll("button"));
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

    private static ExistingWeatherLocation Row(ExistingLocation l) => new()
    {
        LocationId = l.LocationId,
        Name = l.Name,
        Enabled = true,
        IsTracked = true,
        Site = new WeatherLocationSiteDto
        {
            Version = 1,
            Latitude = WeatherRules.RoundCoordinate(l.Latitude),
            Longitude = WeatherRules.RoundCoordinate(l.Longitude),
            AltitudeMeters = l.AltitudeMeters,
            TimeZoneId = l.TimeZoneId,
        },
    };

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private static BunitContext Context(Action<Mock<IWeatherApiClient>> setup, IReadOnlyList<string>? claims = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var weather = new Mock<IWeatherApiClient>();
        weather.Setup(w => w.OptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherLocationOptions()));
        weather.Setup(w => w.PreferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPreferences()));
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherDailyResponse()));
        setup(weather);

        ctx.Services.AddSingleton(weather.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new Claims(claims ?? [PermissionClaims.WeatherRead, PermissionClaims.WeatherPreferencesUpdate]));
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
