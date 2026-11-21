using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsPage
{
    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expand: one card open at a time; the detail loads on the first expand ("Loading article…" said once) and is
    /// kept; an unread card is marked read straight away for a <c>news.update</c> holder (§3.2.8). Focus stays on the
    /// card header.
    /// </summary>
    private async Task OnToggleAsync(NewsArticleSummary article, bool open)
    {
        _openId = open ? article.Id : (_openId == article.Id ? null : _openId);
        var tasks = new List<Task>();
        // Leaving an Updated-since-read card (closing it, or opening another) counts as reading the new version:
        // ReadAtUtc moves to now and the flag clears. Not on open, so the chip and the alert stay while it is read.
        if (_rereadId is { } rereadId && rereadId != _openId)
        {
            _rereadId = null;
            if (_rows.FirstOrDefault(r => r.Id == rereadId) is { } left && NewsArticleCard.UpdatedSinceRead(left))
            {
                tasks.Add(WriteReadAsync(left, true, ReadSource.Reread));
            }
        }

        if (!open)
        {
            await Task.WhenAll(tasks);
            return;
        }

        if (article.IsRead && _canUpdate && NewsArticleCard.UpdatedSinceRead(article))
        {
            _rereadId = article.Id;
        }

        var state = _details.GetValueOrDefault(article.Id);
        if (state is null || (state.Phase is NewsBodyPhase.Error or NewsBodyPhase.StorageUnavailable && !state.Retrying))
        {
            Say("Loading article…");
            tasks.Add(LoadDetailAsync(article));
        }
        else if (state.Detail is { } detail)
        {
            AnnounceBodyState(detail);
        }

        if (!article.IsRead && _canUpdate)
        {
            tasks.Add(WriteReadAsync(article, true, ReadSource.Expand));
        }

        await Task.WhenAll(tasks);
    }

    private async Task LoadDetailAsync(NewsArticleSummary article)
    {
        _details[article.Id] = new NewsArticleDetailState(NewsBodyPhase.Loading);
        StateHasChanged();
        var result = await News.GetArticleAsync(article.Id);
        if (!_rows.Any(r => r.Id == article.Id))
        {
            return;
        }

        var outcome = Gate.Observe(result);
        if (outcome == NewsStoreOutcome.Ok)
        {
            _details[article.Id] = new NewsArticleDetailState(NewsBodyPhase.Ready, result.Value);
            if (_openId == article.Id)
            {
                AnnounceBodyState(result.Value!);
            }
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            await RemoveCardAsync(article);
            return;
        }
        else
        {
            _details[article.Id] = new NewsArticleDetailState(
                outcome == NewsStoreOutcome.StorageUnavailable ? NewsBodyPhase.StorageUnavailable : NewsBodyPhase.Error);
        }

        StateHasChanged();
    }

    /// <summary>Body.Unavailable and Body.Omitted are said once by the page announcer as the card expands (§3.2.7).</summary>
    private void AnnounceBodyState(NewsArticleDetail detail)
    {
        if (detail.BodyUnavailable)
        {
            Say(_canReadSettings
                ? $"{NewsArticleCard.UnavailableText} Ask an administrator to check the Background jobs page."
                : NewsArticleCard.UnavailableText);
        }
        else if (detail.BodyOmittedReason is not null)
        {
            Say(NewsArticleCard.OmittedText);
        }
    }

    /// <summary>Retry on Body.Error: pressed before Retry-After allows it, it waits the rest out and says so (§7.3).</summary>
    private async Task RetryDetailAsync(NewsArticleSummary article)
    {
        if (_details.GetValueOrDefault(article.Id) is not { Retrying: false } state)
        {
            return;
        }

        if (Gate.RemainingSeconds is { } wait)
        {
            _details[article.Id] = state with { Retrying = true };
            Say($"Retrying in {wait.ToString(CultureInfo.CurrentCulture)} seconds");
            StateHasChanged();
            await Gate.WaitAsync();
        }

        await LoadDetailAsync(article);
        if (_details.GetValueOrDefault(article.Id)?.Phase == NewsBodyPhase.Ready)
        {
            // Retry is gone with the error: focus back on the card header rather than <body>.
            await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(article.Id), RootId);
        }
    }

    private async Task RemoveCardAsync(NewsArticleSummary article)
    {
        var next = Neighbour(article.Id);
        _rows = [.. _rows.Where(r => r.Id != article.Id)];
        _details.Remove(article.Id);
        if (_openId == article.Id)
        {
            _openId = null;
        }

        Say(NewsFeedback.Gone(Snackbar));
        StateHasChanged();
        await (next is { } id
            ? InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(id), RootId)
            : InvokeJsAsync("odsNewsFocusListStart", RootId));
    }

    private Guid? Neighbour(Guid id)
    {
        var index = _rows.FindIndex(r => r.Id == id);
        return index < 0 ? null : index + 1 < _rows.Count ? _rows[index + 1].Id : index > 0 ? _rows[index - 1].Id : null;
    }

    // ── The ⋯ menu (§3.2.10) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mark read / Mark unread · Tags… (both <c>news.update</c>) · Show earlier versions (when the publisher changed
    /// it) · Show articles from ‹feed› · then the link items, which the Original link tile's own ⋯ menu repeats.
    /// </summary>
    internal IReadOnlyList<OdsMenuItem> MenuFor(NewsArticleSummary article)
    {
        var title = NewsArticleCard.TitleOf(article);
        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(article.IsRead
                ? Item("mark_email_unread", "Mark unread", title, () => WriteReadAsync(article, false, ReadSource.Menu))
                : Item("mark_email_read", "Mark read", title, () => WriteReadAsync(article, true, ReadSource.Menu)));
            items.Add(Item("local_offer", "Tags…", title, () => OpenTags(article)));
        }

        if (article.RevisionCount > 0)
        {
            items.Add(Item("history", "Show earlier versions", title, () => OpenVersions(article)));
        }

        var feedLabel = $"Show articles from {article.FeedTitle}";
        items.Add(new OdsMenuItem
        {
            Icon = "rss_feed",
            Label = feedLabel,
            AriaLabel = feedLabel,
            OnClick = EventCallback.Factory.Create(this, () => ShowFeedAsync(article.FeedId)),
        });
        items.AddRange(LinkMenuFor(article));
        return items;
    }

    /// <summary>
    /// Open original — only for a safe URL (OdsExternalLink.Target: absolute http/https, no user info), opening exactly
    /// that href in a new tab with no opener and no referrer — and Copy link, whenever there is a link.
    /// </summary>
    internal IReadOnlyList<OdsMenuItem> LinkMenuFor(NewsArticleSummary article)
    {
        var items = new List<OdsMenuItem>();
        if (OdsExternalLink.Target(article.Link) is { } target)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "open_in_new",
                Label = "Open original",
                AriaLabel = "Open original (opens in a new tab)",
                OnClick = EventCallback.Factory.Create(this, () => InvokeJsAsync("odsNewsOpenOriginal", target.AbsoluteUri)),
            });
        }

        if (!string.IsNullOrWhiteSpace(article.Link))
        {
            var link = article.Link;
            items.Add(new OdsMenuItem
            {
                Icon = "link",
                Label = "Copy link",
                TrailingIcon = "content_copy",
                OnClick = EventCallback.Factory.Create(this, () => CopyLinkAsync(link)),
            });
        }

        return items;
    }

    /// <summary>The shared clipboard service reports the result in a snackbar, success and failure alike.</summary>
    private Task CopyLinkAsync(string link) => Clipboard.CopyAsync(link, LinkCopiedText);

    private OdsMenuItem Item(string icon, string label, string title, Func<Task> action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {title}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    private OdsMenuItem Item(string icon, string label, string title, Action action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {title}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    /// <summary>Show articles from ‹feed›: the deep link, applied here and written to the URL (§3.1).</summary>
    private Task ShowFeedAsync(Guid feedId)
    {
        _handledFeedQuery = feedId;
        _handledTagQuery = null;
        _search = string.Empty;
        _feedFilter = [feedId.ToString()];
        _tagFilter = [];
        if (_sort.Key == RelevanceKey)
        {
            _sort = DefaultSort;
        }

        Nav.NavigateTo($"news?feedId={feedId}");
        return ResetAsync();
    }

    // ── Read state (§3.2.8, §10) ───────────────────────────────────────────────────────────────────

    /// <summary>What asked for the write. <see cref="Reread"/> is leaving an updated-since-read card: silent.</summary>
    internal enum ReadSource { Expand, Menu, Undo, Reread }

    private readonly Dictionary<Guid, bool> _wanted = [];
    private readonly HashSet<Guid> _writing = [];
    private readonly Dictionary<Guid, (bool IsRead, DateTime? ReadAt)> _confirmed = [];
    private ITimer? _countsTimer;
    private Snackbar? _undoSnackbar;

    /// <summary>
    /// An optimistic read-state write: the chip and the counts change at once; writes are serialised per article and
    /// the last one wins; a failure rolls the article and the counts back and says "Couldn't update. Changed back."
    /// without moving focus. Counts are re-read a second after the last write.
    /// </summary>
    private async Task WriteReadAsync(NewsArticleSummary article, bool isRead, ReadSource source)
    {
        var id = article.Id;
        if (!_confirmed.ContainsKey(id))
        {
            _confirmed[id] = (article.IsRead, article.ReadAtUtc);
        }

        ApplyLocally(article, isRead, isRead ? Time.GetUtcNow().UtcDateTime : null);
        if (isRead)
        {
            _justRead.Add(id);
        }

        _wanted[id] = isRead;
        switch (source)
        {
            case ReadSource.Expand:
                OfferUndo(article);
                Say(NewsFeedback.Spoken(OfferedUndoText));
                break;
            case ReadSource.Reread:
                break; // the state does not change, so there is nothing to say
            default:
                Say(isRead ? "Marked as read" : "Marked as unread");
                break;
        }

        StateHasChanged();
        if (!_writing.Add(id))
        {
            return; // the write in flight picks the new wish up when it returns
        }

        try
        {
            while (true)
            {
                var want = _wanted[id];
                var result = await News.SetReadAsync(id, want);
                if (!result.IsSuccess)
                {
                    if (result.Status == HttpStatusCode.NotFound)
                    {
                        await RemoveCardAsync(article);
                        return;
                    }

                    var (confirmedRead, confirmedAt) = _confirmed[id];
                    ApplyLocally(article, confirmedRead, confirmedAt);
                    var text = NewsFeedback.IsBusy(result.Problem) ? NewsFeedback.BusyText : ChangedBackText;
                    Snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true);
                    Say(NewsFeedback.Spoken(text));
                    StateHasChanged();
                    return;
                }

                _confirmed[id] = (want, article.ReadAtUtc);
                if (_wanted[id] == want)
                {
                    return;
                }
            }
        }
        finally
        {
            _writing.Remove(id);
            _confirmed.Remove(id);
            ScheduleCountsRefresh();
        }
    }

    private void ApplyLocally(NewsArticleSummary article, bool isRead, DateTime? readAt)
    {
        if (article.IsRead != isRead)
        {
            AdjustCounts(article, isRead ? -1 : 1);
        }

        article.IsRead = isRead;
        article.ReadAtUtc = isRead ? readAt : null;
    }

    /// <summary>Moves the unread figures by one for an optimistic change; never below 0.</summary>
    private void AdjustCounts(NewsArticleSummary article, int delta)
    {
        if (_counts is { } counts)
        {
            counts.Total = Math.Max(0, counts.Total + delta);
            foreach (var feed in counts.ByFeed.Where(f => f.FeedId == article.FeedId))
            {
                feed.UnreadCount = Math.Max(0, feed.UnreadCount + delta);
            }

            var tags = article.TagIds.Concat(article.FeedTagIds).ToHashSet();
            foreach (var tag in counts.ByTag.Where(t => tags.Contains(t.TagId)))
            {
                tag.UnreadCount = Math.Max(0, tag.UnreadCount + delta);
            }
        }

        if (_feedsById.TryGetValue(article.FeedId, out var entry))
        {
            entry.UnreadCount = Math.Max(0, entry.UnreadCount + delta);
        }
    }

    private void ScheduleCountsRefresh()
    {
        _countsTimer?.Dispose();
        _countsTimer = Time.CreateTimer(_ => _ = InvokeAsync(async () =>
        {
            SetCounts(await News.GetUnreadCountsAsync());
            StateHasChanged();
        }), null, CountsDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>One "Marked as read" snackbar with Undo for 8 s; the newest replaces any earlier one (§3.2.8).</summary>
    private void OfferUndo(NewsArticleSummary article)
    {
        if (_undoSnackbar is { } previous)
        {
            Snackbar.Remove(previous);
        }

        var id = article.Id;
        _undoSnackbar = Snackbar.Add(OfferedUndoText, Severity.Normal, config =>
        {
            config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds;
            config.Action = "Undo";
            config.ActionColor = Color.Primary;
            config.OnClick = _ => InvokeAsync(() => UndoAsync(id));
        });
    }

    private async Task UndoAsync(Guid id)
    {
        _undoSnackbar = null;
        if (_rows.FirstOrDefault(r => r.Id == id) is not { } article)
        {
            return;
        }

        await WriteReadAsync(article, false, ReadSource.Undo);
        await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(id), RootId);
    }

    /// <summary>
    /// Undo is withdrawn once its card is off screen (§3.2.8, AC 30): after Hide read or any fresh first page the
    /// snackbar loses its action and reads the Mark-unread sentence.
    /// </summary>
    private void WithdrawUndo()
    {
        if (_undoSnackbar is not { } undo)
        {
            return;
        }

        _undoSnackbar = null;
        if (Snackbar.ShownSnackbars.Contains(undo))
        {
            Snackbar.Remove(undo);
            Snackbar.Add(UndoWithdrawnText, Severity.Normal, config => config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds);
        }
    }
}
