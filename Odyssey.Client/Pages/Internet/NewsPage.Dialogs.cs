using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsPage
{
    // ── Earlier versions ───────────────────────────────────────────────────────────────────────────

    private NewsArticleSummary? _versionsArticle;
    private Guid _versionsKey;
    private bool _versionsOpen;

    private void OpenVersions(NewsArticleSummary article)
    {
        _versionsArticle = article;
        _versionsKey = Guid.NewGuid();
        _versionsOpen = true;
    }

    private void OnVersionsOpenChanged(bool open)
    {
        _versionsOpen = open;
        if (!open)
        {
            _versionsArticle = null;
        }
    }

    // ── Tags (§3.2.11) ─────────────────────────────────────────────────────────────────────────────

    private NewsArticleSummary? _tagsArticle;
    private Guid _tagsKey;
    private bool _tagsOpen;
    private IReadOnlyCollection<string> _tagsDraft = [];

    /// <summary>Tags minted from the article Tags field, under their temporary ids until Save resolves them.</summary>
    private readonly List<OdsOption> _createdTags = [];

    private IReadOnlyList<OdsOption> ArticleTagOptions(NewsArticleSummary article)
    {
        var known = NewsVisuals.TagPickerOptions(_tags, article.TagIds);
        return [.. _createdTags.Where(c => known.All(o => o.Value != c.Value)), .. known];
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
        _tagsDraft = [.. _tagsDraft.Where(id => id != tempId)];
        _ = InvokeAsync(StateHasChanged);
    }

    private string? BlockedTagName(string text) => NewsVisuals.BlockedTagName(_tags, _createdTags, text);
    private string? _tagsError;

    private IEnumerable<ExistingNewsTag> InheritedTagsOf(NewsArticleSummary article) =>
        article.FeedTagIds.Select(id => _tagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private void OpenTags(NewsArticleSummary article)
    {
        if (!_canUpdate)
        {
            return;
        }

        _tagsArticle = article;
        _tagsDraft = [.. article.TagIds.Select(id => id.ToString())];
        _tagsError = null;
        _createdTags.Clear();
        TagCreator.OnCreateFailed = OnTagCreateFailed;
        _tagsKey = Guid.NewGuid();
        _tagsOpen = true;
    }

    private void OnTagsOpenChanged(bool open)
    {
        _tagsOpen = open;
        if (!open)
        {
            _tagsArticle = null;
        }
    }

    /// <summary>One <c>PUT …/tags</c> per Save — removing a single own tag included; never a per-tag DELETE.</summary>
    private async Task<bool> SaveArticleTagsAsync()
    {
        if (_tagsArticle is not { } article)
        {
            return false;
        }

        if (_tagsDraft.Count > NewsLimits.MaxTagIdsPerRequest)
        {
            _tagsError = $"Choose at most {NewsLimits.MaxTagIdsPerRequest} tags.";
            return false;
        }

        if (_createdTags.Count > 0)
        {
            // Inline-created tags hand the picker a temporary id; wait for their POSTs, then map to the real ids.
            await TagCreator.WhenSettledAsync();
            _tagsDraft = [.. _tagsDraft.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];
        }

        var ids = _tagsDraft.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var result = await News.SetArticleTagsAsync(article.Id, ids);
        if (result.IsSuccess)
        {
            if (ids.Any(id => !_tagsById.ContainsKey(id)))
            {
                // A tag created inline: the chips, the Tag filter and the next dialog need it.
                SetTags((await Catalogue.GetTagsAsync()).ItemsOrToast(Snackbar, "news tags"));
            }

            article.TagIds = ids;
            Say($"Tags saved for {NewsArticleCard.TitleOf(article)}");
            _tagsArticle = null;
            StateHasChanged();
            await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(article.Id), RootId);
            return true;
        }

        if (NewsFeedback.Is(result.Problem, NewsErrorCodes.TagNotUsable) || NewsFeedback.Is(result.Problem, NewsErrorCodes.TagArchived))
        {
            _tagsError = "One of these tags is archived.";
            return false;
        }

        if (result.Status == HttpStatusCode.NotFound)
        {
            _tagsOpen = false;
            _tagsArticle = null;
            await RemoveCardAsync(article);
            return true;
        }

        Say(NewsFeedback.Failure(Snackbar, result.Problem, "Couldn’t save the tags"));
        return false;
    }

    // ── Mark all read (§3.2.9) ─────────────────────────────────────────────────────────────────────

    private Guid _markAllKey;
    private bool _markAllOpen;
    private bool _marking;
    private bool _markAllOutOfDate;
    private string _markAllProgress = string.Empty;
    private int? _markAllCount;
    private NewsMarkAllRun? _run;

    private void OpenMarkAll()
    {
        if (!_canUpdate || _listFilter is null)
        {
            return;
        }

        _markAllCount = MarkAllCount();
        _markAllOutOfDate = false;
        _markAllProgress = string.Empty;
        _markAllKey = Guid.NewGuid();
        _markAllOpen = true;
    }

    /// <summary>
    /// The exact count, shown only when one is known (§3.2.9, AC 31): Status is Unread, no search, the total is not
    /// capped, and the selection is no feed and no tag (the overall total), exactly one feed alone, or exactly one tag
    /// alone. A feed or tag the counts omit — an archived feed — shows no number rather than 0.
    /// </summary>
    internal int? MarkAllCount()
    {
        if (_listFilter is not { } filter || filter.Statuses is not { Count: 1 } statuses || !statuses.Contains(NewsArticleReadStatus.Unread) || filter.Search is not null || _totalCapped || _counts is not { } counts)
        {
            return null;
        }

        var feeds = filter.FeedIds?.Count ?? 0;
        var tags = filter.TagIds?.Count ?? 0;
        return (feeds, tags) switch
        {
            (0, 0) => counts.Total,
            (1, 0) => counts.ByFeed.FirstOrDefault(f => f.FeedId == filter.FeedIds!.First())?.UnreadCount,
            (0, 1) => counts.ByTag.FirstOrDefault(t => t.TagId == filter.TagIds!.First())?.UnreadCount,
            _ => null,
        };
    }

    /// <summary>"Mark 42 unread articles in ‹Example News› as read?" — the scope named in words, names emphasised.</summary>
    private RenderFragment MarkAllSentence => builder =>
    {
        var filter = _listFilter ?? new NewsArticleListFilter();
        var feeds = (filter.FeedIds ?? []).Select(id => _feedsById.GetValueOrDefault(id)).OfType<NewsFeedCatalogueEntry>().ToList();
        var tags = (filter.TagIds ?? []).Select(id => _tagsById.GetValueOrDefault(id)?.Name).OfType<string>().ToList();
        var seq = 0;

        void Text(string text) => builder.AddContent(seq++, text);

        void Em(string text, string? lang)
        {
            builder.OpenElement(seq++, "em");
            builder.AddAttribute(seq++, "lang", lang);
            builder.OpenElement(seq++, "bdi");
            builder.AddContent(seq++, text);
            builder.CloseElement();
            builder.CloseElement();
        }

        Text(_markAllCount is { } count
            ? $"Mark {count.ToString("N0", CultureInfo.CurrentCulture)} unread {(count == 1 ? "article" : "articles")} "
            : "Mark all unread articles ");

        if (feeds.Count == 0 && tags.Count == 0)
        {
            Text("in all feeds");
        }
        else if (feeds.Count == 1)
        {
            Text("in ");
            Em(feeds[0].DisplayTitle, NewsArticleCard.LangOf(feeds[0].Language));
        }
        else if (feeds.Count > 1 && tags.Count > 0)
        {
            Text($"from {feeds.Count.ToString("N0", CultureInfo.CurrentCulture)} feeds");
        }
        else if (feeds.Count > 1)
        {
            Text("in ");
            Em(feeds[0].DisplayTitle, NewsArticleCard.LangOf(feeds[0].Language));
            Text($" and {NewsFeedback.Count(feeds.Count - 1, "other feed", "other feeds")}");
        }

        if (tags.Count > 0)
        {
            Text(feeds.Count > 1 ? ", tagged " : feeds.Count == 1 ? " tagged " : "tagged ");
            for (var i = 0; i < tags.Count; i++)
            {
                if (i > 0)
                {
                    Text(i == tags.Count - 1 ? " or " : ", ");
                }

                Em(tags[i], null);
            }
        }

        if (filter.Search is { } search)
        {
            Text(" matching ‘");
            builder.OpenElement(seq++, "bdi");
            builder.AddContent(seq++, search);
            builder.CloseElement();
            Text("’");
        }

        Text(" as read?");
    };

    private async Task MarkAllAsync()
    {
        if (_marking || _markAllOutOfDate || _listFilter is not { } filter || _asOf is not { } asOf)
        {
            return;
        }

        _marking = true;
        var run = _run = new NewsMarkAllRun(News, Time);
        StateHasChanged();
        var result = await run.RunAsync(filter, asOf, total =>
        {
            _markAllProgress = $"Marking… {total.ToString("N0", CultureInfo.CurrentCulture)} so far";
            StateHasChanged();
            return Task.CompletedTask;
        });
        _marking = false;
        _run = null;
        _markAllProgress = $"Marking… {result.Total.ToString("N0", CultureInfo.CurrentCulture)} so far";

        if (result.End == NewsMarkAllEnd.OutOfDate)
        {
            _markAllOutOfDate = true;
            StateHasChanged();
            return;
        }

        _markAllOpen = false;
        var marked = $"{NewsFeedback.Count(result.Total, "article", "articles")} marked as read.";
        switch (result.End)
        {
            case NewsMarkAllEnd.Failed:
                Say(NewsFeedback.Failure(Snackbar, result.Problem, "Couldn’t mark the articles as read"));
                StateHasChanged();
                await InvokeJsAsync("odsNewsFocusListStart", RootId);
                return;
            case NewsMarkAllEnd.Finished:
                Say(NewsFeedback.Success(Snackbar, marked));
                break;
            case NewsMarkAllEnd.Stopped:
                var stopped = $"{marked} Stopped before finishing.";
                Snackbar.Add(stopped, Severity.Info, config => config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds);
                Say(NewsFeedback.Spoken(stopped));
                break;
            default:
                const string partial = "Some articles were marked as read; try again to finish.";
                Snackbar.Add(partial, Severity.Error, config => config.RequireInteraction = true);
                Say(NewsFeedback.Spoken(partial));
                break;
        }

        await RefreshAfterMarkingAsync();
        await ResetAsync(focusListStart: true);
    }

    private async Task RefreshAfterMarkingAsync()
    {
        Catalogue.Invalidate();
        var catalogueTask = Catalogue.GetCatalogueAsync();
        SetCounts(await News.GetUnreadCountsAsync());
        var catalogue = await catalogueTask;
        if (catalogue.IsSuccess && catalogue.Value is { } entries)
        {
            SetCatalogue(entries);
        }
    }

    /// <summary>Cancel and Escape stay available while marking: they stop further calls after the one in flight.</summary>
    private void CancelMarkAll()
    {
        _run?.Stop();
        _markAllOpen = false;
    }

    private void OnMarkAllOpenChanged(bool open)
    {
        if (!open)
        {
            CancelMarkAll();
        }
    }

    // Escape and the scrim already raise OpenChanged; OnClose is set only so the modal offers them.
    private static void MarkAllClosed()
    {
    }

    private async Task RefreshAfterOutOfDateAsync()
    {
        _markAllOpen = false;
        await RefreshAfterMarkingAsync();
        await ResetAsync(focusListStart: true);
    }
}
