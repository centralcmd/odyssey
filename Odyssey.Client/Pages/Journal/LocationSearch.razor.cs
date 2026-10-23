using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>A picked candidate, with the response's attribution so the provenance card can credit it.</summary>
public sealed record LocationPick(LocationCandidate Candidate, LocationAttribution Attribution);

public partial class LocationSearch : IDisposable
{
    internal enum SearchState { Idle, Short, Long, Loading, Done, Failed }

    [Inject] private ILocationsApiClient Locations { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public LocationSearchRegion Region { get; set; } = LocationSearchRegion.Norway;
    [Parameter] public EventCallback<LocationSearchRegion> RegionChanged { get; set; }

    [Parameter] public EventCallback<LocationPick> OnPick { get; set; }

    /// <summary>Offered beside "Search worldwide" on no matches; create only.</summary>
    [Parameter] public EventCallback OnManual { get; set; }

    /// <summary>The design's 350 ms; a test passes zero.</summary>
    [Parameter] public int DebounceMilliseconds { get; set; } = 350;

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private string _query = string.Empty;
    private SearchState _state = SearchState.Idle;
    private LocationSearchResponse? _response;
    private HttpStatusCode _failure;
    private int _active = -1;
    private string _announce = string.Empty;
    private int _seq;
    private CancellationTokenSource? _debounce;
    private LocationSearchRegion? _lastRegion;
    private bool _escapeHooked;

    private string InputId => $"loc-q-{_uid}";
    private string ListId => $"loc-list-{_uid}";
    private string HelpId => $"loc-help-{_uid}";
    private string OptionId(int index) => $"{ListId}-{index}";

    private LocationRegionInfo RegionInfo => LocationVisuals.RegionOf(Region);
    private LocationRegionInfo OtherRegion => LocationVisuals.Regions.First(r => r.Region != Region);
    private string ProviderLabel => LocationVisuals.SourceOf(RegionInfo.Source).Label;

    private IReadOnlyList<LocationCandidate> Candidates => _state == SearchState.Done ? _response?.Candidates ?? [] : [];
    private bool IsOpen => Candidates.Count > 0;

    private static readonly IReadOnlyList<OdsSegmentedOption> RegionOptions =
    [
        .. LocationVisuals.Regions.Select(r => new OdsSegmentedOption { Value = r.Region.ToString(), Label = r.Label, Icon = r.Icon }),
    ];

    // Per-user and instance-wide limits read the same: the client cannot tell them apart.
    private string FailureText => _failure == HttpStatusCode.TooManyRequests
        ? "You’ve searched a lot in the last minute. Wait a moment, then try again — or enter the place by hand."
        : $"{ProviderLabel} isn’t answering right now. Try again in a moment, or enter the place by hand.";

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// What is sent: trimmed, whitespace collapsed, NFC — the server's normalization minus the lower-casing,
    /// so the length checked here is the length its <c>[StringLength]</c> sees.
    /// </summary>
    internal static string Clean(string? query) =>
        Whitespace().Replace((query ?? string.Empty).Normalize(NormalizationForm.FormC).Trim(), " ");

    protected override void OnParametersSet()
    {
        // Switching region re-runs the current query against the newly selected provider only.
        if (_lastRegion is { } last && last != Region)
            Schedule(immediate: true);
        _lastRegion = Region;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_escapeHooked)
            return;
        _escapeHooked = true;
        try
        { await Js.InvokeVoidAsync("odsEscapeClearsInput", InputId); }
        catch (JSException) { /* the dialog's own Esc still closes it */ }
    }

    private async Task OnRegionChanged(string value)
    {
        if (!Enum.TryParse<LocationSearchRegion>(value, out var region) || region == Region)
            return;
        Region = region;
        _lastRegion = region;
        await RegionChanged.InvokeAsync(region);
        Schedule(immediate: true);
    }

    private void OnInput(ChangeEventArgs e)
    {
        _query = e.Value?.ToString() ?? string.Empty;
        Schedule(immediate: false);
    }

    private async Task ClearAsync()
    {
        _query = string.Empty;
        Schedule(immediate: false);
        try
        { await Js.InvokeVoidAsync("odsFocusById", InputId); }
        catch (JSException) { }
    }

    private void Schedule(bool immediate)
    {
        _debounce?.Cancel();
        _debounce = null;

        var cleaned = Clean(_query);
        var normalizedLength = LocationRules.NormalizeQuery(_query).Length;
        SearchState? settled = normalizedLength == 0 ? SearchState.Idle
            : normalizedLength < RegionInfo.MinLength ? SearchState.Short
            : cleaned.Length > LocationRules.QueryMaxLength ? SearchState.Long
            : null;
        if (settled is { } state)
        {
            // Anything still in flight is now stale.
            _seq++;
            _state = state;
            _active = -1;
            return;
        }

        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = RunAfterDelayAsync(immediate ? 0 : DebounceMilliseconds, cts.Token);
    }

    private async Task RunAfterDelayAsync(int delay, CancellationToken ct)
    {
        try
        {
            if (delay > 0)
                await Task.Delay(delay, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested)
            await InvokeAsync(RunAsync);
    }

    private async Task RunAsync()
    {
        var mine = ++_seq;
        var region = Region;
        _state = SearchState.Loading;
        _active = -1;
        StateHasChanged();

        var result = await Locations.SearchAsync(new LocationSearchRequest { Query = Clean(_query), Region = region });
        if (mine != _seq)
            return;

        var provider = ProviderLabel;
        if (result.IsSuccess && result.Value is { } body)
        {
            _response = body;
            _state = SearchState.Done;
            var n = body.Candidates.Count;
            _announce = n > 0
                ? $"{n} {(n == 1 ? "result" : "results")} from {provider}{(body.Partial ? ", some may be missing" : "")}."
                : $"No matches {RegionInfo.Phrase}.";
        }
        else
        {
            // No provider text is ever shown for a failure — the copy is the page's own.
            _response = null;
            _failure = result.Status;
            _state = SearchState.Failed;
            _announce = _failure == HttpStatusCode.TooManyRequests
                ? "Too many searches. Wait a moment, then try again."
                : $"{provider} isn’t answering right now.";
        }

        StateHasChanged();
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (!IsOpen)
            return;
        switch (e.Key)
        {
            case "ArrowDown":
                _active = Math.Min(_active + 1, Candidates.Count - 1);
                break;
            case "ArrowUp":
                _active = Math.Max(_active - 1, 0);
                break;
            case "Enter":
                await PickAsync(Candidates[_active < 0 ? 0 : _active]);
                break;
        }
    }

    private Task PickAsync(LocationCandidate candidate) =>
        _response is { } response ? OnPick.InvokeAsync(new LocationPick(candidate, response.Attribution)) : Task.CompletedTask;

    public void Dispose()
    {
        _seq++;
        _debounce?.Cancel();
    }
}
