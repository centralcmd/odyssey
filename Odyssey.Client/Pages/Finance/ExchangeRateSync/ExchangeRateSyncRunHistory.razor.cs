using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.ExchangeRateSync;

public partial class ExchangeRateSyncRunHistory
{
    internal const int PageSize = 10;

    private List<ExistingExchangeRateSyncRun> _runs = [];
    private bool _loading = true;
    private bool _refetching;
    private bool _error;
    private int _page = 1;
    private int _total;
    private readonly ListLoader _listLoader = new();

    private IReadOnlyList<OdsRecordColumn<ExistingExchangeRateSyncRun>> Columns =>
    [
        new() { Key = "started", HeaderText = "Started", Cell = StartedCell },
        new()
        {
            Key = "trigger", HeaderText = "Trigger",
            Cell = (r, _) => b => b.AddContent(0, OdsTypeRegistries.ExchangeRateSyncTriggerOf(r.Trigger)),
        },
        new() { Key = "status", HeaderText = "Status", Cell = StatusCell },
        new() { Key = "added", HeaderText = "Added", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsInserted.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "rejected", HeaderText = "Rejected", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsRejected.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "deleted", HeaderText = "Deleted", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsDeleted.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "error", HeaderText = "Error", Cell = ErrorCell },
    ];

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        // A retry after a failed read blanks the table for the spinner, as the first load does; a page
        // change keeps the rows and shows the refetch bar.
        if (_error)
        {
            _error = false;
            _loading = true;
            StateHasChanged();
        }
        else if (!_loading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var response = await _listLoader.RunAsync(ct => Sync.ListRunsAsync(_page, PageSize, ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "run history");
        if (load.IsSuccess)
        {
            _runs = [.. load.Items];
            _total = load.TotalCount;
            _error = false;
        }
        else
        {
            _error = true;
        }

        _loading = false;
        _refetching = false;
        StateHasChanged();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return LoadAsync();
    }

    internal IReadOnlyList<OdsMenuItem> RunActions(ExistingExchangeRateSyncRun r, OdsRecordActionContext ctx) =>
    [
        new OdsMenuItem { Icon = ctx.Expanded ? "unfold_less" : "unfold_more", Label = ctx.Expanded ? "Hide details" : "View details", OnClick = EventCallback.Factory.Create(this, ctx.Toggle) },
    ];

    private static string SpanText(ExistingExchangeRateSyncRun r) => (r.SpanFrom, r.SpanTo) switch
    {
        ({ } from, { } to) when from == to => ExchangeRateSyncTime.Date(from),
        ({ } from, { } to) => $"{ExchangeRateSyncTime.Date(from)} – {ExchangeRateSyncTime.Date(to)}",
        _ => "—",
    };

    public void Dispose() => _listLoader.Dispose();
}
