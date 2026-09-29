using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Odyssey.Client.Layout;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdysseyThemeProvider owns the root element's <c>data-theme</c> attribute (issue #228).
/// </summary>
/// <remarks>
/// <para>
/// MudBlazor 9.10 only rewrites <c>--mud-palette-*</c>; it writes no <c>data-theme</c> anywhere. Every
/// other token's dark variant (finance, status, tag, chart, nav, focus) is keyed on
/// <c>[data-theme='dark']</c>, so unless the provider mirrors <c>IsDarkMode</c> onto the root element
/// those overrides are unreachable and dark surfaces render light-mode tokens. The contrast guard
/// tests parse the CSS text and stay green either way — this is the test that pins the wiring.
/// </para>
/// <para>
/// <b>What this tier does not reach:</b> <c>OperatingSystem.IsBrowser()</c> is false under bUnit, so
/// the browser-only preference load in <c>OnAfterRenderAsync</c> (and the wait for it before the first
/// write) never runs here. That path is covered by <c>Odyssey.E2ETests.DarkThemeAttributeTests</c>
/// against a real stack. What runs here is the value seeded from <c>Current</c>, every
/// <c>DarkModeChanged</c>, the de-duplication, disposal and the JS-failure tolerance.
/// </para>
/// </remarks>
public class OdysseyThemeProviderTests
{
    private const string SetTheme = OdysseyThemeProvider.SetThemeAttributeIdentifier;

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

    // Mutation-checked: with the `_appliedDarkMode == _isDarkMode` guard removed this fails with
    // [true, true, true] — one call per render rather than per change.
    [Fact]
    public async Task A_re_render_without_a_change_does_not_call_again()
    {
        var preferences = new FakePreferences(true);
        using var ctx = NewContext(preferences);
        var cut = ctx.Render<OdysseyThemeProvider>();

        // Raise on the renderer's dispatcher so the StateHasChanged it queues has completed — its
        // OnAfterRenderAsync included — before the assertion reads the invocations.
        await cut.InvokeAsync(() => preferences.Raise(true));
        cut.Render();

        Assert.Equal([true], ThemeCalls(ctx));
    }

    [Fact]
    public async Task A_change_after_dispose_makes_no_call_and_leaves_no_subscription()
    {
        var preferences = new FakePreferences(true);
        using var ctx = NewContext(preferences);
        ctx.Render<OdysseyThemeProvider>();

        await ctx.DisposeComponentsAsync();
        preferences.Raise(false);

        Assert.False(preferences.HasSubscribers);
        Assert.Equal([true], ThemeCalls(ctx));
    }

    [Fact]
    public void A_missing_js_helper_does_not_break_the_layout_and_is_retried()
    {
        // A stale cached index.html without window.odysseyTheme makes the call throw JSException.
        var preferences = new FakePreferences(true);
        using var ctx = NewContext(preferences);
        var failing = ctx.JSInterop.SetupVoid(SetTheme, _ => true);
        failing.SetException(new JSException("odysseyTheme is not defined"));

        var cut = ctx.Render<OdysseyThemeProvider>();
        Assert.Single(ThemeCalls(ctx));

        // Nothing was marked applied, so the next render tries again rather than giving up.
        cut.Render();
        Assert.Equal([true, true], ThemeCalls(ctx));
    }

    /// <summary>
    /// The component calls a JS function by name, and nothing but this lint ties that name to the
    /// helper index.html defines — a rename on either side would compile and fail only in a browser.
    /// </summary>
    [Fact]
    public void Index_html_defines_the_helper_the_component_calls_and_restores_it_pre_boot()
    {
        var html = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var parts = SetTheme.Split('.');
        Assert.Equal(2, parts.Length);

        Assert.Matches(
            new Regex($@"window\.{Regex.Escape(parts[0])}\s*=\s*\{{[^}}]*\b{Regex.Escape(parts[1])}\s*:\s*function"),
            html);

        // The pre-boot script reads the key the helper writes, and runs in <head> before first paint.
        var writes = Regex.Match(html, @"localStorage\.setItem\('(?<key>[^']+)'");
        Assert.True(writes.Success, "odysseyTheme.set no longer caches the applied theme.");
        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains($"localStorage.getItem('{writes.Groups["key"].Value}')", head);

        // No-JS / no-cache default stays dark, the preference default.
        Assert.Contains("<html lang=\"en\" data-theme=\"dark\">", html);
    }

    private sealed class FakePreferences(bool dark) : IUserPreferenceService
    {
        public event Action<bool>? DarkModeChanged;

        public bool HasSubscribers => DarkModeChanged is not null;

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
