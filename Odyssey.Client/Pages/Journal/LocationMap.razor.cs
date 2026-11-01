using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>Where the location map is (issue #371 §3). Only <see cref="Loading"/>, <see cref="Ready"/> and
/// <see cref="Degraded"/> show the frame; the rest replace it with the state box.</summary>
public enum LocationMapState
{
    Loading,
    Ready,
    Degraded,
    TilesFailed,
    WebGlUnavailable,
    ConfigUnavailable,
    InvalidCoordinates,
}

public partial class LocationMap
{
    /// <summary>Centre zoom on create (§3). The camera keeps the user's zoom on a later move.</summary>
    public const int StartZoom = 14;

    /// <summary>Privacy hardening (§3, #370 §7.5): bounds how precise the requested tiles get.</summary>
    public const int MaxZoom = 16;

    internal const string ModulePath = "./js/location-map.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IMapStylesCache StylesCache { get; set; } = default!;
    [Inject] private IUserPreferenceService Preferences { get; set; } = default!;

    [Parameter, EditorRequired] public decimal Latitude { get; set; }

    [Parameter, EditorRequired] public decimal Longitude { get; set; }

    /// <summary>The location's name: always text — the pin's name, the popup heading and the map's label.</summary>
    [Parameter, EditorRequired] public string Name { get; set; } = string.Empty;

    /// <summary>A full sentence naming the location, for the page's polite announcer (§6.9).</summary>
    [Parameter] public EventCallback<string> OnStatus { get; set; }

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private readonly CancellationTokenSource _cts = new();
    private LocationMapState _state = LocationMapState.Loading;
    private MapStyles? _styles;
    private IJSObjectReference? _module;
    private int? _handle;
    private DotNetObjectReference<LocationMap>? _selfRef;
    private ElementReference _host;
    private ElementReference _frame;
    private ElementReference _stateBox;
    private bool _dark;
    private bool _disposed;
    private bool _createPending;
    private int _generation;
    private int _styleGeneration;
    private string? _appliedStyleUrl;
    private (decimal Lat, decimal Lng, string Name) _shown;
    private bool _announceReady;
    private FocusTarget _focusAfterRender;

    // Set by Try again or by a re-create that took focus from the map; it holds until the outcome, so
    // the ready surface or the failure's state box receives focus rather than <body> (§6.12).
    private bool _focusOutcome;

    private enum FocusTarget { None, Frame, StateBox, Surface }

    internal LocationMapState State => _state;

    /// <summary>The style URL applied after the latest theme change completed; <see langword="null"/> before any.</summary>
    internal string? AppliedStyleUrl => _appliedStyleUrl;

    internal int Generation => _generation;

    private string DescriptionId => $"loc-map-{_uid}-desc";

    private string KeysId => $"loc-map-{_uid}-keys";

    private string RootStyle => $"--loc-accent: {LocationVisuals.Accent}";

    private bool ShowsFrame => _state is LocationMapState.Loading or LocationMapState.Ready or LocationMapState.Degraded;

    private bool ShowsCaption => _state is LocationMapState.Ready or LocationMapState.Degraded;

    private bool CanRetry => _state is LocationMapState.TilesFailed or LocationMapState.ConfigUnavailable;

    private string StateText => _state switch
    {
        LocationMapState.TilesFailed => $"The map for {Name} couldn't load. The tile service may be down.",
        LocationMapState.WebGlUnavailable =>
            "This browser can't draw maps (WebGL is off or unavailable). This place's coordinates are listed in this record.",
        LocationMapState.ConfigUnavailable => "The map couldn't load its settings.",
        _ => "This location has no usable coordinates.",
    };

    /// <summary>Mirrors the server's <c>[Range]</c> and CHECK constraints; never replaces them (§8).</summary>
    public static bool AreUsable(decimal latitude, decimal longitude) =>
        latitude is >= -90m and <= 90m && longitude is >= -180m and <= 180m;

    protected override void OnInitialized()
    {
        _dark = Preferences.Current.DarkModeEnabled;
        Preferences.DarkModeChanged += OnDarkModeChanged;
        _shown = (Latitude, Longitude, Name);
    }

    protected override async Task OnParametersSetAsync()
    {
        if ((Latitude, Longitude, Name) == _shown)
            return;

        var moved = Latitude != _shown.Lat || Longitude != _shown.Lng;
        _shown = (Latitude, Longitude, Name);

        if (!AreUsable(Latitude, Longitude))
        {
            await DisposeMapAsync();
            _state = LocationMapState.InvalidCoordinates;
            return;
        }

        if (_state == LocationMapState.InvalidCoordinates && moved)
        {
            _state = LocationMapState.Loading;
            _ = CreateAsync();
            return;
        }

        if (_handle is { } handle && _module is not null)
        {
            await SafeInvokeAsync(() => _module.InvokeVoidAsync("update", handle, new
            {
                lat = (double)Latitude,
                lng = (double)Longitude,
                name = Name,
            }));
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await CreateAsync();
            return;
        }

        var target = _focusAfterRender;
        _focusAfterRender = FocusTarget.None;
        switch (target)
        {
            case FocusTarget.Frame when ShowsFrame:
                await SafeFocusAsync(_frame);
                break;
            case FocusTarget.StateBox when !ShowsFrame:
                await SafeFocusAsync(_stateBox);
                break;
            case FocusTarget.Surface when _handle is { } handle && _module is not null:
                await SafeInvokeAsync(() => _module.InvokeVoidAsync("focusSurface", handle));
                break;
        }
    }

    /// <summary>The create sequence (§4): import → isSupported → styles → create. Each await re-checks disposal.</summary>
    private async Task CreateAsync()
    {
        var generation = ++_generation;
        var token = _cts.Token;

        if (!AreUsable(Latitude, Longitude))
        {
            _state = LocationMapState.InvalidCoordinates;
            StateHasChanged();
            return;
        }

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", token, ModulePath);
            if (IsStale(generation))
                return;

            if (!await _module.InvokeAsync<bool>("isSupported", token))
            {
                await FailAsync(generation, LocationMapState.WebGlUnavailable);
                return;
            }

            _styles = await StylesCache.GetAsync(token);
            if (IsStale(generation))
                return;

            if (_styles is null)
            {
                await FailAsync(generation, LocationMapState.ConfigUnavailable);
                return;
            }

            if (_state != LocationMapState.Loading)
            {
                _state = LocationMapState.Loading;
                StateHasChanged();
                await Task.Yield();
            }

            _selfRef ??= DotNetObjectReference.Create(this);
            var styleUrl = StyleUrlFor(_dark);
            var created = (Latitude, Longitude, Name);
            _createPending = true;
            int handle;
            try
            {
                // No token: a cancelled await would abandon a map JS still builds. A late handle is released
                // by the stale check below instead.
                handle = await _module.InvokeAsync<int>("create", _host, new
                {
                    lat = (double)created.Latitude,
                    lng = (double)created.Longitude,
                    zoom = StartZoom,
                    maxZoom = MaxZoom,
                    styleUrl,
                    name = created.Name,
                    describedBy = $"{DescriptionId} {KeysId}",
                }, _selfRef, generation);
            }
            finally
            {
                _createPending = false;
            }

            // Disposal or a newer create won the race: this map is not wanted.
            if (IsStale(generation))
            {
                await ReleaseAsync(handle);
                if (_disposed)
                    await ReleaseModuleAsync();
                return;
            }

            // 0: MapLibre could not create a GPU context — the browser can't draw maps.
            if (handle == 0)
            {
                await FailAsync(generation, LocationMapState.WebGlUnavailable);
                return;
            }

            _handle = handle;
            _appliedStyleUrl = styleUrl;

            // A move or rename that landed while create was in flight was skipped (no handle yet); the map
            // was built with the values it was handed, so bring it up to date now.
            if ((Latitude, Longitude, Name) != created)
            {
                await SafeInvokeAsync(() => _module.InvokeVoidAsync("update", handle, new
                {
                    lat = (double)Latitude,
                    lng = (double)Longitude,
                    name = Name,
                }));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // Disposed mid-sequence; nothing left to show.
        }
        catch (JSException)
        {
            // MapLibre or the module could not be loaded: the map can't draw, the same as a failed style.
            await FailAsync(generation, LocationMapState.TilesFailed);
        }
    }

    private bool IsStale(int generation) => _disposed || generation != _generation;

    private string StyleUrlFor(bool dark) => dark ? _styles!.DarkStyleUrl : _styles!.LightStyleUrl;

    private async Task FailAsync(int generation, LocationMapState state)
    {
        if (IsStale(generation))
            return;

        await DisposeMapAsync();
        _state = state;
        if (_focusOutcome)
        {
            _focusOutcome = false;
            _focusAfterRender = FocusTarget.StateBox;
        }

        StateHasChanged();
        await AnnounceAsync(state switch
        {
            LocationMapState.WebGlUnavailable => $"Map of {Name} can't be shown in this browser.",
            LocationMapState.ConfigUnavailable => $"Map of {Name} couldn't load its settings.",
            _ => $"Map of {Name} couldn't load.",
        });
    }

    /// <summary>Try again: disposes what exists and runs the create sequence afresh (§3), focus to the loading frame.</summary>
    private async Task RetryAsync()
    {
        await DisposeMapAsync();
        _announceReady = true;
        _focusOutcome = true;
        _state = LocationMapState.Loading;
        _focusAfterRender = FocusTarget.Frame;
        StateHasChanged();
        await Task.Yield();
        await CreateAsync();
    }

    // ── Callbacks from location-map.js, each tagged with the generation it was created under (R2-F1) ──

    [JSInvokable]
    public async Task OnReady(int generation)
    {
        if (IsStale(generation) || _state != LocationMapState.Loading)
            return;

        _state = LocationMapState.Ready;
        var announce = _announceReady;
        _announceReady = false;
        if (_focusOutcome)
        {
            _focusOutcome = false;
            _focusAfterRender = FocusTarget.Surface;
        }

        StateHasChanged();
        if (announce)
            await AnnounceAsync($"Map of {Name} loaded.");
    }

    [JSInvokable]
    public Task OnFailed(int generation, string kind) => FailAsync(generation, LocationMapState.TilesFailed);

    [JSInvokable]
    public async Task OnDegraded(int generation)
    {
        if (IsStale(generation) || _state != LocationMapState.Ready)
            return;

        _state = LocationMapState.Degraded;
        StateHasChanged();
        await AnnounceAsync($"Map of {Name}: some tiles didn't load.");
    }

    /// <summary>30 s with no tile error ends the episode. Not announced (R2-F4, R2-X4).</summary>
    [JSInvokable]
    public Task OnRecovered(int generation)
    {
        if (!IsStale(generation) && _state == LocationMapState.Degraded)
        {
            _state = LocationMapState.Ready;
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>The WebGL context was lost and not restored within 3 s.</summary>
    [JSInvokable]
    public Task OnContextLost(int generation) => FailAsync(generation, LocationMapState.WebGlUnavailable);

    /// <summary>Restored within the window: the map is recreated silently (F4).</summary>
    [JSInvokable]
    public async Task OnContextRestored(int generation)
    {
        if (IsStale(generation))
            return;

        // The canvas is about to be replaced: if it (or the pin, a control) held focus, keep it in the
        // map — the frame while loading, then the new surface — rather than letting it fall to <body>.
        var hadFocus = false;
        if (_handle is { } handle && _module is not null)
        {
            try
            {
                hadFocus = await _module.InvokeAsync<bool>("hasFocus", handle);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
            {
            }
        }

        await DisposeMapAsync();
        _state = LocationMapState.Loading;
        if (hadFocus)
        {
            _focusOutcome = true;
            _focusAfterRender = FocusTarget.Frame;
        }

        StateHasChanged();
        await Task.Yield();
        await CreateAsync();
    }

    // ── Theme (§4): the latest toggle wins ───────────────────────────────────────────────────────────

    private void OnDarkModeChanged(bool dark) => _ = InvokeAsync(() => ApplyThemeAsync(dark));

    internal async Task ApplyThemeAsync(bool dark)
    {
        _dark = dark;
        var generation = ++_styleGeneration;
        if (_disposed || _handle is not { } handle || _module is null || _styles is null)
            return;

        var url = StyleUrlFor(dark);
        await SafeInvokeAsync(() => _module.InvokeVoidAsync("setStyle", handle, url));

        // A completion from a superseded toggle changes nothing.
        if (generation == _styleGeneration && !_disposed)
            _appliedStyleUrl = url;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    private async Task AnnounceAsync(string sentence)
    {
        if (!_disposed && OnStatus.HasDelegate)
            await OnStatus.InvokeAsync(sentence);
    }

    private async Task DisposeMapAsync()
    {
        var handle = _handle;
        _handle = null;
        await ReleaseAsync(handle);
    }

    private async Task ReleaseAsync(int? handle)
    {
        if (handle is { } h && _module is not null)
            await SafeInvokeAsync(() => _module.InvokeVoidAsync("dispose", h));
    }

    private async Task ReleaseModuleAsync()
    {
        var module = _module;
        _module = null;
        if (module is not null)
            await SafeInvokeAsync(() => module.DisposeAsync());
        _selfRef?.Dispose();
        _selfRef = null;
    }

    private static async Task SafeFocusAsync(ElementReference element)
    {
        try
        {
            await element.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // The element left the DOM before focus landed.
        }
    }

    private static async Task SafeInvokeAsync(Func<ValueTask> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException or ObjectDisposedException or JSException)
        {
            // Disposal and teardown races are expected here and are not app errors.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        Preferences.DarkModeChanged -= OnDarkModeChanged;
        await DisposeMapAsync();

        // A create still in flight owns the module until it returns: it disposes its own late handle and
        // then the module (CreateAsync's stale branch), so `dispose` still runs exactly once.
        if (!_createPending)
            await ReleaseModuleAsync();

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
