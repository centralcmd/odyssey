using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Forecast and historical data as two separate settings (design system · Weather.jsx WeatherSettingsModal and
/// WeatherPurgeModal, components-weather.md): the settings dialog sends both switches, the household default's
/// forecast is locked, and Delete weather history says which scope it has.
/// </summary>
public class WeatherForecastHistorySplitTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();
    private readonly Mock<IWeatherApiClient> weather = new();

    public WeatherForecastHistorySplitTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(weather.Object);
        ctx.Services.AddSingleton(new Mock<ILocationsApiClient>().Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    [Fact]
    public async Task Saving_sends_the_forecast_and_history_switches_separately()
    {
        UpsertWeatherLocation? sent = null;
        weather.Setup(w => w.UpsertLocationAsync(Home.LocationId, It.IsAny<UpsertWeatherLocation>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpsertWeatherLocation, CancellationToken>((_, request, _) => sent = request)
            .ReturnsAsync(ApiResult<ExistingWeatherLocation>.Success(Row(), HttpStatusCode.OK));
        var dialog = RenderSettings(Row());

        await dialog.InvokeAsync(() => Switch(dialog, "Historical data").Instance.CheckedChanged.InvokeAsync(false));
        dialog.FindAll("button").Last(b => b.TextContent.Contains("Save")).Click();

        dialog.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.ForecastEnabled);
        Assert.False(sent.HistoryEnabled);
        // The history-from date belongs to historical data, the schedule to the forecast.
        Assert.DoesNotContain("Keep measured history from", dialog.Markup);
        Assert.Contains("Refresh on a schedule", dialog.Markup);
    }

    [Fact]
    public void The_household_defaults_forecast_cannot_be_turned_off_but_its_history_can()
    {
        var dialog = RenderSettings(Row() with { IsHouseholdDefault = true });

        Assert.True(Switch(dialog, "Forecast").Instance.Disabled);
        Assert.False(Switch(dialog, "Historical data").Instance.Disabled);
    }

    [Fact]
    public async Task Turning_weather_on_needs_at_least_one_half()
    {
        var dialog = RenderSettings(null);

        await dialog.InvokeAsync(() => Switch(dialog, "Forecast").Instance.CheckedChanged.InvokeAsync(false));
        await dialog.InvokeAsync(() => Switch(dialog, "Historical data").Instance.CheckedChanged.InvokeAsync(false));

        dialog.WaitForAssertion(() => Assert.Contains("Turn on the forecast, historical data or both.", dialog.Markup));
        Assert.True(dialog.FindAll("button").Last(b => b.TextContent.Contains("Turn on weather")).HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(true, "The forecast keeps running")]
    [InlineData(false, "every weather position it has used")]
    public void Delete_weather_history_names_its_scope(bool forecastOn, string expected)
    {
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<WeatherPurgeDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.Location, HomeLocation)
            .Add(d => d.Row, Row() with { ForecastEnabled = forecastOn, HistoryEnabled = false }));

        provider.WaitForAssertion(() => Assert.Contains(expected, provider.Markup));
    }

    [Fact]
    public void Starting_a_delete_moves_focus_to_its_explanation_before_the_controls_are_disabled()
    {
        var finish = new TaskCompletionSource<ApiResult>();
        weather.Setup(w => w.PurgeAsync(HomeLocation.LocationId, It.IsAny<CancellationToken>())).Returns(finish.Task);
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<WeatherPurgeDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.Location, HomeLocation)
            .Add(d => d.Row, Row() with { HistoryEnabled = false }));
        provider.WaitForAssertion(() => Assert.Contains("ods-modal", provider.Markup));

        provider.FindComponents<OdsField>().Single().Find("input").Input("Home");
        int Focuses() => ctx.JSInterop.Invocations.Count(i => i.Identifier == "Blazor._internal.domWrapper.focus");
        var before = Focuses();

        provider.FindAll("button").Last(b => b.TextContent.Contains("Delete weather history")).Click();

        // Every control is disabled while it runs, so focus must have been moved somewhere inside the dialog first
        // — its explanation paragraph — rather than left on the button that just went disabled (WCAG 2.4.3).
        Assert.Equal(before + 1, Focuses());
        Assert.True(provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").HasAttribute("disabled"));
        Assert.Contains("tabindex=\"-1\"", System.Text.RegularExpressions.Regex.Match(provider.Markup, "<p class=\"wx-lede\"[^>]*>").Value);
        finish.SetResult(ApiResult.Success(HttpStatusCode.NoContent));
    }

    [Fact]
    public async Task The_settings_dialog_cannot_be_closed_while_a_save_is_in_flight()
    {
        var finish = new TaskCompletionSource<ApiResult<ExistingWeatherLocation>>();
        weather.Setup(w => w.UpsertLocationAsync(Home.LocationId, It.IsAny<UpsertWeatherLocation>(), It.IsAny<CancellationToken>()))
            .Returns(finish.Task);
        var closed = false;
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<WeatherSettingsDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.OpenChanged, (bool open) => { if (!open) closed = true; })
            .Add(d => d.Location, HomeLocation)
            .Add(d => d.Row, Row())
            .Add(d => d.TrackedCount, 1)
            .Add(d => d.Cap, 5));
        provider.WaitForAssertion(() => Assert.Contains("ods-modal", provider.Markup));

        provider.FindAll("button").Last(b => b.TextContent.Contains("Save")).Click();
        provider.WaitForAssertion(() => Assert.True(provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").HasAttribute("disabled")));
        Assert.False(closed);

        await provider.InvokeAsync(() => finish.SetResult(ApiResult<ExistingWeatherLocation>.Success(Row(), HttpStatusCode.OK)));
        provider.WaitForAssertion(() => Assert.True(closed));
    }

    [Fact]
    public void A_select_reapplies_its_accessible_name_when_the_name_changes()
    {
        var select = ctx.Render<OdsSelect>(p => p.Add(s => s.AriaLabel, "Weather location").Add(s => s.Options, [new OdsOption("", "Home")]));
        select.Render(p => p.Add(s => s.AriaLabel, "Your weather location"));

        var calls = ctx.JSInterop.Invocations["odsSetAttr"];
        Assert.Equal(["Weather location", "Your weather location"], calls.Select(c => (string?)c.Arguments[2]));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly ExistingLocation HomeLocation = new()
    {
        LocationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Name = "Home",
        Latitude = 59.92302m,
        Longitude = 10.75937m,
        TimeZoneId = "Europe/Oslo",
    };

    private static readonly ExistingWeatherLocation Home = Row();

    private static ExistingWeatherLocation Row() => new()
    {
        LocationId = HomeLocation.LocationId,
        Name = HomeLocation.Name,
        ForecastEnabled = true,
        HistoryEnabled = true,
        IsTracked = true,
        HistoryFrom = new DateOnly(2025, 3, 2),
        Site = new WeatherLocationSiteDto
        {
            Version = 1,
            Latitude = WeatherRules.RoundCoordinate(HomeLocation.Latitude),
            Longitude = WeatherRules.RoundCoordinate(HomeLocation.Longitude),
            TimeZoneId = HomeLocation.TimeZoneId,
        },
    };

    /// <summary>The dialog is an inline MudDialog, so the provider's markup is what the user sees.</summary>
    private IRenderedComponent<MudDialogProvider> RenderSettings(ExistingWeatherLocation? row)
    {
        var provider = ctx.Render<MudDialogProvider>();
        ctx.Render<WeatherSettingsDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.Location, HomeLocation)
            .Add(d => d.Row, row)
            .Add(d => d.TrackedCount, 1)
            .Add(d => d.Cap, 5));
        provider.WaitForAssertion(() => Assert.Contains("ods-modal", provider.Markup));
        return provider;
    }

    private static IRenderedComponent<OdsSwitch> Switch(IRenderedComponent<MudDialogProvider> dialog, string label) =>
        dialog.FindComponents<OdsSwitch>().Single(s => s.Instance.AriaLabel == label);
}
