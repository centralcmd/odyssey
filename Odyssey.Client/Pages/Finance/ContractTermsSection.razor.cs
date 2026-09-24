using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The contract half of the term surface (issue #135).
/// </summary>
/// <remarks>
/// <para>
/// <b>The per-contract cap is deliberately NOT mirrored here.</b> <c>ContractMaxTermsPerContract</c>
/// is admin-editable, and CLAUDE.md forbids a page holding a server cap as a constant: a stale copy
/// would either refuse a write the server allows or allow one it refuses. The two sibling caps on
/// this very card — parties and documents — take the same posture: the server enforces, and its
/// <c>422</c> names the effective number, which the dialog surfaces verbatim. That is strictly better
/// than a client copy, because the number in the message is the one actually enforced.
/// </para>
/// <para>
/// The design system draws a disabled "New term" at the cap. Reaching that state faithfully would
/// need a fourth claim-free limits endpoint plus its own cache key, and
/// <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single string — this key already evicts the
/// finance request caps, so a second consumer could not be invalidated on a settings save without
/// changing that shared type. The refusal is therefore stated at the moment of writing rather than
/// before it.
/// </para>
/// </remarks>
public partial class ContractTermsSection
{
    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    /// <summary>Gates every write affordance (<c>contracts.update</c>).</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>Formats a money-valued term in its own currency — supplied by the host.</summary>
    [Parameter, EditorRequired]
    public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An outstanding "New term" request from the record's row action menu — a token that changes per
    /// ask. The section carries no action slot of its own, so the request arrives as DATA rather than
    /// through an <c>@ref</c> the host would have to time correctly; a token only ever reaches the
    /// section of the contract that asked.
    /// </summary>
    [Parameter] public Guid? NewTermRequestToken { get; set; }

    /// <summary>
    /// Raised after a term is created, edited or deleted so the host can refresh the contract — the
    /// header's term count and the nested <c>currentTerms</c> both move with it.
    /// </summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private List<ExistingTerm> _terms = [];
    private List<ExistingTerm> _current = [];
    private HashSet<Guid> _currentIds = [];
    private List<OdsTermHistorySeries> _chartSeries = [];

    private bool _isLoading;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingTerm? _editingTerm;

    /// <summary>
    /// A contract with no terms at all is a different state from one whose entries are all scheduled,
    /// and the divider says which: "Terms · 0 entries" for the first, "Current terms · none in
    /// force" for the second.
    /// </summary>
    private bool HasNoTerms => !_isLoading && _terms.Count == 0;

    private string CurrentLabel => HasNoTerms ? "Terms" : "Current terms";

    /// <summary>
    /// A contract is not one-directional — an employment agreement pays a salary in and deducts dues
    /// out — so the divider says how many of each rather than one undifferentiated count of "values".
    /// </summary>
    private string CurrentMeta
    {
        get
        {
            if (HasNoTerms) return "0 entries";
            if (_current.Count == 0) return "none in force";

            var incoming = _current.Count(TermVisuals.IsIncoming);
            // The split is stated only when there IS an incoming side: on a file that records costs
            // alone it would be a breakdown of one thing, which reads as noise on every contract.
            var split = incoming > 0
                ? $" · {incoming} incoming, {_current.Count - incoming} outgoing"
                : string.Empty;

            return $"{_current.Count} {(_current.Count == 1 ? "value" : "values")} in force{split} · {DateTime.UtcNow:MMM dd, yyyy}";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await LoadAsync();
    }

    /// <summary>The last request token acted on, so one ask opens exactly one dialog.</summary>
    private Guid? _handledNewTermToken;

    protected override void OnParametersSet()
    {
        if (NewTermRequestToken is not { } token || token == _handledNewTermToken)
            return;

        _handledNewTermToken = token;
        OpenNew();
    }

    private void OpenNew()
    {
        _editingTerm = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    /// <summary>
    /// Re-reads the term history. Public for the same reason <see cref="OpenNew"/> is: the section
    /// carries no action slot, so the host — which owns the record and knows when it changed under
    /// the section's feet, an unarchive being the obvious case — drives it.
    /// </summary>
    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _isLoading = true;
        _terms = (await Contracts.ListTermsAsync(Contract.ContractId)).ItemsOrToast(Snackbar, "terms");
        Recompute();
        _isLoading = false;
        StateHasChanged();
    }

    private async Task OnTermChanged()
    {
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    /// <summary>
    /// Resolves the in-force entry of each series from the history this section already holds.
    /// </summary>
    /// <remarks>
    /// The API also serves the same set as <c>ExistingContract.CurrentTerms</c> and as
    /// <c>GET …/terms/current</c>, and any non-Blazor consumer should read it there. This surface
    /// derives it because it needs the in-force <em>ids</em> anyway, to mark the history rows: taking
    /// the tiles from one source and the badges from another is how a tile and the row it summarises
    /// come to disagree. One computation, both readings.
    /// </remarks>
    private void Recompute()
    {
        // Newest first for the history table, ties broken by newest created — the supersession order.
        _terms = _terms
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.CreatedAtUtc)
            .ToList();

        // One entry per SERIES — its label: a lease carrying rent and a service charge has two in
        // force, and the later of them supersedes only its own series.
        var asOf = DateTime.UtcNow.Date;

        _current = _terms
            .Where(t => t.EffectiveFrom.Date <= asOf)
            .GroupBy(t => TermLabel.Key(t.Label))
            .Select(group => group
                .OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.CreatedAtUtc)
                .First())
            .OrderBy(t => TermLabel.Key(t.Label) ?? "", StringComparer.Ordinal)
            .ToList();
        _currentIds = _current.Select(t => t.TermId).ToHashSet();
        _chartSeries = TermChartSeries.Build(_terms, asOf, FormatMoney);
    }

    /// <summary>
    /// The tile's foot: the date the term took effect, and the cadence.
    /// </summary>
    private static string TileFoot(ExistingTerm term, string? cadence)
    {
        var parts = new List<string>(2);
        parts.Add($"since {term.EffectiveFrom:MMM dd, yyyy}");

        if (cadence is not null)
            parts.Add(cadence);

        return string.Join(" · ", parts);
    }

    private void OpenEdit(ExistingTerm term)
    {
        _editingTerm = term;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingTerm term)
    {
        // Named by what the user called it, so a contract with several charges says which one is going.
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete term?",
            $"Remove the {TermVisuals.DisplayName(term)} entry effective {term.EffectiveFrom:MMM dd, yyyy}? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Contracts.DeleteTermAsync(Contract.ContractId, term.TermId))
            .Toast(Snackbar, "Unable to delete term", "Term deleted.");

        if (ok)
            await OnTermChanged();
    }
}
