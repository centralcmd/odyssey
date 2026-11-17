using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// The OPML import dialog on the Feeds page (issue #509 §3.2–§3.4, §7.2, §8, amendments v4 and v5; Odyssey Design
/// System · ui_kits/web/ImportNewsFeedsModal.jsx). One file, one request, then the report the server returned.
/// </summary>
public partial class ImportNewsFeedsDialog : IDisposable
{
    public const string PickerId = "news-opml-file";
    public const string ImportButtonId = "news-opml-import";
    public const string ResultHeadingId = "news-opml-result-h";

    /// <summary>When <b>Stop waiting</b> appears while a request is in flight (§3.2, S7).</summary>
    public static readonly TimeSpan StopWaitingAfter = TimeSpan.FromSeconds(30);

    private const long BytesPerMegabyte = 1L << 20;

    internal const string NoFileText = "Choose an OPML file to import.";
    internal const string WrongTypeText = "That isn't an OPML file. Use a .opml or .xml file.";
    internal const string UnreadableText = "This file isn't an OPML document, or part of it couldn't be read. Nothing was imported.";
    internal const string ConflictText = "The feed list changed while importing. No feeds were imported, but new tags may have been added. Try again.";
    internal const string BusyText = "News is busy, and the import may or may not have finished. Close to check the Feeds list, or try again.";
    internal const string RateLimitedText = "Another import is running. Try again in a moment.";
    internal const string ForbiddenText = "You don't have permission to import feeds.";
    internal const string DroppedText = "The upload didn't finish, and the import may or may not have run. Close to check the Feeds list, or try again.";
    internal const string OtherText = "Couldn't import the file.";
    internal const string StoppedText = "Stopped waiting. The import may still finish on the server. Check the Feeds list after closing.";
    internal const string ImportingText = "Importing feeds…";
    internal const string StillImportingText = "Still importing. You can stop waiting.";

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private IImportLimitsCache ImportLimits { get; set; } = default!;

    [Inject] private IClaimsRefresher ClaimsRefresher { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The element focus returns to on close: the Feeds page's ⋯ menu root (§3.3 step 5).</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>
    /// Fires once the dialog has closed, with whether any request was sent (§3.3 step 6, F5): tags or feeds may have
    /// been committed even when the dialog showed a failure, so the page reloads after any sent request.
    /// </summary>
    [Parameter] public EventCallback<bool> Closed { get; set; }

    internal enum Phase { Compose, Importing, Rejected, Stopped, Result }

    private enum FocusTarget { None, Picker, Import, Heading }

    private Phase _phase = Phase.Compose;
    private IReadOnlyList<OdsUploadFile> _files = [];
    private string? _fieldError;
    private string? _alert;
    private NewsOpmlImportResult? _result;
    private string _fileName = "the file";
    private bool _slow;
    private bool _sent;
    private bool _wasOpen;
    private int _generation;
    private CancellationTokenSource? _request;
    private ITimer? _slowTimer;
    private FocusTarget _focus;
    private OdsLiveAnnouncer? _announcer;
    private readonly HashSet<string> _openGroups = new(StringComparer.Ordinal);

    private int _maxMegabytes = ImportLimitsCache.Fallback.NewsOpmlMaxImportMegabytes;
    private int _maxEntries = ImportLimitsCache.Fallback.NewsOpmlMaxImportEntries;

    private bool Importing => _phase == Phase.Importing;

    internal Phase CurrentPhase => _phase;

    private string Hint =>
        $"OPML (.opml or .xml) · one file · up to {Number(_maxMegabytes)} MB · up to {Number(_maxEntries)} feeds in the file";

    private string? AlertText => _phase switch
    {
        Phase.Stopped => StoppedText,
        Phase.Rejected => _alert,
        _ => null,
    };

    protected override async Task OnParametersSetAsync()
    {
        var opening = Open && !_wasOpen;
        _wasOpen = Open;
        if (!opening)
        {
            return;
        }

        _focus = FocusTarget.Picker;
        await ReadLimitsAsync();
    }

    private async Task ReadLimitsAsync()
    {
        var limits = await ImportLimits.GetAsync();
        _maxMegabytes = limits.NewsOpmlMaxImportMegabytes;
        _maxEntries = limits.NewsOpmlMaxImportEntries;
    }

    private long MaxBytes => _maxMegabytes * BytesPerMegabyte;

    // ── Compose ────────────────────────────────────────────────────────────────────────────────────

    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        _files = files;
        _fieldError = null;
        if (_phase is Phase.Rejected or Phase.Stopped)
        {
            _phase = Phase.Compose;
            _alert = null;
        }
    }

