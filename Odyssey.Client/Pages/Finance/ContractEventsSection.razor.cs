using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The contract's event log (issue #138) — the section's data, paging and writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paged on the server, not sliced in memory.</b> The endpoint exists precisely because an event
/// log grows without bound, so the pager's window becomes the query's <c>offset</c>/<c>limit</c> and
/// each page is a fresh read. Loading the whole log to page it locally would give back exactly the
/// cost the separate endpoint was created to avoid.
/// </para>
/// <para>
/// <b>No client copy of any server rule.</b> The 256/1024 lengths and the future bound belong to the
/// dialog, which mirrors the server's validation so a user meets a refusal before the round trip; the
/// server still decides. Nothing here holds a cap.
/// </para>
/// <para>
/// <b>The section takes no <c>Archived</c> parameter</b>, unlike <see cref="ContractTermsSection"/>.
/// An archived contract still accepts every event write (§8.6), so there is no refusal to explain and
/// no affordance to withdraw — adding the parameter "for symmetry" would invite a guard that
/// contradicts the API.
/// </para>
/// </remarks>
public partial class ContractEventsSection
{
    /// <summary>
    /// Rows per page. Matches the design system's <c>pageSize</c> default; the endpoint's own default
    /// is not relied on, because the pager has to ask for a window it can then describe.
    /// </summary>
    public const int PageSize = 25;

    /// <summary>
    /// What an unresolvable author reads as. The API resolves this itself and the field is
    /// non-null in practice; the fallback is for a response shaped by an older or partial server
    /// rather than for a deleted user, which the resolver already answers.
    /// </summary>
    private const string UnknownAuthor = "Unknown user";

    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    /// <summary>Gates every write affordance (<c>contracts.update</c>).</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>
    /// An outstanding "New event" request from the record's row action menu — a token that changes
    /// per ask. The section carries no action slot of its own, so the request arrives as DATA rather
    /// than through an <c>@ref</c> the host would have to time correctly; a token only ever reaches
    /// the section of the contract that asked. Same arrangement <see cref="ContractTermsSection"/>
    /// uses, and for the same reason.
    /// </summary>
    [Parameter] public Guid? NewEventRequestToken { get; set; }

    private List<ExistingContractEvent> _events = [];
    private int _total;
    private int _page = 1;
    private bool _isLoading;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingContractEvent? _editingEvent;

    /// <summary>Today in UTC, captured once per render so the cap and a year marker cannot disagree.</summary>
    private DateTime Today => DateTime.UtcNow.Date;

    private bool IsFirstPage => _page == 1;

    /// <summary>
    /// Whether this page holds the OLDEST end of the log. It decides whether the rail's line stops
    /// hard at the foot or fades: a paged middle genuinely continues, and a hard stop would say
    /// otherwise.
    /// </summary>
    private bool IsLastPage => _page * PageSize >= _total;

    private string Meta => _isLoading
        ? "loading…"
        : _total == 0
            ? "none recorded"
            : $"{_total} {(_total == 1 ? "entry" : "entries")} · newest first";

    /// <summary>The last request token acted on, so one ask opens exactly one dialog.</summary>
    private Guid? _handledNewEventToken;

    /// <summary>
    /// Which contract the log currently in <see cref="_events"/> belongs to. The host list renders its
    /// rows WITHOUT an <c>@key</c> (see <c>OdsInfiniteList</c>), so re-sorting or re-filtering while a
    /// card is expanded can re-point this instance at a different record without re-initialising it —
    /// and a log showing another contract's history under this one's header is the kind of wrong that
    /// reads as correct. Tracking the id is what turns that into a reload.
    /// </summary>
    private Guid _loadedContractId;

    protected override async Task OnParametersSetAsync()
    {
        if (OperatingSystem.IsBrowser() && _loadedContractId != Contract.ContractId)
        {
            _loadedContractId = Contract.ContractId;
            // A different record means a different log, so nothing about the old one carries over —
            // including which page of it was being read.
            _page = 1;
            await LoadAsync();
        }

        if (NewEventRequestToken is not { } token || token == _handledNewEventToken)
            return;

        _handledNewEventToken = token;
        OpenNew();
    }

