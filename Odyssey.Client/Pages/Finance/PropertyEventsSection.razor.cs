using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// A property's event log (issue #209) — the section's data, paging and writes. The
/// <see cref="ContractEventsSection"/> arrangement, owner-swapped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paged on the server, not sliced in memory.</b> The pager's window is the query's
/// <c>offset</c>/<c>limit</c>, and each page is a fresh read.
/// </para>
/// <para>
/// <b>An archived property stays writable here.</b> The API accepts every event write on an archived
/// property, so the section takes no <c>Archived</c> parameter and withdraws nothing.
/// </para>
/// </remarks>
public partial class PropertyEventsSection : IAsyncDisposable
{
    /// <summary>Rows per page — the design system's <c>pageSize</c> default.</summary>
    public const int PageSize = 25;

    private const string UnknownAuthor = "Unknown user";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private IJSObjectReference? _focusJs;

    [Parameter, EditorRequired] public ExistingProperty Property { get; set; } = default!;

    /// <summary>Gates every write affordance (<c>properties.update</c>).</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>A sentence for the page's single live region, raised after a delete.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>
    /// Raised after any write, so the host can refresh the record's <c>EventCount</c> — the collapsed
    /// card shows it.
    /// </summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>
    /// An outstanding "New event" request from the record's ⋯ menu — a token that changes per ask, the
    /// <see cref="PropertyEstimatesSection.NewEstimateRequestToken"/> arrangement.
    /// </summary>
    [Parameter] public Guid? NewEventRequestToken { get; set; }

    private List<ExistingPropertyEvent> _events = [];
    private int _total;
    private int _page = 1;
    private bool _isLoading;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingPropertyEvent? _editingEvent;

    private Guid? _handledNewEventToken;

    /// <summary>
    /// Which property the loaded log belongs to. The host list renders rows without an <c>@key</c>, so
    /// a re-sort can re-point this instance at another record; tracking the id turns that into a reload.
    /// </summary>
    private Guid _loadedPropertyId;

    /// <summary>
    /// The record's <c>UpdatedAt</c> when the log was read. A property write — an archive, a restore, a
    /// date set or cleared — is exactly when the server may have recorded a system row, and the host
    /// hands a re-read record down afterwards; a moved stamp is what re-reads the log. An event write
    /// does not touch the property, so it never costs a second read.
    /// </summary>
    private DateTime _loadedUpdatedAt;

    /// <summary>Per record, so two open logs could never share a focus target.</summary>
    private string HeadingId => $"prop-events-heading-{Property.PropertyId}";

    private DateTime Today => DateTime.UtcNow.Date;

    private bool IsFirstPage => _page == 1;

    private bool IsLastPage => _page * PageSize >= _total;

    private string Meta => _isLoading
        ? "loading…"
        : _total == 0
            ? "0 entries"
            : $"{_total} {(_total == 1 ? "entry" : "entries")} · newest first";

    private string Examples => Property.Type == PropertyType.Vehicle
        ? "a service, a tyre change, the periodic inspection"
        : "a repair, a renovation, a tenancy";

    protected override async Task OnParametersSetAsync()
    {
        if (OperatingSystem.IsBrowser()
            && (_loadedPropertyId != Property.PropertyId || _loadedUpdatedAt != Property.UpdatedAt))
        {
            // A different record means a different log, read from its first page; the same record
            // re-read after a property write keeps the reader's page.
            if (_loadedPropertyId != Property.PropertyId)
                _page = 1;

            _loadedPropertyId = Property.PropertyId;
            _loadedUpdatedAt = Property.UpdatedAt;
            await LoadAsync();
        }

        if (NewEventRequestToken is not { } token || token == _handledNewEventToken)
            return;

        _handledNewEventToken = token;
        OpenNew();
    }

