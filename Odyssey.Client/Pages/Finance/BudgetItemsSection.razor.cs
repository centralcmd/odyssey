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
    [Parameter] public Func<decimal, string?, string> Format { get; set; } = (value, _) => value.ToString("C2");
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

    // Display actual for an item: income uses the raw sum, expense uses its magnitude.
    // A null tag (or, before the report loads, an unmatched tag) reads as "—".
    private decimal? ItemActual(ExistingBudgetItem item)
    {
        if (item.TransactionTagId is null || !HasReport)
            return null;
        if (!ActualByTag.TryGetValue(item.TransactionTagId.Value, out var sum))
            return 0m;
        return item.CategoryType == BudgetCategoryType.Income ? sum : Math.Abs(sum);
    }

    private string TagName(Guid? tagId)
    {
        if (tagId is null)
            return "Untagged";
        var tag = TransactionTags.FirstOrDefault(t => t.TransactionTagId == tagId.Value);
        return tag?.Name ?? "Untagged";
    }

    // ── Edit mode ────────────────────────────────────────────────────────
    private Task StopEditing() => EditingChanged.InvokeAsync(false);

    private void SeedDrafts()
    {
        _drafts.Clear();
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

    private async Task OnNameChanged(ExistingBudgetItem item, string value)
    {
        GetDraft(item).Name = value;
        await SaveItem(item);
    }

    private async Task OnCategoryChanged(ExistingBudgetItem item, BudgetCategoryType value)
    {
        GetDraft(item).CategoryType = value;
        await SaveItem(item);
    }

    private async Task OnTagChanged(ExistingBudgetItem item, Guid? value)
    {
        GetDraft(item).TransactionTagId = value;
        await SaveItem(item);
    }

    private async Task OnPlannedChanged(ExistingBudgetItem item, decimal value)
    {
        GetDraft(item).PlannedAmount = value;
        await SaveItem(item);
    }

    private async Task SaveItem(ExistingBudgetItem item)
    {
        if (!CanUpdate || _isBusy)
            return;

        var draft = GetDraft(item);
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            Snackbar.Add("Budget item name is required.", Severity.Error);
            return;
        }

        _isBusy = true;
        try
        {
            var update = new NewBudgetItem
            {
                BudgetId = BudgetId,
                Name = draft.Name.Trim(),
                Description = item.Description,
                CategoryType = draft.CategoryType,
                PlannedAmount = draft.PlannedAmount,
                TransactionTagId = draft.TransactionTagId,
            };

            if ((await BudgetItems.UpdateAsync(item.BudgetItemId, update)).Toast(Snackbar, "Unable to save item"))
            {
                item.Name = update.Name;
                item.CategoryType = update.CategoryType;
                item.PlannedAmount = update.PlannedAmount;
                item.TransactionTagId = update.TransactionTagId;
                await OnChanged.InvokeAsync();
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

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
            $"Delete '{item.Name}'? This cannot be undone.",
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

    private List<Guid> UsedTagIds(Guid? exclude = null) =>
        Items.Where(i => i.BudgetItemId != exclude && i.TransactionTagId.HasValue)
             .Select(i => i.TransactionTagId!.Value)
             .Distinct()
             .ToList();

    // A tag can be picked by one item only; the item keeps its current tag as an option.
    private IEnumerable<ExistingTransactionTag> AvailableTagsForItem(Guid budgetItemId)
    {
        var used = UsedTagIds(budgetItemId);
        var current = GetDraft(Items.First(i => i.BudgetItemId == budgetItemId)).TransactionTagId;
        return TransactionTags.Where(t => !used.Contains(t.TransactionTagId) || t.TransactionTagId == current);
    }

    private sealed class Draft
    {
        public Draft(ExistingBudgetItem item)
        {
            Name = item.Name;
            CategoryType = item.CategoryType;
            PlannedAmount = item.PlannedAmount;
            TransactionTagId = item.TransactionTagId;
        }

        public string Name { get; set; }
        public BudgetCategoryType CategoryType { get; set; }
        public decimal PlannedAmount { get; set; }
        public Guid? TransactionTagId { get; set; }
    }
}