    /// <summary>
    /// Opens the create dialog. Public for the same reason <see cref="ReloadAsync"/> is: the section
    /// carries no action slot, so the host — which owns the record — drives it.
    /// </summary>
    public void OpenNew()
    {
        _editingEvent = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    /// <summary>Re-reads the current page. Public so the host can refresh after a change it made.</summary>
    public Task ReloadAsync()
    {
        _loadedContractId = Contract.ContractId;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        StateHasChanged();

        var page = (await Contracts.ListEventsAsync(Contract.ContractId, page: _page, pageSize: PageSize))
            .PagedOrToast(Snackbar, "events");

        _events = [.. page.Items];
        _total = page.TotalCount;

        // A delete can empty the last page. Stepping back rather than leaving an empty rail under a
        // pager that still offers the page is what keeps the two consistent. The retry is bounded by
        // requiring the new page to be STRICTLY EARLIER than the one that came back empty — without
        // that, a total and a window that disagree (a concurrent write between the count and the
        // read) would re-request the same page forever.
        var lastPage = Math.Max(1, (_total + PageSize - 1) / PageSize);
        if (_events.Count == 0 && _total > 0 && lastPage < _page)
        {
            _page = lastPage;
            await LoadAsync();
            return;
        }

        _isLoading = false;
        StateHasChanged();
    }

    private async Task GoToPageAsync(int page)
    {
        if (page == _page)
            return;

        _page = page;
        await LoadAsync();
    }

    private void OpenEdit(ExistingContractEvent ev)
    {
        _editingEvent = ev;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingContractEvent ev)
    {
        // Named by what the user called it, so a log with several entries says which one is going.
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete event?",
            $"Remove “{ev.Title}” from this contract's log? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Contracts.DeleteEventAsync(Contract.ContractId, ev.ContractEventId))
            .Toast(Snackbar, "Unable to delete event", "Event deleted.");

        if (ok)
            await LoadAsync();
    }

    private Task OnEventChangedAsync() => LoadAsync();

    private bool IsKnownAuthor(ExistingContractEvent ev) =>
        !string.IsNullOrWhiteSpace(ev.CreatedBy)
        && !string.Equals(ev.CreatedBy, UnknownAuthor, StringComparison.Ordinal);

    private string AuthorLabel(ExistingContractEvent ev) =>
        string.IsNullOrWhiteSpace(ev.CreatedBy) ? UnknownAuthor : ev.CreatedBy;

    /// <summary>What a track entry is: an event, a year boundary, or one of the two endpoint caps.</summary>
    private enum TrackKind
    {
        Event,
        Year,
        Now,
        Origin,
    }

    private sealed record TrackEntry(TrackKind Kind, ExistingContractEvent? Event = null, int Year = 0);

    /// <summary>
    /// A FLAT track, not a stack of per-year lists: the line runs unbroken from the newest entry to
    /// the oldest, and a year is a marker sitting ON it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The top is anchored to TODAY on page 1 — newest-first means the top of the rail is the present,
    /// the log is live, and the next entry lands there. That cap already carries the current year, so
    /// a year marker repeating it one row later would say nothing and is suppressed.
    /// </para>
    /// <para>
    /// The foot is anchored to when the RECORD was added, placed CHRONOLOGICALLY rather than pinned to
    /// the bottom: an event may legitimately be backdated before the record was created — a history
    /// entered after the fact. Usually it is older than everything and lands at the foot anyway, which
    /// is the anchor this is for. It is appended only on the page that actually holds the end of the
    /// log, since a mid-log page has no end to anchor.
    /// </para>
    /// </remarks>
    private IReadOnlyList<TrackEntry> Track
    {
        get
        {
            var track = new List<TrackEntry>(_events.Count + 3);
            if (IsFirstPage)
            {
                track.Add(new TrackEntry(TrackKind.Now));
            }

            int? lastYear = IsFirstPage ? Today.Year : null;
            foreach (var ev in _events)
            {
                var year = ev.OccurredAt.Year;
                if (year != lastYear)
                {
                    track.Add(new TrackEntry(TrackKind.Year, Year: year));
                    lastYear = year;
                }

                track.Add(new TrackEntry(TrackKind.Event, ev));
            }

            var addedAt = Contract.CreatedAtUtc;
            var origin = new TrackEntry(TrackKind.Origin);
            var at = track.FindIndex(x => x.Event is { } e && e.OccurredAt < addedAt);
            if (at != -1)
            {
                track.Insert(at, origin);
            }
            else if (IsLastPage)
            {
                track.Add(origin);
            }

            return track;
        }
    }

    /// <summary>
    /// The design system's log format — "14 Jun 2026", and "14 Jun 2026 · 09:31" where the time
    /// matters. Rendered as stored, which is UTC: an event's time is a fact about when it happened,
    /// and shifting it into the reader's zone would make two people describing the same entry disagree
    /// about its date.
    /// </summary>
    private static string FormatDate(DateTime value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    private static string FormatDateTime(DateTime value) =>
        $"{FormatDate(value)} · {value.ToString("HH:mm", CultureInfo.CurrentCulture)}";
}
