using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountTermsSection
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>Gates the New term / edit / delete affordances (accounts.terms.write).</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>
    /// The disclosure shell. False renders the section bare — no OdsCollapsible, no header — for a host
    /// that introduces it with its own OdsSectionDivider (an OdsRecordCard body).
    /// </summary>
    [Parameter] public bool Chrome { get; set; } = true;

    /// <summary>
    /// Render the "Current terms" block. False when the host lifted those values into the record
    /// card's own Current band, so they are not stated twice in one body.
    /// </summary>
    [Parameter] public bool ShowCurrent { get; set; } = true;

    /// <summary>
    /// Render the inner "History" sub-divider. False when the host's own section divider already
    /// labels this content and carries its count.
    /// </summary>
    [Parameter] public bool BareAction { get; set; } = true;

    /// <summary>Raised after a term is created/edited/deleted so the host can refresh the account
    /// list (the record card's Current band shows the in-force terms).</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>Formats a fee amount in its currency — supplied by the host (per-account currency).</summary>
    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    private List<ExistingTerm> _terms = [];
    private List<ExistingTerm> _current = [];
    private HashSet<Guid> _currentIds = [];

    private bool _isLoading;
    private bool _isOpen;

    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingTerm? _editingTerm;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;
        await LoadAsync();
    }

    private async Task ToggleOpen()
    {
        _isOpen = !_isOpen;
        if (_isOpen && _terms.Count == 0 && !_isLoading)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _terms = (await Accounts.ListTermsAsync(Account.AccountId)).ItemsOrToast(Snackbar, "terms");
        Recompute();
        _isLoading = false;
        StateHasChanged();
    }

    private async Task OnTermChanged()
    {
        await LoadAsync();
        await OnChanged.InvokeAsync();
    }

    private void Recompute()
    {
        // Newest first for the history table.
        _terms = _terms.OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.CreatedAtUtc).ToList();

        // One entry per SERIES — its label: a card charging a domestic and a foreign ATM fee has two
        // in force, and the later of them supersedes only its own series. Ordered by label, so tiles
        // keep a stable order across loads.
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
    }

    private void OpenNew()
    {
        _editingTerm = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OpenEdit(ExistingTerm term)
    {
        _editingTerm = term;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task DeleteAsync(ExistingTerm term)
    {
        // Named by what the user called it, so a card with several fees says which one is going.
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete term?",
            $"Remove the {TermVisuals.DisplayName(term)} entry effective {term.EffectiveFrom:MMM dd, yyyy}? This can’t be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed != true)
            return;

        var ok = (await Accounts.DeleteTermAsync(Account.AccountId, term.TermId))
            .Toast(Snackbar, "Unable to delete term", "Term deleted.");

        if (ok)
            await OnTermChanged();
    }

    private static string MonthYear(DateTime date) =>
        date.ToString("MMM", CultureInfo.InvariantCulture) + " ’" + (date.Year % 100).ToString("00");
}
