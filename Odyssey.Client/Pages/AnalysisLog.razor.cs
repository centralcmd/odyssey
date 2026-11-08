using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

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

    /// <summary>What the latest match run sent (issue #434), from the audit entry's own fields.</summary>
    internal static string MatchedAgainst(FileAnalysisAuditEntry e)
    {
        if (e.MatchVocabularyScope is null)
        {
            return e.VocabularyCount is { } names ? $"{names:N0} contact + tag names" : "—";
        }

        if (e.ContactCount is not { } contacts || e.TagCount is not { } tags)
        {
            return "Not run yet";
        }

        if (e.MatchVocabularyScope == FileAnalysisMatchVocabularyScope.NamesOnly)
        {
            return $"{contacts:N0} contacts · {tags:N0} tags · names only";
        }

        var reduced = e.VocabularyReduction switch
        {
            FileAnalysisVocabularyReduction.AliasesOmitted => " · aliases left out",
            FileAnalysisVocabularyReduction.AliasesAndDescriptionsOmitted => " · aliases and descriptions left out",
            _ => string.Empty,
        };
        return $"{contacts:N0} contacts ({e.AliasCount ?? 0:N0} aliases) · {tags:N0} tags ({e.TagDescriptionCount ?? 0:N0} described){reduced}";
    }

    /// <summary>The guidance fact: the fingerprint in full, "None sent", or "Not recorded (before this feature)".</summary>
    internal static RenderFragment Guidance(FileAnalysisAuditEntry e, string? digest) => builder =>
    {
        if (e.MatchVocabularyScope is null)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "fal-unrec");
            builder.AddContent(2, "Not recorded (before this feature)");
            builder.CloseElement();
        }
        else if (!string.IsNullOrEmpty(digest))
        {
            builder.OpenElement(3, "span");
            builder.AddAttribute(4, "class", "mono");
            builder.AddContent(5, digest);
            builder.CloseElement();
        }
        else
        {
            builder.AddContent(6, "None sent");
        }
    };
}
