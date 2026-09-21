using Odyssey.Client.Components;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class BudgetItemsSection
{
    [Parameter] public Guid BudgetId { get; set; }
    [Parameter] public string? BudgetName { get; set; }
    [Parameter] public List<ExistingBudgetItem> Items { get; set; } = new();
    [Parameter] public Dictionary<Guid, decimal> ActualByTag { get; set; } = new();
    [Parameter] public bool HasReport { get; set; }
    [Parameter] public List<ExistingTransactionTag> TransactionTags { get; set; } = new();

    /// <summary>
    /// The tag list failed to load, as opposed to being empty (issue #75 §11). Reading is unaffected —
    /// every row renders its name from the payload — but the pickers say so and offer a retry, which is
    /// NOT the "create a tag" empty state: that would point the reader at the wrong problem.
    /// </summary>
    [Parameter] public bool TagsLoadFailed { get; set; }

    /// <summary>Re-fetch the tag list after a failed load.</summary>
    [Parameter] public EventCallback OnRetryTags { get; set; }
    /// <summary>Formats a figure in its currency — supplied by the host, which holds the currency
    /// table. The fallback writes the amount with its code trailing and the default two decimals.</summary>
    [Parameter] public Func<decimal, string?, string> Format { get; set; } = (value, code) => OdsMoney.Format(value, code);
    [Parameter] public string CurrencyCode { get; set; } = "USD";

    /// <summary>
    /// The disclosure shell. False renders the section bare — no OdsCollapsible, no header — for a host
    /// that introduces it with its own OdsSectionDivider (an OdsRecordCard body).
    /// </summary>
    [Parameter] public bool Chrome { get; set; } = true;

    /// <summary>
    /// The "edit multiple" batch grid. Host-owned, because with <see cref="Chrome"/> off the section has
    /// no header to put the toggle on — it lives on the record's row menu, alongside "New item".
    /// </summary>
    [Parameter] public bool Editing { get; set; }

    /// <summary>Raised when the batch grid's own Done button leaves the mode.</summary>
    [Parameter] public EventCallback<bool> EditingChanged { get; set; }

    [Parameter] public bool CanCreate { get; set; }
    [Parameter] public bool CanUpdate { get; set; }
    [Parameter] public bool CanDelete { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _isOpen = true;
    private bool _isBusy;
    private bool _seededFor;
    private readonly Dictionary<Guid, Draft> _drafts = new();

    // Per-row field errors for the batch grid (issue #75 §3) — the view rows have no editable field
    // and so need none.
    private readonly Dictionary<Guid, string> _draftErrors = new();

    // Serialises the grid's save-on-change writes without dropping any of them.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private void ToggleOpen() => _isOpen = !_isOpen;

    // The drafts are seeded when the host switches the mode on, not when a button here is pressed.
    protected override void OnParametersSet()
    {
        if (Editing == _seededFor)
            return;
        if (Editing)
            SeedDrafts();
        _seededFor = Editing;
    }

    // Income first, then expenses; empty groups are dropped.
    private IEnumerable<ItemGroup> VisibleGroups
    {
        get
        {
            var income = Items.Where(i => i.CategoryType == BudgetCategoryType.Income).ToList();
            var expense = Items.Where(i => i.CategoryType == BudgetCategoryType.Expense).ToList();
            if (income.Count > 0)
                yield return new ItemGroup("Income", BudgetCategoryType.Income, income);
            if (expense.Count > 0)
                yield return new ItemGroup("Expenses", BudgetCategoryType.Expense, expense);
        }
    }

    private sealed record ItemGroup(string Label, BudgetCategoryType Category, List<ExistingBudgetItem> Items);

    // Display actual for an item: income uses the raw sum, expense uses its magnitude. Every item has
    // a tag now, so the only "—" left is the one before the report has loaded.
    private decimal? ItemActual(ExistingBudgetItem item)
    {
        if (!HasReport)
            return null;
        if (!ActualByTag.TryGetValue(item.TransactionTagId, out var sum))
            return 0m;
        return item.CategoryType == BudgetCategoryType.Income ? sum : Math.Abs(sum);
    }

    // ── Edit mode ────────────────────────────────────────────────────────
    private Task StopEditing() => EditingChanged.InvokeAsync(false);

    private void SeedDrafts()
    {
        _drafts.Clear();
        _draftErrors.Clear();
        foreach (var item in Items)
            _drafts[item.BudgetItemId] = new Draft(item);
    }

    private Draft GetDraft(ExistingBudgetItem item)
    {
        if (!_drafts.TryGetValue(item.BudgetItemId, out var draft))
        {
            draft = new Draft(item);
            _drafts[item.BudgetItemId] = draft;
        }
        return draft;
    }

    private async Task OnCategoryChanged(ExistingBudgetItem item, BudgetCategoryType value)
    {
        GetDraft(item).CategoryType = value;
        await SaveItem(item);
    }

    private async Task OnTagChanged(ExistingBudgetItem item, Guid? value)
    {
        var draft = GetDraft(item);
        if (value is null || value == Guid.Empty)
        {
            // The picker is required and offers no clear-to-nothing path to a saved row, so an empty
            // value is a transient UI state, not a write.
            _draftErrors[item.BudgetItemId] = "Choose a transaction tag.";
            draft.TransactionTagId = value;
            StateHasChanged();
            return;
        }

        draft.TransactionTagId = value;
        await SaveItem(item);
    }

    private async Task OnPlannedChanged(ExistingBudgetItem item, decimal value)
    {
        GetDraft(item).PlannedAmount = value;
        await SaveItem(item);
    }

    /// <summary>
    /// Saves one row of the batch grid.
    /// </summary>
    /// <remarks>
    /// It does <b>not</b> optimistically patch the parameter-supplied item. <c>PUT</c> returns
    /// <c>204</c>, so there is no <c>Tag</c> to write back — and the tag is now the row's whole
    /// identity, so a retag patched from the request would leave the row rendering the PREVIOUS tag's
    /// name and description until something else reloaded it. The parent's reload is awaited instead.
    /// </remarks>
    private async Task SaveItem(ExistingBudgetItem item)
    {
        if (!CanUpdate)
            return;

        var draft = GetDraft(item);
        if (draft.TransactionTagId is not { } tagId || tagId == Guid.Empty)
        {
            _draftErrors[item.BudgetItemId] = "Choose a transaction tag.";
            StateHasChanged();
            return;
        }

        // An in-flight save on ANOTHER row must not drop this one: the grid saves on change, so a
        // reader tabbing along a row would otherwise lose an edit to a save it never saw. Each row
        // awaits its turn on the shared gate instead.
        await _saveGate.WaitAsync();
        _isBusy = true;
        try
        {
            var update = new NewBudgetItem
            {
                BudgetId = BudgetId,
                CategoryType = draft.CategoryType,
                PlannedAmount = draft.PlannedAmount,
                TransactionTagId = tagId,
            };

            var result = await BudgetItems.UpdateAsync(item.BudgetItemId, update);

            // The grid's own per-row error channel — without it a field-level rejection would have
            // nowhere to render here and would fall back to an unattributed toast.
            if (result.Problem?.ErrorFor(nameof(NewBudgetItem.TransactionTagId)) is { } fieldError)
            {
                _draftErrors[item.BudgetItemId] = fieldError;
                return;
            }

            _draftErrors.Remove(item.BudgetItemId);
            if (result.Toast(Snackbar, "Unable to save item"))
                await OnChanged.InvokeAsync();
        }
        finally
        {
            _isBusy = false;
            _saveGate.Release();
        }
    }

    private string? DraftError(ExistingBudgetItem item) => _draftErrors.GetValueOrDefault(item.BudgetItemId);

    private bool _itemDialogOpen;
    private Guid _itemKey;
    private ExistingBudgetItem? _editItem;
    private List<Guid> _dialogUsedTagIds = [];

    private void AddItem()
    {
        if (!CanCreate)
            return;

        _editItem = null;
        _dialogUsedTagIds = UsedTagIds();
        _itemKey = Guid.NewGuid();
        _itemDialogOpen = true;
    }

    private void EditItem(ExistingBudgetItem item)
    {
        if (!CanUpdate)
            return;

        _editItem = item;
        _dialogUsedTagIds = UsedTagIds(item.BudgetItemId);
        _itemKey = Guid.NewGuid();
        _itemDialogOpen = true;
    }

    private async Task OnItemSaved()
    {
        var wasAdd = _editItem is null;
        await OnChanged.InvokeAsync();
        if (wasAdd)
        {
            _isOpen = true;
            // A new row needs a draft before the batch grid can bind to it.
            if (Editing)
                SeedDrafts();
        }
    }

    private async Task DeleteItem(ExistingBudgetItem item)
    {
        if (!CanDelete || _isBusy)
            return;

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete budget item",
            $"Delete '{item.Tag.Name}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed != true)
            return;

        _isBusy = true;
        try
        {
            if ((await BudgetItems.DeleteAsync(item.BudgetItemId)).Toast(Snackbar, "Unable to delete item", "Budget item deleted."))
                await OnChanged.InvokeAsync();
        }
        finally
        {
            _isBusy = false;
        }
    }

    private Task CopyItemId(ExistingBudgetItem item) =>
        Clipboard.CopyAsync(item.BudgetItemId.ToString(), "Budget item ID copied.");

    // A tag can be planned for by one item only per budget; the picker marks the others "in use"
    // rather than hiding them, and always exempts the row's own current tag.
    private List<Guid> UsedTagIds(Guid? exclude = null) =>
        Items.Where(i => i.BudgetItemId != exclude)
             .Select(i => i.TransactionTagId)
             .Distinct()
             .ToList();

    private sealed class Draft
    {
        public Draft(ExistingBudgetItem item)
        {
            CategoryType = item.CategoryType;
            PlannedAmount = item.PlannedAmount;
            TransactionTagId = item.TransactionTagId;
        }

        public BudgetCategoryType CategoryType { get; set; }
        public decimal PlannedAmount { get; set; }

        // Nullable only because the picker can be momentarily empty mid-edit; a write is refused
        // until it is not.
        public Guid? TransactionTagId { get; set; }
    }
}