    private void FieldError(string text)
    {
        _fieldError = text;
        _alert = null;
        _phase = Phase.Compose;
        _focus = FocusTarget.Picker;
    }

    private async Task SubmitAsync()
    {
        if (Importing)
        {
            return;
        }

        var file = _files.FirstOrDefault()?.Source;
        if (file is null)
        {
            FieldError(NoFileText);
            return;
        }

        if (!file.Name.EndsWith(".opml", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            FieldError(WrongTypeText);
            return;
        }

        if (file.Size > MaxBytes)
        {
            // The cached limit may be stale (a raise in another tab): re-read it once before refusing (S4).
            ImportLimits.Invalidate();
            await ReadLimitsAsync();
            if (file.Size > MaxBytes)
            {
                FieldError(TooLargeText);
                return;
            }
        }

        _fieldError = null;
        _alert = null;
        _slow = false;
        _phase = Phase.Importing;
        _sent = true;
        var generation = ++_generation;
        _request?.Dispose();
        _request = new CancellationTokenSource();
        StartSlowTimer();
        Say(ImportingText);
        StateHasChanged();

        ApiResult<NewsOpmlImportResult> outcome;
        try
        {
            outcome = await News.ImportOpmlAsync(file.ToApiUpload(MaxBytes), _request.Token);
        }
        catch (Exception ex)
        {
            // A browser that cannot buffer the file fails here (§8.6); the transport catches the rest.
            outcome = ApiResult<NewsOpmlImportResult>.Failure(ex);
        }

        if (generation != _generation)
        {
            // Stopped meanwhile: this answer belongs to a request the user stopped waiting for.
            return;
        }

        StopSlowTimer();
        if (outcome.IsSuccess && outcome.Value is { } result)
        {
            _fileName = string.IsNullOrWhiteSpace(file.Name) ? "the file" : file.Name;
            _result = result;
            _openGroups.Clear();
            _phase = Phase.Result;
            _focus = FocusTarget.Heading;
            Say(Summary(result));
            return;
        }

        await FailedAsync(outcome.Status, outcome.Problem);
    }

    private string TooLargeText => $"That file is too large for this server. The import limit is {Number(_maxMegabytes)} MB.";

    /// <summary>§7.2: by code where there is one, by status otherwise. The chosen file stays chosen in every case.</summary>
    private async Task FailedAsync(HttpStatusCode status, ApiProblem? problem)
    {
        var code = problem?.Code;
        if (Is(code, NewsErrorCodes.OpmlFileRequired))
        {
            FieldError(NoFileText);
            return;
        }

        if (Is(code, NewsErrorCodes.OpmlWrongType))
        {
            FieldError(WrongTypeText);
            return;
        }

        if (status == HttpStatusCode.RequestEntityTooLarge)
        {
            FieldError(TooLargeText);
            return;
        }

        string text;
        if (Is(code, NewsErrorCodes.OpmlUnreadable))
        {
            text = UnreadableText;
        }
        else if (Is(code, NewsErrorCodes.OpmlTooManyEntries))
        {
            var limit = problem!.TryGetExtensionValue<int>("limit", out var stated) && stated > 0 ? stated : _maxEntries;
            text = $"This file lists more than {Number(limit)} feeds, the most one import accepts. Nothing was imported.";
        }
        else if (Is(code, NewsErrorCodes.OpmlConflict))
        {
            text = ConflictText;
        }
        else if (status == HttpStatusCode.ServiceUnavailable)
        {
            text = BusyText;
        }
        else if (status == HttpStatusCode.TooManyRequests)
        {
            text = RateLimitedText;
        }
        else if (status == HttpStatusCode.Forbidden)
        {
            text = ForbiddenText;
        }
        else if (status == default)
        {
            text = DroppedText;
        }
        else
        {
            // The detail only when the body was a problem document: a non-JSON body leaves it unset (S6).
            text = string.IsNullOrWhiteSpace(problem?.Detail) ? OtherText : $"{OtherText} {problem.Detail}";
        }

        _alert = text;
        _phase = Phase.Rejected;
        _focus = FocusTarget.Import;
        if (status == HttpStatusCode.Forbidden)
        {
            // The page redraws its menu on the provider's AuthenticationStateChanged (§7.3); the dialog stays open.
            await ClaimsRefresher.OnResponseAsync(status);
        }
    }

    private static bool Is(string? code, string expected) => string.Equals(code, expected, StringComparison.Ordinal);

    // ── Stop waiting ───────────────────────────────────────────────────────────────────────────────

    private void StartSlowTimer()
    {
        StopSlowTimer();
        _slowTimer = Time.CreateTimer(_ => _ = InvokeAsync(OnSlow), null, StopWaitingAfter, Timeout.InfiniteTimeSpan);
    }

    private void StopSlowTimer()
    {
        _slowTimer?.Dispose();
        _slowTimer = null;
    }

    private void OnSlow()
    {
        if (!Importing || _slow)
        {
            return;
        }

        _slow = true;
        Say(StillImportingText);
        StateHasChanged();
    }

    private async Task StopWaitingAsync()
    {
        if (!Importing)
        {
            return;
        }

        _generation++;
        StopSlowTimer();
        _request?.Cancel();
        // Focus moves to Import BEFORE Stop waiting is removed, so it never drops to <body> (v4 §3).
        await FocusAsync(FocusTarget.Import);
        _slow = false;
        _phase = Phase.Stopped;
        _focus = FocusTarget.Import;
    }

    // ── Result ─────────────────────────────────────────────────────────────────────────────────────

    private void ToggleGroup(string id)
    {
        if (!_openGroups.Remove(id))
        {
            _openGroups.Add(id);
        }
    }

    private void ImportAnother()
    {
        ClearReport();
        _files = [];
        _fieldError = null;
        _alert = null;
        _phase = Phase.Compose;
        _focus = FocusTarget.Picker;
    }

    private void ClearReport()
    {
        // The report can hold 2,000 rows with URLs: it is dropped as soon as it is no longer shown (v4 §10).
        _result = null;
        _openGroups.Clear();
        _fileName = "the file";
    }

    private async Task CloseAsync()
    {
        if (Importing)
        {
            return;
        }

        var sent = _sent;
        ClearReport();
        _files = [];
        _fieldError = null;
        _alert = null;
        _phase = Phase.Compose;
        _sent = false;
        _focus = FocusTarget.None;
        await OpenChanged.InvokeAsync(false);
        await Closed.InvokeAsync(sent);
    }

    // ── Report text ────────────────────────────────────────────────────────────────────────────────

    internal static int NotImported(NewsOpmlImportResult r) => r.Invalid + r.PendingDeletion + r.FeedLimit;

    /// <summary>The one announcement of a result (§6.5): counts only, never a report value (S2).</summary>
    internal static string Summary(NewsOpmlImportResult r)
    {
        if (r.Feeds == 0)
        {
            return "This file doesn't list any feeds.";
        }

        var counts = $"{Number(r.Created)} added, {Number(r.AlreadySubscribed)} already subscribed, {Number(NotImported(r))} not imported.";
        return r.Created == 0 ? $"No feeds were added. {counts}" : counts;
    }

    internal sealed record OutcomeGroup(string Id, string Reason, int Count, IReadOnlyList<NewsOpmlImportItem> Rows, bool Neutral, string Icon, bool WithMessage);

    /// <summary>The groups in §3.4's order; a group whose count is 0 is left out. Each count is the server's counter.</summary>
    internal static IReadOnlyList<OutcomeGroup> Groups(NewsOpmlImportResult r)
    {
        List<NewsOpmlImportItem> Of(NewsOpmlImportOutcome outcome) => [.. r.Items.Where(i => i.Outcome == outcome)];
        var unknown = r.Items.Where(i => !Enum.IsDefined(i.Outcome) || i.Outcome == NewsOpmlImportOutcome.Unknown).ToList();
        OutcomeGroup[] all =
        [
            new("news-opml-g-created", "Added feeds", r.Created, Of(NewsOpmlImportOutcome.Created), true, "add_circle_outline", false),
            new("news-opml-g-existing", "Already subscribed", r.AlreadySubscribed, Of(NewsOpmlImportOutcome.AlreadySubscribed), true, "bookmark_border", false),
            new("news-opml-g-invalid", "Address can't be used", r.Invalid, Of(NewsOpmlImportOutcome.Invalid), false, "playlist_remove", true),
            new("news-opml-g-limit", "Over the feed limit", r.FeedLimit, Of(NewsOpmlImportOutcome.FeedLimit), false, "playlist_remove", false),
            new("news-opml-g-deleting", "Being deleted", r.PendingDeletion, Of(NewsOpmlImportOutcome.PendingDeletion), false, "playlist_remove", false),
            new("news-opml-g-unknown", "Not imported", unknown.Count, unknown, false, "playlist_remove", false),
        ];
        return [.. all.Where(g => g.Count > 0)];
    }

    internal static string TagReason(NewsOpmlTagSkipReason reason) => reason switch
    {
        NewsOpmlTagSkipReason.NoPermission => "You can't create tags",
        NewsOpmlTagSkipReason.TagLimit => "The tag limit is reached",
        NewsOpmlTagSkipReason.NameTooLong => $"Name too long (over {NewsLimits.MaxTagNameLength} characters)",
        NewsOpmlTagSkipReason.Archived => "The tag is archived",
        _ => "Not added",
    };

    internal static string MergedNote(int merged) => merged == 1
        ? "1 repeated entry for the same feed was combined. An Odyssey export lists a feed once per tag."
        : $"{Number(merged)} repeated entries for the same feed were combined. An Odyssey export lists a feed once per tag.";

    internal static string DroppedNote =>
        $"Some feeds named more than {NewsLimits.MaxTagIdsPerRequest} tags; the extra ones were left off.";

    /// <summary>
    /// A report URL as text with its user-info masked (§3.4, v4 §4), from a parsed <see cref="Uri"/>. <c>null</c> when
    /// the URL does not parse as an absolute address: the row then says "(address hidden)" rather than the raw text.
    /// </summary>
    internal static MaskedUrl? Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            // On Unix a bare "/path" parses as a file URI; only an address that names its scheme is one.
            || (uri.IsFile && !url.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return string.IsNullOrEmpty(uri.UserInfo)
            ? new MaskedUrl(url, null)
            : new MaskedUrl($"{uri.Scheme}://", $"@{uri.Authority}{uri.PathAndQuery}{uri.Fragment}");
    }

    /// <summary>A URL for display: <see cref="Head"/> alone, or the scheme, the masked user-info, then <see cref="Tail"/>.</summary>
    internal sealed record MaskedUrl(string Head, string? Tail)
    {
        public bool Masked => Tail is not null;
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ── Focus and announcements ────────────────────────────────────────────────────────────────────

    private void Say(string text) => _ = _announcer?.Announce(text);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focus == FocusTarget.None || !Open)
        {
            return;
        }

        var target = _focus;
        _focus = FocusTarget.None;
        await FocusAsync(target);
    }

    private async Task FocusAsync(FocusTarget target)
    {
        try
        {
            switch (target)
            {
                case FocusTarget.Picker:
                    // After the re-render (and after the modal's own open-edge focus), the dropzone button.
                    await JS.InvokeVoidAsync("odsFocusFirstIn", PickerId, false, 2);
                    break;
                case FocusTarget.Import:
                    await JS.InvokeVoidAsync("odsFocusById", ImportButtonId);
                    break;
                case FocusTarget.Heading:
                    await JS.InvokeVoidAsync("odsFocusById", ResultHeadingId);
                    break;
            }
        }
        catch (JSException)
        {
            // Not rendered (a concurrent re-render): only the focus courtesy is lost.
        }
    }

    public void Dispose()
    {
        _generation++;
        StopSlowTimer();
        _request?.Cancel();
        _request?.Dispose();
    }
}
