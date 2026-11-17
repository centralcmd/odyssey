using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsFeedDialog
{
    public const string UrlId = "nf-form-url";
    public const string TitleFieldId = "nf-form-title";
    public const string IntervalId = "nf-form-interval";
    public const string ConflictId = "nf-form-conflict";
    public const string ReloadId = "nf-form-reload";

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The feed to edit, or <see langword="null"/> for New feed. Its <c>management</c> must be present.</summary>
    [Parameter] public ExistingNewsFeed? Feed { get; set; }

    /// <summary>The news tags, for the create-only Tags picker.</summary>
    [Parameter] public IReadOnlyList<ExistingNewsTag> Tags { get; set; } = [];

    /// <summary>Raised with the server's feed after a create or a save — the row shows the server's <c>url</c>.</summary>
    [Parameter] public EventCallback<ExistingNewsFeed> Saved { get; set; }

    /// <summary>"Go to feed" on a duplicate: the existing feed's id.</summary>
    [Parameter] public EventCallback<Guid> GoToFeed { get; set; }

    /// <summary>Raised when a Reload or a save finds the feed deleted.</summary>
    [Parameter] public EventCallback<Guid> Deleted { get; set; }

    /// <summary>
    /// Opens Edit already in the conflict state: a <b>Use new address</b> that met <c>409 news-feed-changed</c> follows
    /// the Reload flow (§3.3), with <see cref="InitialUrl"/> — the suggested address — as the draft.
    /// </summary>
    [Parameter] public bool InitialConflict { get; set; }

    /// <summary>The URL draft to open with instead of the feed's own; <see langword="null"/> uses the feed's.</summary>
    [Parameter] public string? InitialUrl { get; set; }

    /// <summary>Raised when a Reload brings a newer copy of the feed, so the page's row matches.</summary>
    [Parameter] public EventCallback<ExistingNewsFeed> Refreshed { get; set; }

    private enum Conflict { None, Changed, ReloadFailed, Deleted, Reloaded }

    private bool IsEdit => Feed is not null;

    private ExistingNewsFeed? _base;
    private Guid _seededFor = Guid.Empty;
    private bool _seeded;

    private string? _url;
    private string? _title;
    private decimal? _interval;
    private bool _downloadImages = true;
    private bool _active = true;
    private IReadOnlyCollection<string> _tagIds = [];

    private string? _urlError;
    private string? _titleError;
    private string? _intervalError;
    private string? _tagsError;
    private string? _formAlert;
    private Guid? _duplicateOf;

    private Conflict _conflict = Conflict.None;
    private IReadOnlyList<string> _updatedFields = [];
    private bool _reloading;

    private IReadOnlyList<OdsOption> TagOptions =>
        NewsVisuals.TagPickerOptions(Tags, _tagIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty));

    internal string ReloadedText => _updatedFields.Count == 0
        ? "Reloaded. Nothing changed on the server."
        : $"Reloaded. Updated from the server: {string.Join(", ", _updatedFields)}.";

    protected override void OnParametersSet()
    {
        // Seeded once per opening: a re-render with the same feed never discards what the reader typed.
        var key = Feed?.Id ?? Guid.Empty;
        if (_seeded && key == _seededFor)
        {
            return;
        }

        _seeded = true;
        _seededFor = key;
        _base = Feed;
        Seed(Feed);
        if (InitialUrl is { } url)
        {
            _url = url;
        }

        if (InitialConflict && Feed is not null)
        {
            _conflict = Conflict.Changed;
            _focusConflict = true;
        }
    }

    private bool _focusConflict;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConflict && Open)
        {
            _focusConflict = false;
            await FocusAsync(ConflictId);
        }
    }

    private void Seed(ExistingNewsFeed? feed)
    {
        _url = feed?.Management?.Url ?? string.Empty;
        _title = feed?.CustomTitle ?? string.Empty;
        _interval = feed?.FetchIntervalMinutes;
        _downloadImages = feed?.DownloadImages ?? true;
        _active = feed?.IsActive ?? true;
        _tagIds = [];
    }

    private void OnUrlChanged(string? value) { _url = value; _urlError = null; _duplicateOf = null; }

    private void OnTitleChanged(string? value) { _title = value; _titleError = null; }

    private void OnIntervalChanged(decimal? value) { _interval = value; _intervalError = null; }

    private void OnTagsChanged(IReadOnlyCollection<string> values) { _tagIds = values ?? []; _tagsError = null; }

    private async Task<bool> SubmitAsync()
    {
        if (_conflict == Conflict.Deleted)
        {
            return false;
        }

        _formAlert = null;
        _urlError = NewsFeedForm.ValidateUrl(_url);
        _titleError = NewsFeedForm.ValidateTitle(_title);
        _intervalError = NewsFeedForm.ValidateInterval(_interval);
        _tagsError = _tagIds.Count > NewsLimits.MaxTagIdsPerRequest
            ? $"Choose at most {NewsLimits.MaxTagIdsPerRequest} tags."
            : null;
        if (FirstInvalidField() is { } invalid)
        {
            StateHasChanged();
            await FocusAsync(invalid);
            return false;
        }

        var url = _url!.Trim();
        var title = string.IsNullOrWhiteSpace(_title) ? null : _title.Trim();
        var interval = _interval is { } minutes ? (int?)(int)minutes : null;

        ApiResult<ExistingNewsFeed> result;
        if (Feed is { } feed)
        {
            result = await News.UpdateFeedAsync(feed.Id, new UpdateNewsFeed
            {
                Version = (_base ?? feed).Version,
                Url = url,
                CustomTitle = title,
                FetchIntervalMinutes = interval,
                DownloadImages = _downloadImages,
                IsActive = _active,
            });
        }
        else
        {
            result = await News.CreateFeedAsync(new NewNewsFeed
            {
                Url = url,
                CustomTitle = title,
                FetchIntervalMinutes = interval,
                DownloadImages = _downloadImages,
                TagIds = _tagIds.Count == 0 ? null : [.. _tagIds.Select(Guid.Parse)],
            });
        }

        if (result.IsSuccess && result.Value is { } saved)
        {
            await Saved.InvokeAsync(saved);
            return true;
        }

        await ShowFailureAsync(result);
        return false;
    }

    private string? FirstInvalidField() =>
        _urlError is not null ? UrlId
        : _titleError is not null ? TitleFieldId
        : _intervalError is not null ? IntervalId
        : null;

    private async Task ShowFailureAsync(ApiResult<ExistingNewsFeed> result)
    {
        var problem = result.Problem;
        if (NewsFeedback.Is(problem, NewsErrorCodes.FeedChanged))
        {
            _conflict = Conflict.Changed;
            StateHasChanged();
            await FocusAsync(ConflictId);
            return;
        }

        if (result.Status == HttpStatusCode.NotFound && Feed is { } gone)
        {
            await MarkDeletedAsync(gone.Id);
            return;
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.FeedDuplicate))
        {
            _urlError = "You already follow this feed.";
            _duplicateOf = problem!.TryGetExtensionValue<Guid>("existingFeedId", out var existing) && existing != Guid.Empty ? existing : null;
        }
        else if (NewsFeedback.Is(problem, NewsErrorCodes.FeedLimit))
        {
            _formAlert = "The limit has been reached. An administrator can raise it in Settings.";
        }
        else if (NewsFeedback.Is(problem, NewsErrorCodes.TagNotUsable))
        {
            _tagsError = "One of these tags is archived.";
        }
        else if (problem?.ErrorFor("url") is { } urlError)
        {
            // The server's message wins over any client result (§7.2).
            _urlError = urlError;
        }
        else if (NewsFeedback.Is(problem, NewsErrorCodes.FeedUrlRejected))
        {
            _urlError = problem!.Message;
        }
        else if (problem?.ErrorFor("customTitle") is { } titleError)
        {
            _titleError = titleError;
        }
        else if (problem?.ErrorFor("fetchIntervalMinutes") is { } intervalError)
        {
            _intervalError = intervalError;
        }
        else if (problem?.ErrorFor("tagIds") is { } tagsError)
        {
            _tagsError = tagsError;
        }
        else
        {
            NewsFeedback.Failure(Snackbar, problem, IsEdit ? "Couldn't save the feed" : "Couldn't create the feed");
            return;
        }

        StateHasChanged();
        if (FirstInvalidField() is { } field)
        {
            await FocusAsync(field);
        }
    }

    private async Task ReloadAsync()
    {
        if (Feed is not { } feed || _reloading)
        {
            return;
        }

        _reloading = true;
        StateHasChanged();
        var result = await News.GetFeedAsync(feed.Id);
        _reloading = false;

        if (result.Status == HttpStatusCode.NotFound)
        {
            await MarkDeletedAsync(feed.Id);
            return;
        }

        if (!result.IsSuccess || result.Value is not { } server)
        {
            _conflict = Conflict.ReloadFailed;
            StateHasChanged();
            await FocusAsync(ReloadId);
            return;
        }

        var updated = Merge(_base ?? feed, server);
        _base = server;
        _updatedFields = updated;
        _conflict = Conflict.Reloaded;
        await Refreshed.InvokeAsync(server);
        StateHasChanged();
        await FocusAsync(FieldIdOf(updated.FirstOrDefault()) ?? UrlId);
    }

    /// <summary>
    /// Folds the server's copy into the draft: a field the server changed since the read WINS, whether or not the
    /// reader changed it too; a field only the reader changed keeps their value. Returns the names of the fields the
    /// server updated, in form order.
    /// </summary>
    private List<string> Merge(ExistingNewsFeed before, ExistingNewsFeed server)
    {
        var updated = new List<string>();
        if ((before.Management?.Url ?? string.Empty) != (server.Management?.Url ?? string.Empty))
        {
            _url = server.Management?.Url ?? string.Empty;
            _urlError = null;
            updated.Add("Feed URL");
        }

        if ((before.CustomTitle ?? string.Empty) != (server.CustomTitle ?? string.Empty))
        {
            _title = server.CustomTitle ?? string.Empty;
            _titleError = null;
            updated.Add("Title");
        }

        if (before.FetchIntervalMinutes != server.FetchIntervalMinutes)
        {
            _interval = server.FetchIntervalMinutes;
            _intervalError = null;
            updated.Add("Interval");
        }

        if (before.DownloadImages != server.DownloadImages)
        {
            _downloadImages = server.DownloadImages;
            updated.Add("Download images");
        }

        if (before.IsActive != server.IsActive)
        {
            _active = server.IsActive;
            updated.Add("Active");
        }

        return updated;
    }

    private static string? FieldIdOf(string? name) => name switch
    {
        "Feed URL" => UrlId,
        "Title" => TitleFieldId,
        "Interval" => IntervalId,
        _ => null,
    };

    private async Task MarkDeletedAsync(Guid id)
    {
        _conflict = Conflict.Deleted;
        StateHasChanged();
        await Deleted.InvokeAsync(id);
        try
        {
            await JS.InvokeVoidAsync("odsFocusDialogCancel");
        }
        catch (JSException)
        {
        }
    }

    private async Task FocusAsync(string id)
    {
        try
        {
            await JS.InvokeVoidAsync("odsFocusById", id);
        }
        catch (JSException)
        {
            // Not rendered yet: only the focus courtesy is lost.
        }
    }
}
