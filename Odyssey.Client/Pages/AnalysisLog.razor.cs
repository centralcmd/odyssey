using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;

namespace Odyssey.Client.Pages;

public partial class AnalysisLog
{
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();

    // Server-side (issue #277): outcome filter, search (incl. initiating user) and sort are applied by
    // the API, which enriches each entry with the user before filtering.
    private async Task LoadAsync()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var response = await _listLoader.RunAsync(ct => FileAnalysis.ListAuditAsync(
            search: _search,
            statuses: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            ct: ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;

        if (result.IsSuccess)
        {
            _entries = result.ValueOr([]);
            _loadError = false;
            _announce = _entries.Count == 0 ? "No log entries match your filters."
                : $"Showing {_entries.Count} log {(_entries.Count == 1 ? "entry" : "entries")}.";
        }
        else
        {
            Snackbar.Add($"Unable to load the analysis log: {result.Error}", Severity.Error);
            _entries = [];
            _loadError = true;
            _announce = "Couldn't load the analysis log.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    public void Dispose() => _listLoader.Dispose();
}
