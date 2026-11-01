using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="LocationMap"/>'s state machine and lifecycle (issue #371 AC 8, 9, 10, 14, 20). MapLibre is
/// behind <c>location-map.js</c>, which bUnit fakes here; what runs in a real browser is the E2E tier's.
/// </summary>
public class LocationMapTests : IAsyncLifetime
{
    private const string Light = "https://tiles.example/styles/light";
    private const string Dark = "https://tiles.example/styles/dark";

    private static readonly MapStyles Styles = new()
    {
        LightStyleUrl = Light,
        DarkStyleUrl = Dark,
        Attribution = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap",
        AttributionUrl = "https://www.openstreetmap.org/copyright",
    };

    private readonly BunitContext ctx = new();
    private readonly Mock<IUserPreferenceService> preferences = new();
    private readonly FakeStylesCache cache = new();
    private readonly List<string> announced = [];
    private readonly BunitJSModuleInterop module;

    public LocationMapTests()
    {
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        preferences.SetupGet(p => p.Current).Returns(new UserPreferencesPage(DarkModeEnabled: false));
        ctx.Services.AddSingleton(preferences.Object);
        ctx.Services.AddSingleton<IMapStylesCache>(cache);

        module = ctx.JSInterop.SetupModule(LocationMap.ModulePath);
        module.Setup<bool>("isSupported").SetResult(true);
        // Setup(identifier) with no matcher only matches a call with NO arguments, so every one that
        // takes a handle gets an explicit catch-all.
        module.SetupVoid("update", _ => true).SetVoidResult();
        module.SetupVoid("dispose", _ => true).SetVoidResult();
        module.SetupVoid("focusSurface", _ => true).SetVoidResult();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ctx.DisposeAsync();

    private IRenderedComponent<LocationMap> Render(decimal lat = 61.52914m, decimal lng = 10.13802m, string name = "Cabin") =>
        ctx.Render<LocationMap>(p => p
            .Add(c => c.Latitude, lat)
            .Add(c => c.Longitude, lng)
            .Add(c => c.Name, name)
            .Add(c => c.OnStatus, (string s) => announced.Add(s)));

    private JSRuntimeInvocationHandler<int> SetupCreate(int? handle = 1)
    {
        var create = module.Setup<int>("create", _ => true);
        if (handle is { } h)
            create.SetResult(h);
        return create;
    }

    // ── Refusals before any map (AC 20) ─────────────────────────────────────────────────────────

    [Fact]
    public void Invalid_coordinates_render_the_state_without_probing_or_creating()
    {
        cache.Next = Styles;
        SetupCreate();

        var cut = Render(lat: 91m);

        Assert.Equal(LocationMapState.InvalidCoordinates, cut.Instance.State);
        Assert.Contains("This location has no usable coordinates.", cut.Markup);
        Assert.Empty(module.Invocations["isSupported"]);
        Assert.Empty(module.Invocations["create"]);
        Assert.Empty(announced);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void No_webgl_shows_its_state_announces_once_and_never_creates()
    {
        module.Setup<bool>("isSupported").SetResult(false);
        cache.Next = Styles;
        SetupCreate();

        var cut = Render();

        cut.WaitForAssertion(() => Assert.Equal(LocationMapState.WebGlUnavailable, cut.Instance.State));
        Assert.Contains("This browser can't draw maps (WebGL is off or unavailable).", cut.Markup);
        Assert.Empty(module.Invocations["create"]);
        Assert.Equal(["Map of Cabin can't be shown in this browser."], announced);
        Assert.Empty(cut.FindAll(".loc-map-caption"));
        Assert.Empty(cut.FindAll("button"));
    }

    // ── Settings (AC 14) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_settings_failure_is_its_own_state_and_try_again_refetches()
    {
        cache.Next = null;
        SetupCreate();

        var cut = Render();
        cut.WaitForAssertion(() => Assert.Equal(LocationMapState.ConfigUnavailable, cut.Instance.State));
        Assert.Contains("The map couldn't load its settings.", cut.Markup);
        Assert.Equal(["Map of Cabin couldn't load its settings."], announced);
        Assert.Empty(cut.FindAll(".loc-map-caption"));
        Assert.Empty(cut.FindAll("[role=alert]"));

        cache.Next = Styles;
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        Assert.Equal(2, cache.Calls);
    }

    // ── Ready, caption, announcement on retry ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ready_shows_the_caption_as_text_with_a_safe_new_tab_link()
    {
        cache.Next = Styles with { Attribution = "<img src=x onerror=alert(1)>" };
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));

        var link = cut.Find(".loc-map-caption a");
        Assert.Equal("https://www.openstreetmap.org/copyright", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"));
        Assert.Contains("noreferrer", link.GetAttribute("rel"));
        Assert.Contains("<img src=x onerror=alert(1)>", link.TextContent);
        Assert.Empty(cut.FindAll(".loc-map-caption img"));
        Assert.Empty(announced);
    }

    [Fact]
    public void The_create_call_carries_the_theme_style_zoom_cap_and_description()
    {
        preferences.SetupGet(p => p.Current).Returns(new UserPreferencesPage(DarkModeEnabled: true));
        cache.Next = Styles;
        SetupCreate();

        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        var options = module.Invocations["create"][0].Arguments[1]!;
        Assert.Equal(Dark, Read<string>(options, "styleUrl"));
        Assert.Equal(LocationMap.StartZoom, Read<int>(options, "zoom"));
        Assert.Equal(LocationMap.MaxZoom, Read<int>(options, "maxZoom"));
        Assert.Equal("Cabin", Read<string>(options, "name"));

        var describedBy = Read<string>(options, "describedBy").Split(' ');
        Assert.Contains("Pin at 61.52914° N · 10.13802° E.", cut.Find($"#{describedBy[0]}").TextContent);
        Assert.Equal("Arrow keys pan, plus and minus zoom.", cut.Find($"#{describedBy[1]}").TextContent);
    }

    [Fact]
    public async Task A_failure_offers_try_again_which_announces_loaded_once_ready()
    {
        cache.Next = Styles;
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        await cut.InvokeAsync(() => cut.Instance.OnFailed(cut.Instance.Generation, "tiles"));
        Assert.Equal(LocationMapState.TilesFailed, cut.Instance.State);
        Assert.Contains("The map for Cabin couldn't load. The tile service may be down.", cut.Markup);
        Assert.Equal(["Map of Cabin couldn't load."], announced);

        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["create"].Count));
        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));

        Assert.Equal(LocationMapState.Ready, cut.Instance.State);
        Assert.Equal("Map of Cabin loaded.", announced[^1]);
        Assert.Single(module.Invocations["focusSurface"]);
    }

    // ── Stale callbacks (AC 20, R2-F1) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_callback_from_a_superseded_generation_changes_nothing()
    {
        cache.Next = Styles;
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        var first = cut.Instance.Generation;

        await cut.InvokeAsync(() => cut.Instance.OnFailed(first, "tiles"));
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["create"].Count));
        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));
        announced.Clear();

        await cut.InvokeAsync(() => cut.Instance.OnFailed(first, "timeout"));
        await cut.InvokeAsync(() => cut.Instance.OnDegraded(first));
        await cut.InvokeAsync(() => cut.Instance.OnContextLost(first));

        Assert.Equal(LocationMapState.Ready, cut.Instance.State);
        Assert.Empty(announced);
    }

    // ── Degraded ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Degraded_keeps_the_map_announces_once_and_clears_silently()
    {
        cache.Next = Styles;
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));

        await cut.InvokeAsync(() => cut.Instance.OnDegraded(cut.Instance.Generation));
        await cut.InvokeAsync(() => cut.Instance.OnDegraded(cut.Instance.Generation));

        var alert = cut.Find(".loc-map-alert");
        Assert.Contains("Some map tiles didn't load. The tile service may be slow.", alert.TextContent);
        Assert.Null(alert.GetAttribute("role"));
        Assert.Single(cut.FindAll(".loc-map-caption"));
        Assert.Equal(["Map of Cabin: some tiles didn't load."], announced);

        await cut.InvokeAsync(() => cut.Instance.OnRecovered(cut.Instance.Generation));
        Assert.Empty(cut.FindAll(".loc-map-alert"));
        Assert.Single(announced);
    }

    // ── Parameters (AC 9) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Moving_the_coordinates_or_renaming_calls_update()
    {
        cache.Next = Styles;
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        cut.Render(p => p.Add(c => c.Latitude, 59.91273m).Add(c => c.Longitude, 10.75027m));
        cut.Render(p => p.Add(c => c.Name, "Old cabin"));

        var updates = module.Invocations["update"];
        Assert.Equal(2, updates.Count);
        Assert.Equal(59.91273, Read<double>(updates[0].Arguments[1]!, "lat"));
        Assert.Equal(10.75027, Read<double>(updates[0].Arguments[1]!, "lng"));
        Assert.Equal("Cabin", Read<string>(updates[0].Arguments[1]!, "name"));
        Assert.Equal(59.91273, Read<double>(updates[1].Arguments[1]!, "lat"));
        Assert.Equal("Old cabin", Read<string>(updates[1].Arguments[1]!, "name"));
        // `update` takes no zoom: the camera keeps the user's.
        Assert.Null(updates[0].Arguments[1]!.GetType().GetProperty("zoom"));
        Assert.Single(module.Invocations["create"]);
    }

    // ── Theme (AC 8, R2-F2) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_last_theme_toggle_wins_and_a_superseded_completion_changes_nothing()
    {
        cache.Next = Styles;
        SetupCreate();
        var toDark = module.SetupVoid("setStyle", inv => (string?)inv.Arguments[1] == Dark);
        var toLight = module.SetupVoid("setStyle", inv => (string?)inv.Arguments[1] == Light);
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        Assert.Equal(Light, cut.Instance.AppliedStyleUrl);

        preferences.Raise(p => p.DarkModeChanged += null, true);
        preferences.Raise(p => p.DarkModeChanged += null, false);
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["setStyle"].Count));
        Assert.Equal([Dark, Light], module.Invocations["setStyle"].Select(i => (string?)i.Arguments[1]));

        // The second toggle completes first; the first's late completion must not overwrite it.
        toLight.SetVoidResult();
        cut.WaitForAssertion(() => Assert.Equal(Light, cut.Instance.AppliedStyleUrl));
        toDark.SetVoidResult();

        cut.WaitForAssertion(() => Assert.Equal(Light, cut.Instance.AppliedStyleUrl));
    }

    // ── Disposal (AC 10) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disposing_releases_the_map_once_and_stops_hearing_theme_changes()
    {
        cache.Next = Styles;
        SetupCreate(7);
        module.SetupVoid("setStyle", _ => true).SetVoidResult();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        await cut.Instance.DisposeAsync();
        await cut.Instance.DisposeAsync();
        preferences.Raise(p => p.DarkModeChanged += null, true);

        var dispose = Assert.Single(module.Invocations["dispose"]);
        Assert.Equal(7, dispose.Arguments[0]);
        Assert.Empty(module.Invocations["setStyle"]);
    }

    [Fact]
    public async Task Disposing_while_create_is_pending_releases_the_late_map_exactly_once()
    {
        cache.Next = Styles;
        var create = SetupCreate(handle: null);
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        await cut.Instance.DisposeAsync();
        Assert.Empty(module.Invocations["dispose"]);

        create.SetResult(42);

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["dispose"]));
        Assert.Equal(42, module.Invocations["dispose"][0].Arguments[0]);
        Assert.Empty(announced);
    }

    // ── Review round 2 ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_gpu_context_that_cannot_be_created_is_webgl_unavailable_not_a_tile_failure()
    {
        cache.Next = Styles;
        SetupCreate(0);

        var cut = Render();

        cut.WaitForAssertion(() => Assert.Equal(LocationMapState.WebGlUnavailable, cut.Instance.State));
        Assert.Equal(["Map of Cabin can't be shown in this browser."], announced);
        Assert.Empty(module.Invocations["dispose"]);
    }

    [Fact]
    public void A_move_while_create_is_pending_reaches_the_map_once_it_exists()
    {
        cache.Next = Styles;
        var create = SetupCreate(handle: null);
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        cut.Render(p => p.Add(c => c.Latitude, 59.91273m).Add(c => c.Name, "Old cabin"));
        Assert.Empty(module.Invocations["update"]);
        create.SetResult(3);

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["update"]));
        var update = module.Invocations["update"][0];
        Assert.Equal(3, update.Arguments[0]);
        Assert.Equal(59.91273, Read<double>(update.Arguments[1]!, "lat"));
        Assert.Equal("Old cabin", Read<string>(update.Arguments[1]!, "name"));
    }

    [Fact]
    public async Task Try_again_that_fails_again_moves_focus_to_the_new_state_box()
    {
        cache.Next = Styles;
        SetupCreate();
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        await cut.InvokeAsync(() => cut.Instance.OnFailed(cut.Instance.Generation, "tiles"));
        var focusBefore = FocusCalls();

        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["create"].Count));
        cut.WaitForAssertion(() => Assert.Equal(focusBefore + 1, FocusCalls()));
        await cut.InvokeAsync(() => cut.Instance.OnFailed(cut.Instance.Generation, "tiles"));

        Assert.Equal(LocationMapState.TilesFailed, cut.Instance.State);
        cut.WaitForAssertion(() => Assert.Equal(focusBefore + 2, FocusCalls()));
        Assert.Empty(module.Invocations["focusSurface"]);
    }

    [Fact]
    public async Task A_restored_context_recreates_the_map_and_keeps_focus_in_it()
    {
        cache.Next = Styles;
        SetupCreate();
        module.Setup<bool>("hasFocus", _ => true).SetResult(true);
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));

        await cut.InvokeAsync(() => cut.Instance.OnContextRestored(cut.Instance.Generation));
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["create"].Count));
        await cut.InvokeAsync(() => cut.Instance.OnReady(cut.Instance.Generation));

        Assert.Equal(LocationMapState.Ready, cut.Instance.State);
        Assert.Single(module.Invocations["dispose"]);
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["focusSurface"]));
        Assert.Empty(announced);
    }

    private int FocusCalls() => ctx.JSInterop.Invocations["Blazor._internal.domWrapper.focus"].Count;

    private static T Read<T>(object anonymous, string property) =>
        (T)anonymous.GetType().GetProperty(property)!.GetValue(anonymous)!;

    private sealed class FakeStylesCache : IMapStylesCache
    {
        public MapStyles? Next { get; set; }

        public int Calls { get; private set; }

        public Task<MapStyles?> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }
}
