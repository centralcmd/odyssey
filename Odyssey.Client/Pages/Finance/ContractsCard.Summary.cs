using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    // ── Money (term values) ──────────────────────────────────────────────────────
    //
    // A contract has no currency of its own — every term names the one it is priced in — so the
    // denomination is resolved PER VALUE rather than once per record. OdsMoney writes the ISO code
    // after the figure, so the code IS the denomination on screen and there is no sigil to fall back
    // from; the only thing looked up here is the currency's own decimals.

    private IReadOnlyDictionary<string, ExistingCurrency> _currenciesByCode =
        new Dictionary<string, ExistingCurrency>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _minorUnitsCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadCurrencies()
    {
        var currencies = await ReferenceData.CurrenciesAsync();
        _currenciesByCode = currencies.ToDictionary(c => c.CurrencyCode, c => c, StringComparer.OrdinalIgnoreCase);
        _minorUnitsCache.Clear();
    }

    private string FormatMoney(decimal value, string? currencyCode) =>
        OdsMoney.Format(value, currencyCode, MinorUnits(currencyCode));

    // Cached per code so a list re-render does not re-walk the currency table per row.
    private int MinorUnits(string? currencyCode)
    {
        var key = string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : currencyCode;
        if (_minorUnitsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _currenciesByCode.TryGetValue(key, out var currency);
        var units = OdsMoney.MinorUnitsOf(currency);
        _minorUnitsCache[key] = units;
        return units;
    }

    // ── Header signal: "Attention" ────────────────────────────────────────────────
    //
    // Several groups under one button, one count and one worst-severity reading, because a reader
    // checking for a renewal cliff is checking for a charge in the same breath. It was labelled
    // "Upcoming" until it also carried the missing-rate group, which is not a date at all.
    //
    //   Recently expired  a term that ran out and was never archived: the decision still outstanding
    //   Missing exchange rate  an Active contract with a fee left out of the run rate (no rate to base)
    //   Ending soon       the cliff itself
    //   Starting soon     its mirror — signed, not yet in force
    //   Upcoming transactions  what falls due or arrives, derived from the fee terms in force
    //                          (server-computed): charges and receipts in one date-ordered list
    //
    // EVERY group is read against the loaded LIST, which is server-filtered: narrowing the search
    // narrows the panel with it. That is pre-existing behaviour for the ending-soon group and is kept
    // deliberately — every row carries a jump action, and a row that jumps to a record the list is not
    // showing would scroll to nothing and look broken. The transaction rows are computed server-side from
    // term data the list projection does not carry, so they arrive on the unfiltered summary and are
    // intersected back against the list here rather than being exempted from the rule.
    //
    // Two of the three dated groups are capped; ENDING SOON deliberately is not. It is the renewal
    // cliff the whole feature exists to surface, so dropping its seventh row — and under-counting the
    // badge — would hide exactly what a reader opened the panel for. The design system caps the same
    // two and leaves this one unbounded for the same reason.
    private const int MaxDatedSignalRows = 6;

    private List<PageHeaderProblem> HeaderProblems
    {
        get
        {
            var problems = new List<PageHeaderProblem>();
            var live = _contracts.Where(c => c.Archived is null).ToList();
            var listed = live.Select(c => c.ContractId).ToHashSet();

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Expired
                    && c.EndDate is { } end && DaysSince(end) >= 0 && DaysSince(end) <= EndingWindowDays)
                .OrderByDescending(c => c.EndDate)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Recently expired", PageHeaderSeverity.Error,
                    $"Term expired {OdsRelativeDay.Ago(DaysSince(c.EndDate!.Value))}.")));

            // Missing exchange rate: the Accounts problem, applied to the run rate. A WARNING — a figure
            // on this page is partial until the rate exists — placed after the error group and before
            // the dated warnings. Intersected with the list for the same reason as the transaction rows
            // below: the summary is unfiltered, and a jump to a record the list hides scrolls nowhere.
            problems.AddRange(RateGapProblems(
                _summary, listed, id => EventCallback.Factory.Create(this, () => JumpTo(id))));

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Active
                    && c.EndDate is { } end && DaysUntil(end) >= 0 && DaysUntil(end) <= EndingWindowDays)
                .OrderBy(c => c.EndDate)
                .Select(c => Dated(c, "Ending soon", PageHeaderSeverity.Warning,
                    $"Term ends {OdsDates.Long(c.EndDate!.Value)}.")));

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Upcoming
                    && c.StartDate is { } start && DaysUntil(start) >= 0 && DaysUntil(start) <= EndingWindowDays)
                .OrderBy(c => c.StartDate)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Starting soon", PageHeaderSeverity.Information,
                    $"Term starts {OdsRelativeDay.Ahead(DaysUntil(c.StartDate!.Value))}.")));

            // Awaiting signature: sent out, never returned (issue #145). Not a dated cliff — what
            // makes it actionable is that nothing will move it on its own, and every day it sits
            // there is a day an agreement everyone believes is in force is not. WARNING, unlike the
            // paused group below: a pause is a deliberate state someone chose, while an unreturned
            // signature is a thing that has stalled.
            //
            // DRAFTS ARE DELIBERATELY NOT HERE. A draft is work in progress — nobody is waiting on
            // anyone — and listing every one would turn this panel into a second contract list. The
            // status filter is where you go looking for those.
            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Ready && c.Ready is not null)
                .OrderBy(c => c.Ready)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Awaiting signature", PageHeaderSeverity.Warning,
                    $"Ready for signature {OdsRelativeDay.Ago(DaysSince(c.Ready!.Value))} — not signed, not counted in the run rate.")));

            // Paused agreements: not a cliff, but the one group here that cannot be seen by looking
            // at a date — a contract that stopped costing money because someone froze it, and which
            // nothing will un-freeze on its own. Information, not a warning: a deliberate state is
            // not a fault, and raising it would cry wolf on every contract someone meant to suspend.
            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Paused && c.Paused is not null)
                .OrderByDescending(c => c.Paused)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Paused", PageHeaderSeverity.Information,
                    $"Paused {OdsRelativeDay.Ago(DaysSince(c.Paused!.Value))} — not counted in the run rate.")));

            // Intersected against the list for the reason stated above: the summary is unfiltered, so
            // without this a row could name a contract the active filter has excluded, and its jump
            // would scroll to an element that is not on the page — silently, since JumpTo's
            // best-effort scroll swallows the miss.
            //
            // Charges and receipts (issue #159) read as ONE date-ordered group, as the design system
            // draws them: the signed amount names the direction, so the group heading no longer has
            // to. Each list keeps its own server-side cap, so a contract heavy with charges still
            // cannot starve the receipts. A contract that pays a salary on the 25th and deducts a fee
            // on the 1st appears twice — the server collapses per (contract, direction), so neither
            // movement hides the other.
            problems.AddRange(UpcomingMovements(_summary, listed).Select(movement => new PageHeaderProblem
            {
                Group = "Upcoming transactions",
                // Information: money moving as agreed is not a problem, and letting a charge raise the
                // button above info would cry wolf on every contract that has a price.
                Severity = PageHeaderSeverity.Information,
                Message = movement.Row.Name,
                Row = ChargeRow(movement.Row, movement.Incoming),
            }));

            return problems;
        }
    }

    /// <summary>
    /// The run rate's unconverted contracts that the loaded list also shows, in the server's name
    /// order. Empty without a summary.
    /// </summary>
    internal static IEnumerable<UnconvertedContract> RateGaps(ContractSummary? summary, IReadOnlySet<Guid> listed) =>
        (summary?.RunRate.UnconvertedContracts ?? []).Where(gap => listed.Contains(gap.ContractId));

    /// <summary>
    /// The "Missing exchange rate" group: one warning row per gap the list shows, led by the contract's
    /// name, its View bound by <paramref name="onView"/>.
    /// </summary>
    internal static IEnumerable<PageHeaderProblem> RateGapProblems(
        ContractSummary? summary, IReadOnlySet<Guid> listed, Func<Guid, EventCallback> onView) =>
        RateGaps(summary, listed).Select(gap => new PageHeaderProblem
        {
            Group = "Missing exchange rate",
            Severity = PageHeaderSeverity.Warning,
            Lead = gap.Name,
            Message = RateGapSummary(gap, summary!.RunRate.BaseCurrency),
            OnView = onView(gap.ContractId),
        });

    /// <summary>The run rate's gap for one contract, or null when every fee it carries converted.</summary>
    private UnconvertedContract? RateGapFor(Guid contractId) =>
        _summary?.RunRate.UnconvertedContracts.FirstOrDefault(gap => gap.ContractId == contractId);

    /// <summary>The header row: one line, the pair and what was left out of what.</summary>
    internal static string RateGapSummary(UnconvertedContract gap, string baseCurrency) =>
        $"No {string.Join(", ", gap.CurrencyCodes)} → {baseCurrency} rate, so this contract is left out of the run rate.";

    /// <summary>The record alert's detail: why conversion matters here and where the fix is.</summary>
    internal static string RateGapDetail(UnconvertedContract gap, string baseCurrency) =>
        $"Odyssey converts every fee into your main currency ({baseCurrency}) to total the run rate. "
        + $"There’s no {string.Join(", ", gap.CurrencyCodes)} → {baseCurrency} rate stored, so this contract’s "
        + $"{string.Join(" and ", gap.CurrencyCodes)} fees are left out of the run rate and the net. "
        + "Add the rate on the Exchange rates page to include them.";

    // The contextual fix for a missing-rate problem: the Exchange rates page, as on Accounts.
    private void GoToExchangeRates() => NavigationManager.NavigateTo("exchange-rates");

    /// <summary>
    /// The summary's upcoming charges and receipts as ONE list, earliest first, keeping only the
    /// contracts in <paramref name="listed"/>. <c>Incoming</c> comes from which summary list a row was
    /// read from. On the same day charges come before receipts: the sort is stable over the
    /// charges-then-receipts concatenation, so the order is deterministic across renders.
    /// </summary>
    internal static IEnumerable<(ContractUpcomingCharge Row, bool Incoming)> UpcomingMovements(
        ContractSummary? summary, IReadOnlySet<Guid> listed) =>
        (summary?.UpcomingCharges ?? []).Select(charge => (Row: charge, Incoming: false))
            .Concat((summary?.UpcomingReceipts ?? []).Select(receipt => (Row: receipt, Incoming: true)))
            .Where(movement => listed.Contains(movement.Row.ContractId))
            .OrderBy(movement => movement.Row.DaysUntil);

    private PageHeaderProblem Dated(
        ContractListItem contract, string group, PageHeaderSeverity severity, string message) => new()
    {
        Group = group,
        Severity = severity,
        Lead = contract.Name,
        Message = message,
        OnView = EventCallback.Factory.Create(this, () => JumpTo(contract.ContractId)),
    };

    private int DaysUntil(DateTime date) => (date.Date - Today).Days;

    private int DaysSince(DateTime date) => (Today - date.Date).Days;



    private async Task JumpTo(Guid id)
    {
        await EnsureDetail(id);
        _expandedId = id;
        _flashId = id;
        StateHasChanged();

        await OdsRecordReveal.ScrollIntoViewAsync(ScrollManager, $"#con-{id}");
        await OdsRecordReveal.EndFlashAsync(() => _flashId == id, EndFlash);
    }

    private void EndFlash()
    {
        _flashId = null;
        StateHasChanged();
    }

    // ── Expand / detail load ─────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private async Task ToggleExpand(Guid id)
    {
        // Any change of the open card consumes the pending row-menu requests. The sections are created
        // afresh on every expand and cannot remember a token they already handled, so a request left
        // in place would reopen its dialog each time the card is reopened. AddTerm / AddEvent set
        // theirs AFTER expanding, so they are not cleared by the expand they trigger.
        _newTermRequest = null;
        _newEventRequest = null;

        if (_expandedId == id)
        {
            _expandedId = null;
            return;
        }

        _expandedId = id;
        await EnsureDetail(id);
    }

    private async Task EnsureDetail(Guid id)
    {
        if (_details.ContainsKey(id))
        {
            return;
        }

        var contract = await Contracts.GetAsync(id);
        if (contract is not null)
        {
            _details[id] = contract;
        }

        StateHasChanged();
    }

    /// <summary>
    /// Patches one contract's documents in place after a document METADATA edit (issue #146). Such an
    /// edit creates and removes no row, so the list row's file count and every summary figure are
    /// unchanged — pulling the contract, the list and the summary back down for it would be three
    /// requests to observe a one-row change the list endpoint already returned.
    /// </summary>
    private void ApplyContractFiles(Guid id, List<ExistingContractFile> files)
    {
        if (_details.TryGetValue(id, out var contract))
        {
            contract.Files = files;
        }

        StateHasChanged();
    }

    private async Task ReloadContract(Guid id)
    {
        var contract = await Contracts.GetAsync(id);
        if (contract is not null)
        {
            _details[id] = contract;
        }

        await LoadContracts();
        await LoadSummary();
        StateHasChanged();
    }
}
