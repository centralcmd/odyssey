using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsVersionsDialog
{
    public const string HeadingId = "nw-version-h";

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private NewsStoreGate Gate { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public NewsArticleSummary Article { get; set; } = default!;

    /// <summary>The feed's language, the fallback when the article carries none.</summary>
    [Parameter] public string? FeedLanguage { get; set; }

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised with each announcement, for the page's one announcer.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    private enum Phase { Loading, Ready, Failed }

    private Phase _listPhase = Phase.Loading;
    private List<NewsArticleRevisionSummary> _revisions = [];
    private int? _selected;
    private Phase _bodyPhase = Phase.Loading;
    private NewsArticleRevisionDetail? _shown;
    private int _generation;
    private CancellationTokenSource? _cts;
    private bool _retrying;
    private bool _focusHeading;
    private bool _started;

    private string? Lang => NewsArticleCard.LangOf(Article.Language) ?? NewsArticleCard.LangOf(FeedLanguage);

    private NewsArticleRevisionSummary? Selected => _revisions.FirstOrDefault(r => r.RevisionNumber == _selected);

    /// <summary>The loaded body, only while it belongs to the selected version.</summary>
    private NewsArticleRevisionDetail? Shown => _shown?.RevisionNumber == _selected ? _shown : null;

    private string CountLine =>
        $"{NewsFeedback.Count(_revisions.Count, "earlier version", "earlier versions")}. Up to {NewsLimits.MaxRevisions} are kept, and the original is always kept.";

    /// <summary>Newest first, the Original last (§3.5).</summary>
    internal IReadOnlyList<OdsOption> Options =>
    [
        .. _revisions.OrderByDescending(r => r.RevisionNumber).Select(r => new OdsOption(
            r.RevisionNumber.ToString(CultureInfo.InvariantCulture),
            r.RevisionNumber == 1
                ? $"Original · first fetched {NewsArticleCard.Day(r.ValidFromUtc)}"
                : $"Version {r.RevisionNumber.ToString(CultureInfo.CurrentCulture)} · replaced {NewsArticleCard.Day(r.ReplacedAtUtc)}")),
    ];

    protected override async Task OnParametersSetAsync()
    {
        if (Open && !_started)
        {
            _started = true;
            await LoadListAsync();
        }
    }

    private async Task LoadListAsync()
    {
        _listPhase = Phase.Loading;
        var result = await News.ListRevisionsAsync(Article.Id);
        Gate.Observe(result);
        if (result.IsSuccess && result.Value is { } revisions)
        {
            _revisions = [.. revisions.OrderByDescending(r => r.RevisionNumber)];
            _listPhase = Phase.Ready;
            if (_revisions.Count > 0)
            {
                await SelectAsync(_revisions[0].RevisionNumber);
            }
        }
        else
        {
            _listPhase = Phase.Failed;
        }

        StateHasChanged();
    }

    private Task OnVersionChanged(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != _selected
            ? SelectAsync(number)
            : Task.CompletedTask;

    /// <summary>Selects a version and loads its body; a load still in flight for another version is cancelled.</summary>
    private async Task SelectAsync(int number)
    {
        _selected = number;
        await LoadBodyAsync(fromRetry: false);
    }

    private async Task LoadBodyAsync(bool fromRetry)
    {
        if (_selected is not { } number)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var generation = ++_generation;
        _bodyPhase = Phase.Loading;
        StateHasChanged();

        var result = await News.GetRevisionAsync(Article.Id, number, cts.Token);
        if (generation != _generation || _selected != number)
        {
            return; // superseded: discarded, never announced (AC 42)
        }

        var outcome = Gate.Observe(result);
        if (outcome == NewsStoreOutcome.Ok)
        {
            _shown = result.Value;
            _bodyPhase = Phase.Ready;
            _focusHeading = fromRetry;
            StateHasChanged();
            await OnAnnounce.InvokeAsync(_shown!.BodyOmittedReason is not null
                ? NewsArticleCard.OmittedText
                : $"Showing version {number.ToString(CultureInfo.CurrentCulture)}");
            return;
        }

        _bodyPhase = Phase.Failed;
        StateHasChanged();
    }

    private async Task RetryBodyAsync()
    {
        if (_retrying)
        {
            return;
        }

        _retrying = true;
        if (Gate.RemainingSeconds is { } wait)
        {
            await OnAnnounce.InvokeAsync($"Retrying in {wait.ToString(CultureInfo.CurrentCulture)} seconds");
            StateHasChanged();
            await Gate.WaitAsync();
        }

        _retrying = false;
        await LoadBodyAsync(fromRetry: true);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusHeading)
        {
            _focusHeading = false;
            try
            {
                await JS.InvokeVoidAsync("odsFocusById", HeadingId);
            }
            catch (JSException)
            {
                // The dialog closed meanwhile.
            }
        }
    }

    private Task Close() => OpenChanged.InvokeAsync(false);

    // Escape, the scrim and the × already raise OpenChanged; OnClose is set only so the modal offers them.
    private static void Closed()
    {
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
