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

    /// <summary>Focus the input on first render — the dialog's search step, or a re-search in edit.</summary>
    [Parameter] public bool AutoFocus { get; set; }

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
    private bool _keysHooked;
    private int _announceNonce;

    private string InputId => $"loc-q-{_uid}";
    private string ListId => $"loc-list-{_uid}";
    private string HelpId => $"loc-help-{_uid}";
    private string StateId => $"loc-state-{_uid}";

    // The too-short and too-long lines describe the input while they show, alongside the data-flow line.
    private string DescribedBy => _state is SearchState.Short or SearchState.Long ? $"{HelpId} {StateId}" : HelpId;

    private string ShortText => $"Type at least {RegionInfo.MinLength} characters to search {RegionInfo.Phrase}.";
    private string LongText => $"Searches are limited to {LocationRules.QueryMaxLength} characters. Shorten the text to search.";
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
        if (_keysHooked)
            return;
        _keysHooked = true;
        try
        {
            await Js.InvokeVoidAsync("odsComboboxKeys", InputId);
            if (AutoFocus)
                await Js.InvokeVoidAsync("odsFocusById", InputId);
        }
        catch (JSException) { /* the dialog's own Esc still closes it */ }
    }

    /// <summary>
    /// Sets the live-region text so that a repeat of the same sentence is still announced: an unchanged
    /// string renders nothing new, so a run of zero-width spaces alternates behind it.
    /// </summary>
    private void Say(string message)
    {
        _announceNonce = (_announceNonce + 1) % 4;
        _announce = message + new string('\u200B', _announceNonce + 1);
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
        _debounce?.Dispose();
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
            if (state != _state && state is SearchState.Short or SearchState.Long)
                Say(state == SearchState.Short ? ShortText : LongText);
            _state = state;
            _active = -1;
            return;
        }

        // The previous query's results stop being pickable the moment the text changes.
        _seq++;
        _state = SearchState.Loading;
        _active = -1;

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
            Say(n > 0
                ? $"{n} {(n == 1 ? "result" : "results")} from {provider}{(body.Partial ? ", some may be missing" : "")}."
                : $"No matches {RegionInfo.Phrase}.");
        }
        else
        {
            // No provider text is ever shown for a failure — the copy is the page's own.
            _response = null;
            _failure = result.Status;
            _state = SearchState.Failed;
            Say(_failure == HttpStatusCode.TooManyRequests
                ? "Too many searches. Wait a moment, then try again."
                : $"{provider} isn’t answering right now.");
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
                await RevealActiveAsync();
                break;
            case "ArrowUp":
                _active = Math.Max(_active - 1, 0);
                await RevealActiveAsync();
                break;
            case "Enter":
                await PickAsync(Candidates[_active < 0 ? 0 : _active]);
                break;
        }
    }

    // The list scrolls past six rows, so the keyboard-active option is brought into view; a mouse hover
    // is already in view and moves nothing.
    private async Task RevealActiveAsync()
    {
        try
        { await Js.InvokeVoidAsync("odsScrollIntoViewById", OptionId(_active)); }
        catch (JSException) { }
    }

    private Task PickAsync(LocationCandidate candidate) =>
        _response is { } response ? OnPick.InvokeAsync(new LocationPick(candidate, response.Attribution)) : Task.CompletedTask;

    public void Dispose()
    {
        _seq++;
        _debounce?.Cancel();
        _debounce?.Dispose();
    }
}
