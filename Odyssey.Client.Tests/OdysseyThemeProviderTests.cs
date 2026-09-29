using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Layout;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdysseyThemeProvider owns the root element's <c>data-theme</c> attribute (issue #228).
/// </summary>
/// <remarks>
/// MudBlazor 9.10 only rewrites <c>--mud-palette-*</c>; it writes no <c>data-theme</c> anywhere. Every
/// other token's dark variant (finance, status, tag, chart, nav, focus) is keyed on
/// <c>[data-theme='dark']</c>, so unless the provider mirrors <c>IsDarkMode</c> onto the root element
/// those overrides are unreachable and dark surfaces render light-mode tokens. The contrast guard
/// tests parse the CSS text and stay green either way — this is the test that pins the wiring.
/// </remarks>
public class OdysseyThemeProviderTests
{
    private const string SetTheme = "odysseyTheme.set";

    private static BunitContext NewContext(FakePreferences preferences)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IUserPreferenceService>(preferences);
        return ctx;
    }

    private static List<bool> ThemeCalls(BunitContext ctx) =>
        ctx.JSInterop.Invocations
            .Where(i => i.Identifier == SetTheme)
            .Select(i => (bool)i.Arguments[0]!)
            .ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void First_render_stamps_the_known_preference(bool dark)
    {
        using var ctx = NewContext(new FakePreferences(dark));

        ctx.Render<OdysseyThemeProvider>();

        Assert.Equal([dark], ThemeCalls(ctx));
    }

    [Fact]
    public void Each_dark_mode_change_is_mirrored_onto_the_root_element()
    {
        var preferences = new FakePreferences(true);
        using var ctx = NewContext(preferences);
        var cut = ctx.Render<OdysseyThemeProvider>();

        preferences.Raise(false);
        cut.WaitForAssertion(() => Assert.Equal([true, false], ThemeCalls(ctx)));

        preferences.Raise(true);
        cut.WaitForAssertion(() => Assert.Equal([true, false, true], ThemeCalls(ctx)));
    }

    [Fact]
    public void A_re_render_without_a_change_does_not_call_again()
    {
        var preferences = new FakePreferences(true);
        using var ctx = NewContext(preferences);
        var cut = ctx.Render<OdysseyThemeProvider>();

        preferences.Raise(true);
        cut.Render();

        Assert.Equal([true], ThemeCalls(ctx));
    }

    private sealed class FakePreferences(bool dark) : IUserPreferenceService
    {
        public event Action<bool>? DarkModeChanged;

        public void Raise(bool isDarkMode) => DarkModeChanged?.Invoke(isDarkMode);

        public void PreviewDarkMode(bool isDarkMode) => Raise(isDarkMode);

        public Task<bool> GetDarkModePreferencesAsync() => Task.FromResult(Current.DarkModeEnabled);

        public Task LoadUserPreferencesAsync() => Task.CompletedTask;

        public Task<bool> SaveUserPreferencesAsync(UserPreferencesPage userPreferences) => Task.FromResult(true);

        public UserPreferencesPage Current { get; } = new(dark);

        public string? DefaultCurrency => Current.DefaultCurrency;

        public string? MainCurrency => Current.MainCurrency;
    }
}
