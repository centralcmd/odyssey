using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Weather location row on Preferences (design system · Preferences.jsx, components-weather.md →
/// Preferences → Weather location): the dashboard card's choice, saved through the weather endpoint and
/// never in the page's preferences blob.
/// </summary>
[Collection(nameof(PreferencesSeam))]
public class PreferencesWeatherLocationTests : IDisposable
{
    private static readonly WeatherLocationRef Home = new() { LocationId = Guid.NewGuid(), Name = "Home" };
    private static readonly WeatherLocationRef Cabin = new() { LocationId = Guid.NewGuid(), Name = "Cabin" };

    private readonly Mock<IWeatherApiClient> weather = new();
    private readonly Mock<IUserPreferenceService> preferences = new();

    public void Dispose() => Preferences.InteractiveCheck = OperatingSystem.IsBrowser;

    [Fact]
    public async Task The_row_offers_the_household_default_first_and_opens_on_the_stored_place()
    {
        await using var ctx = Context(stored: Cabin);

        var page = ctx.Render<Preferences>();

        page.WaitForAssertion(() => Assert.Contains("Weather location", page.Markup));
        var select = WeatherSelect(page);
        Assert.Equal(["Home (household default)", "Cabin"], select.Instance.Options.Select(o => o.Label));
        Assert.Equal(Cabin.LocationId.ToString("D"), select.Instance.Value);
    }

    [Fact]
    public async Task A_stored_place_that_is_no_longer_selectable_reads_as_the_household_default()
    {
        await using var ctx = Context(stored: new WeatherLocationRef { LocationId = Guid.NewGuid(), Name = "Old flat" });

        var page = ctx.Render<Preferences>();

        page.WaitForAssertion(() => Assert.Equal("", WeatherSelect(page).Instance.Value));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task The_row_is_absent_without_the_claim_or_with_one_place(bool claim, int places)
    {
        await using var ctx = Context(locations: new[] { Home, Cabin }[..places], canChoose: claim);

        var page = ctx.Render<Preferences>();

        page.WaitForAssertion(() => Assert.Contains("Main currency", page.Markup));
        Assert.DoesNotContain("Weather location", page.Markup);
    }

    [Fact]
    public async Task Saving_a_changed_place_goes_to_the_weather_endpoint_and_not_the_preferences_blob()
    {
        weather.Setup(w => w.SetPreferenceAsync(Cabin.LocationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        await using var ctx = Context();
        var page = ctx.Render<Preferences>();
        page.WaitForAssertion(() => WeatherSelect(page));

        await page.InvokeAsync(() => WeatherSelect(page).Instance.ValueChanged.InvokeAsync(Cabin.LocationId.ToString("D")));
        await page.InvokeAsync(() => SaveButton(page).Click());

        page.WaitForAssertion(() => weather.Verify(w => w.SetPreferenceAsync(Cabin.LocationId, It.IsAny<CancellationToken>()), Times.Once));
        preferences.Verify(p => p.SaveUserPreferencesAsync(It.IsAny<UserPreferencesPage>()), Times.Never);
    }

    [Fact]
    public async Task Choosing_the_household_default_sends_null()
    {
        weather.Setup(w => w.SetPreferenceAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        await using var ctx = Context(stored: Cabin);
        var page = ctx.Render<Preferences>();
        page.WaitForAssertion(() => WeatherSelect(page));

        await page.InvokeAsync(() => WeatherSelect(page).Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => SaveButton(page).Click());

        page.WaitForAssertion(() => weather.Verify(w => w.SetPreferenceAsync(null, It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task A_400_puts_the_error_on_the_row_and_keeps_it_dirty()
    {
        weather.Setup(w => w.SetPreferenceAsync(Cabin.LocationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.BadRequest, new ApiProblem { Status = 400 }));
        await using var ctx = Context();
        var page = ctx.Render<Preferences>();
        page.WaitForAssertion(() => WeatherSelect(page));

        await page.InvokeAsync(() => WeatherSelect(page).Instance.ValueChanged.InvokeAsync(Cabin.LocationId.ToString("D")));
        await page.InvokeAsync(() => SaveButton(page).Click());

        page.WaitForAssertion(() => Assert.Equal("That location no longer has weather. Choose another one.", WeatherSelect(page).Instance.Error));
        Assert.False(SaveButton(page).HasAttribute("disabled"));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    private static IRenderedComponent<OdsSelect> WeatherSelect(IRenderedComponent<Preferences> page) =>
        page.FindComponents<OdsSelect>().Single(s => s.Instance.AriaLabel == "Weather location");

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<Preferences> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Save changes") || b.TextContent.Contains("Saved"));

    private BunitContext Context(WeatherLocationRef? stored = null, WeatherLocationRef[]? locations = null, bool canChoose = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var page = new UserPreferencesPage(true, "NOK", "NOK");
        preferences.SetupGet(p => p.Current).Returns(page);
        preferences.SetupGet(p => p.DefaultCurrency).Returns("NOK");
        preferences.SetupGet(p => p.MainCurrency).Returns("NOK");
        preferences.Setup(p => p.GetDarkModePreferencesAsync()).ReturnsAsync(true);

        var reference = new Mock<IReferenceDataCache>();
        reference.Setup(r => r.ActiveCurrenciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        reference.Setup(r => r.CurrencyOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        weather.Setup(w => w.OptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult<WeatherLocationOptions>.Success(
            new WeatherLocationOptions { Locations = locations ?? [Home, Cabin], HouseholdDefaultLocationId = Home.LocationId }, HttpStatusCode.OK));
        weather.Setup(w => w.PreferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult<WeatherPreferences>.Success(
            new WeatherPreferences { DefaultLocation = stored, HouseholdDefaultLocation = Home }, HttpStatusCode.OK));

        ctx.Services.AddSingleton(preferences.Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(reference.Object);
        ctx.Services.AddSingleton(weather.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new Claims(canChoose
            ? [PermissionClaims.UserPreferencesRead, PermissionClaims.WeatherRead, PermissionClaims.WeatherPreferencesUpdate]
            : [PermissionClaims.UserPreferencesRead, PermissionClaims.WeatherRead]));
        Preferences.InteractiveCheck = static () => true;
        return ctx;
    }

    private sealed class Claims(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(p => new Claim(PermissionClaims.Type, p))], "test"))));
    }
}

/// <summary>Serialises the tests that set the Preferences page's static interactivity seam.</summary>
[CollectionDefinition(nameof(PreferencesSeam), DisableParallelization = true)]
public sealed class PreferencesSeam;
