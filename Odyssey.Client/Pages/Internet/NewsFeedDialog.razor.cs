using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
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
    public const string KindId = "nf-form-kind";
    public const string DaysId = "nf-form-days";
    public const string AutoDownloadHelpId = "nf-form-autodownload-help";
    public const string AutoDeleteHelpId = "nf-form-autodelete-help";
    public const string ImagesHelpId = "nf-form-images-help";

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private ITagQuickCreate<ExistingNewsTag> TagCreator { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The feed to edit, or <see langword="null"/> for New feed. Its <c>management</c> must be present.</summary>
    [Parameter] public ExistingNewsFeed? Feed { get; set; }

    /// <summary>The news tags, for the Tags picker.</summary>
    [Parameter] public IReadOnlyList<ExistingNewsTag> Tags { get; set; } = [];

    /// <summary><c>news-tags.create</c>: the Tags field offers an inline "Create ‘…’ · News tag" row.</summary>
    [Parameter] public bool CanCreateTags { get; set; }

    /// <summary>Raised with the server's feed after a create or a save — the row shows the server's <c>url</c>.</summary>
    [Parameter] public EventCallback<ExistingNewsFeed> Saved { get; set; }

    /// <summary>"Go to feed" on a duplicate: the existing feed’s id.</summary>
    [Parameter] public EventCallback<Guid> GoToFeed { get; set; }

    /// <summary>Raised when a Reload or a save finds the feed deleted.</summary>
    [Parameter] public EventCallback<Guid> Deleted { get; set; }

    /// <summary>
    /// Opens Edit already in the conflict state: a <b>Use new address</b> that met <c>409 news-feed-changed</c> follows
    /// the Reload flow (§3.3), with <see cref="InitialUrl"/> — the suggested address — as the draft.
    /// </summary>
    [Parameter] public bool InitialConflict { get; set; }

    /// <summary>The URL draft to open with instead of the feed’s own; <see langword="null"/> uses the feed’s.</summary>
    [Parameter] public string? InitialUrl { get; set; }

    /// <summary>Raised when a Reload brings a newer copy of the feed, so the page's row matches.</summary>
    [Parameter] public EventCallback<ExistingNewsFeed> Refreshed { get; set; }

    /// <summary>New feed only: the Type the dialog opens with — <c>Podcast</c> from the Podcasts page (issue #559 §3.4).</summary>
    [Parameter] public NewsFeedKind InitialKind { get; set; } = NewsFeedKind.Article;

    internal static readonly IReadOnlyList<OdsRadioOption> KindOptions =
    [
        new(nameof(NewsFeedKind.Article), "Articles") { Description = "News and blog posts" },
        new(nameof(NewsFeedKind.Podcast), "Podcast") { Description = "Episodes with downloadable audio" },
    ];

    private enum Conflict { None, Changed, ReloadFailed, Deleted, Reloaded }

    private bool IsEdit => Feed is not null;

    private bool IsPodcast => _kind == NewsFeedKind.Podcast;

    private string DialogTitle => IsEdit
        ? (_base?.Kind == NewsFeedKind.Podcast ? "Edit podcast" : "Edit feed")
        : (InitialKind == NewsFeedKind.Podcast ? "New podcast" : "New feed");

    private OdsRadioGroup? _kindGroup;
    private NewsFeedKind _kind = NewsFeedKind.Article;
    private bool _autoDownload;
    private bool _autoDelete;
    private decimal? _autoDays = PodcastLimits.DefaultAutoDeleteDays;
    private bool _daysTouched;
    private string? _kindError;
    private string? _daysError;
    private string? _automationError;
    private Guid _switchBackKey;
    private bool _switchBackOpen;
    private bool _switchBackSaved;
    private bool _switchBackFailed;
    private int? _switchBackCount;

    private static string Range => string.Create(System.Globalization.CultureInfo.CurrentCulture,
        $"{PodcastLimits.MinAutoDeleteDays:N0} to {PodcastLimits.MaxAutoDeleteDays:N0}");

    /// <summary>
    /// "1 to 3,650 days." from <see cref="PodcastLimits"/>, plus — on edit, when removal is turned on or the number
    /// changes — what the change does to audio already saved to a device (§3.4).
    /// </summary>
    internal string DaysHelp
    {
        get
        {
            var help = $"{Range} days.";
            var before = _base?.Podcast;
            var changed = IsEdit && _autoDelete
                && (before is null || !before.AutoDeleteEnabled || (_daysTouched && (int?)_autoDays != before.AutoDeleteAfterDays));
            return changed ? $"{help} Audio already saved to a device is removed after this many days from today." : help;
        }
    }

    internal string SwitchBackText => _switchBackCount is > 0
        ? $"Audio for {NewsFeedback.Count(_switchBackCount.Value, "episode", "episodes")} will be removed from the server. Episodes and their show notes are kept."
        : "Episodes and their show notes are kept.";

    /// <summary>The day count's client-side check, mirroring the server (§8): empty, not whole, out of range.</summary>
    internal static string? ValidateDays(decimal? days) =>
        days is not { } value ? "Enter the number of days."
        : value != decimal.Truncate(value) ? "Enter a whole number of days."
        : value < PodcastLimits.MinAutoDeleteDays || value > PodcastLimits.MaxAutoDeleteDays ? $"Enter a number from {Range}."
        : null;

    private void OnKindChanged(string value)
    {
        _kind = Enum.TryParse<NewsFeedKind>(value, out var kind) ? kind : NewsFeedKind.Article;
        _kindError = null;
        _automationError = null;
    }

    private void OnAutoDeleteChanged(bool value)
    {
        _autoDelete = value;
        _automationError = null;
        if (!value)
        {
            // Off: the field is not rendered and its last value is kept, sent unchanged and never validated.
            _daysError = null;
        }
    }

    private void OnDaysChanged(decimal? value)
    {
        _autoDays = value;
        _daysTouched = true;
        _daysError = null;
    }

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

    /// <summary>Tags minted from the field in this dialog, under their temporary ids until the save resolves them.</summary>
    private List<OdsOption> _createdTags = [];

    private IReadOnlyList<OdsOption> TagOptions
    {
        get
        {
            var known = NewsVisuals.TagPickerOptions(Tags, _tagIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty));
            return [.. _createdTags.Where(c => known.All(o => o.Value != c.Value)), .. known];
        }
    }

    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
        {
            _createdTags.Add(option);
            StateHasChanged();
        }

        return option;
    }

    private void OnTagCreateFailed(string tempId)
    {
        _createdTags.RemoveAll(o => o.Value == tempId);
        _tagIds = [.. _tagIds.Where(id => id != tempId)];
        _ = InvokeAsync(StateHasChanged);
    }

    // A news tag name is unique across active and archived tags.
    private string? BlockedTagName(string text) => NewsVisuals.BlockedTagName(Tags, _createdTags, text);

    /// <summary>Waits for the inline creates and swaps each temporary id for the real one (a failed create drops out).</summary>
    private async Task ResolveCreatedTagsAsync()
    {
        if (_createdTags.Count == 0)
        {
            return;
        }

        await TagCreator.WhenSettledAsync();
        _tagIds = [.. _tagIds.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];
        _createdTags = TagCreator.ResolveCreated(_createdTags);
    }

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

    protected override void OnInitialized() => TagCreator.OnCreateFailed = OnTagCreateFailed;

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
        _tagIds = feed is null ? [] : [.. feed.TagIds.Select(id => id.ToString())];
        _kind = feed?.Kind is NewsFeedKind.Podcast or NewsFeedKind.Article ? feed.Kind : feed is null ? InitialKind : NewsFeedKind.Article;
        _autoDownload = feed?.Podcast?.AutoDownload ?? false;
        _autoDelete = feed?.Podcast?.AutoDeleteEnabled ?? false;
        _autoDays = feed?.Podcast?.AutoDeleteAfterDays ?? PodcastLimits.DefaultAutoDeleteDays;
        _daysTouched = false;
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
        _daysError = IsPodcast && _autoDelete ? ValidateDays(_autoDays) : null;
        if (FirstInvalidField() is { } invalid)
        {
            StateHasChanged();
            await FocusAsync(invalid);
            return false;
        }

        // Podcast → Articles is ALWAYS confirmed (§3.4, sec F2), with the stored-audio count from a fresh read.
        if (Feed is { } editing && (_base ?? editing).Kind == NewsFeedKind.Podcast && _kind == NewsFeedKind.Article)
        {
            var fresh = await News.GetFeedAsync(editing.Id);
            _switchBackCount = fresh.IsSuccess ? fresh.Value?.Podcast?.StoredAudioCount : null;
            _switchBackSaved = false;
            _switchBackFailed = false;
            _switchBackKey = Guid.NewGuid();
            _switchBackOpen = true;
            StateHasChanged();
            return false;
        }

        return await SaveAsync();
    }

    /// <summary>Confirm: the save runs while the confirmation is open; the form dialog closes when it succeeds.</summary>
    private async Task<string?> ConfirmSwitchBackAsync()
    {
        _switchBackSaved = await SaveAsync();
        if (_switchBackSaved)
        {
            await OpenChanged.InvokeAsync(false);
        }
        else
        {
            // The save failed: the confirmation closes onto the form, which shows why, with Articles still selected.
            _switchBackFailed = true;
        }

        return null;
    }

    /// <summary>
    /// Cancel (or Escape) reverts Type to Podcast — nothing was saved — and focuses that checked radio. A confirmed save
    /// that failed reverts nothing: the reader chose Articles and the form's error says why it did not save.
    /// </summary>
    private async Task OnSwitchBackOpenChanged(bool open)
    {
        _switchBackOpen = open;
        if (open || _switchBackSaved || _switchBackFailed)
        {
            return;
        }

        _kind = NewsFeedKind.Podcast;
        StateHasChanged();
        if (_kindGroup is { } group)
        {
            await group.FocusAsync(frames: 2);
        }
    }

    private async Task<bool> SaveAsync()
    {
        await ResolveCreatedTagsAsync();
        var url = _url!.Trim();
        var title = string.IsNullOrWhiteSpace(_title) ? null : _title.Trim();
        var interval = _interval is { } minutes ? (int?)(int)minutes : null;

        ApiResult<ExistingNewsFeed> result;
        if (Feed is { } feed)
        {
            // The feed as the server last returned it: the type and the podcast automation are sent back unchanged.
            var current = _base ?? feed;
            result = await News.UpdateFeedAsync(feed.Id, new UpdateNewsFeed
            {
                Version = current.Version,
                Url = url,
                CustomTitle = title,
                FetchIntervalMinutes = interval,
                DownloadImages = _downloadImages,
                IsActive = _active,
                TagIds = [.. _tagIds.Select(Guid.Parse)],
                // A PUT is a full replacement (issue #556 §5.1 #2): all four are always sent. With Articles selected
                // the automation goes as off — the server refuses it on an article feed — and the day count as kept.
                Kind = _kind,
                AutoDownload = IsPodcast && _autoDownload,
                AutoDeleteEnabled = IsPodcast && _autoDelete,
                AutoDeleteAfterDays = DaysToSend(current),
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
                Kind = _kind,
                AutoDownload = IsPodcast && _autoDownload,
                AutoDeleteEnabled = IsPodcast && _autoDelete,
                AutoDeleteAfterDays = DaysToSend(null),
            });
        }

        if (result.IsSuccess && result.Value is { } saved)
        {
            if (Feed is not null && (_base?.Kind ?? Feed.Kind) == NewsFeedKind.Article && saved.Kind == NewsFeedKind.Podcast)
            {
                NewsFeedback.Success(Snackbar, "Saved as a podcast. Episodes appear after the next check.");
            }

            await Saved.InvokeAsync(saved);
            return true;
        }

        await ShowFailureAsync(result);
        return false;
    }

    /// <summary>The kept day count: a valid draft, else the feed's own, else the default (a hidden field is never validated).</summary>
    private int DaysToSend(ExistingNewsFeed? current) =>
        ValidateDays(_autoDays) is null && _autoDays is { } days ? (int)days
        : current?.Podcast?.AutoDeleteAfterDays ?? PodcastLimits.DefaultAutoDeleteDays;

    private string? FirstInvalidField() =>
        _urlError is not null ? UrlId
        : _titleError is not null ? TitleFieldId
        : _kindError is not null ? KindId
        : _daysError is not null ? DaysId
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
        else if (problem?.ErrorFor("kind") is { } kindError)
        {
            _kindError = kindError;
        }
        else if (problem?.ErrorFor("autoDeleteAfterDays") is { } daysError)
        {
            _daysError = daysError;
        }
        else if (NewsFeedback.Is(problem, NewsErrorCodes.FeedAutomationNotPodcast)
            || problem?.ErrorFor("autoDownload") is not null || problem?.ErrorFor("autoDeleteEnabled") is not null)
        {
            // The server wins on the switch it names (§7): shown in the Podcast settings group.
            _automationError = problem?.ErrorFor("autoDownload") ?? problem?.ErrorFor("autoDeleteEnabled") ?? problem!.Message;
            if (!IsPodcast)
            {
                NewsFeedback.Failure(Snackbar, problem, "Couldn’t save the feed");
                return;
            }
        }
        else
        {
            NewsFeedback.Failure(Snackbar, problem, IsEdit ? "Couldn’t save the feed" : "Couldn’t create the feed");
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

        // Not a field here: the dialog only carries the server's value back unchanged.
        _active = server.IsActive;

        if (before.Kind != server.Kind)
        {
            _kind = server.Kind is NewsFeedKind.Podcast ? NewsFeedKind.Podcast : NewsFeedKind.Article;
            updated.Add("Type");
        }

        var a = before.Podcast;
        var b = server.Podcast;
        if ((a?.AutoDownload ?? false) != (b?.AutoDownload ?? false)
            || (a?.AutoDeleteEnabled ?? false) != (b?.AutoDeleteEnabled ?? false)
            || (a?.AutoDeleteAfterDays ?? PodcastLimits.DefaultAutoDeleteDays) != (b?.AutoDeleteAfterDays ?? PodcastLimits.DefaultAutoDeleteDays))
        {
            _autoDownload = b?.AutoDownload ?? false;
            _autoDelete = b?.AutoDeleteEnabled ?? false;
            _autoDays = b?.AutoDeleteAfterDays ?? PodcastLimits.DefaultAutoDeleteDays;
            _daysError = null;
            updated.Add("Podcast settings");
        }

        if (!before.TagIds.Order().SequenceEqual(server.TagIds.Order()))
        {
            _tagIds = [.. server.TagIds.Select(id => id.ToString())];
            _tagsError = null;
            updated.Add("Tags");
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