    /// <summary>Opens the create dialog. Public so the host, which owns the record's menu, can drive it.</summary>
    public void OpenNew()
    {
        _editingEvent = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    /// <summary>Re-reads the current page. Public so a host can force a refresh.</summary>
    public Task ReloadAsync()
    {
        _loadedPropertyId = Property.PropertyId;
        _loadedUpdatedAt = Property.UpdatedAt;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        StateHasChanged();

        var page = (await Properties.ListEventsAsync(Property.PropertyId, page: _page, pageSize: PageSize))
            .PagedOrToast(Snackbar, "events");

        _events = [.. page.Items];
        _total = page.TotalCount;

        // A delete can empty the last page; step back, but only to a STRICTLY earlier page so a
        // count and window that disagree cannot loop.
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

    private void OpenEdit(ExistingPropertyEvent ev)
    {
        _editingEvent = ev;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingPropertyEvent ev)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete event?",
            $"Remove “{ev.Title}” from this property's log? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Properties.DeleteEventAsync(Property.PropertyId, ev.PropertyEventId))
            .Toast(Snackbar, "Unable to delete event", "Event deleted.");

        if (!ok)
            return;

        await LoadAsync();

        // The focused row is gone: move focus to the heading and say what happened.
        await FocusHeadingAsync();
        await AnnounceCountAsync();
        await NotifyChangedAsync();
    }

    /// <summary>
    /// Moves focus to the section heading, deferred inside the module past the row menu's own focus
    /// restore.
    /// </summary>
    private async Task FocusHeadingAsync()
    {
        try
        {
            _focusJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/section-focus.js");
            await _focusJs.InvokeVoidAsync("focusHeading", HeadingId);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            // Focus is an enhancement on a delete that already succeeded.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusJs is not null)
        {
            try { await _focusJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The sentence a delete raises to the page's announcer.</summary>
    public static string DeletionAnnouncement(int remaining) =>
        $"Event deleted. {remaining} {(remaining == 1 ? "entry" : "entries")} in the log.";

    private Task AnnounceCountAsync() =>
        OnAnnounce.HasDelegate ? OnAnnounce.InvokeAsync(DeletionAnnouncement(_total)) : Task.CompletedTask;

    private Task NotifyChangedAsync() =>
        OnChanged.HasDelegate ? OnChanged.InvokeAsync() : Task.CompletedTask;

    private async Task OnEventChangedAsync()
    {
        await LoadAsync();
        await NotifyChangedAsync();
    }

    private static bool IsKnownAuthor(ExistingPropertyEvent ev) =>
        !string.IsNullOrWhiteSpace(ev.CreatedBy)
        && !string.Equals(ev.CreatedBy, UnknownAuthor, StringComparison.Ordinal);

    private static string AuthorLabel(ExistingPropertyEvent ev) =>
        string.IsNullOrWhiteSpace(ev.CreatedBy) ? UnknownAuthor : ev.CreatedBy;

    private enum TrackKind
    {
        Event,
        Year,
        Now,
        Origin,
    }

    private sealed record TrackEntry(TrackKind Kind, ExistingPropertyEvent? Event = null, int Year = 0);

    /// <summary>
    /// A flat track: Today on page 1, year markers ON the line, and the record's creation placed
    /// chronologically on the page that holds it.
    /// </summary>
    /// <remarks>
    /// The origin never lands UNDER an older year's marker: when the first event older than the record
    /// is the first of its year, the origin goes above that year's marker, since the record was added in
    /// a later year. This is the design system's own placement rule (PropertyEvents.jsx).
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

            var addedAt = Property.CreatedAt;
            var origin = new TrackEntry(TrackKind.Origin);
            var at = track.FindIndex(x => x.Event is { } e && e.OccurredAt < addedAt);
            if (at > 0 && track[at - 1].Kind == TrackKind.Year && track[at - 1].Year != addedAt.Year)
            {
                at -= 1;
            }

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

    /// <summary>"14 Jun 2026", rendered as stored (UTC) so two readers agree on an entry's date.</summary>
    private static string FormatDate(DateTime value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    private static string FormatDateTime(DateTime value) =>
        $"{FormatDate(value)} · {value.ToString("HH:mm", CultureInfo.CurrentCulture)}";
}
