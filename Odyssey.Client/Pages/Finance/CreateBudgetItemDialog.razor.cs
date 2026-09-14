using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateBudgetItemDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful add/edit so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    [Parameter] public Guid BudgetId { get; set; }

    [Parameter] public string? BudgetName { get; set; }

    /// <summary>The budget's base currency — the only currency a planned amount can be in, so the
    /// money editor shows it locked rather than offering a picker.</summary>
    [Parameter] public string BudgetCurrencyCode { get; set; } = "USD";

    [Parameter] public List<ExistingTransactionTag> TransactionTags { get; set; } = [];

    [Parameter] public List<Guid> UsedTransactionTagIds { get; set; } = [];

    /// <summary>
    /// The tag list failed to load, as opposed to being empty. The two need opposite remedies, so the
    /// picker must be able to tell them apart (issue #75 §11).
    /// </summary>
    [Parameter] public bool TagsLoadFailed { get; set; }

    /// <summary>Re-fetch the tag list after a failed load.</summary>
    [Parameter] public EventCallback OnRetryTags { get; set; }

    // When set, the dialog edits this item (PUT) instead of creating a new one (POST).
    [Parameter] public ExistingBudgetItem? ExistingItem { get; set; }

    private bool IsEdit => ExistingItem is not null;

    private OdsTransactionTagPicker? _tagPicker;
    private Guid? _tagId;
    private BudgetCategoryType _categoryType = BudgetCategoryType.Expense;
    // Held as text, not decimal?: OdsMoneyField is a string control, and round-tripping through
    // decimal? on every keystroke would rewrite a partial entry ("1250.") the moment it was typed.
    private string? _plannedAmountText;

    private string? _tagError;
    private bool _plannedError;
    private bool _canCreateTag;

    /// <summary>
    /// There is no viable path to a saved item: the tag list failed, or nothing is selectable and the
    /// caller cannot create one. Ordinary validation is NOT here — it belongs in the submit, so the
    /// reason lands on the offending field.
    /// </summary>
    private bool Blocked =>
        TagsLoadFailed
        || (_tagId is null
            && !_canCreateTag
            && !TransactionTags.Any(tag => tag.Archived is null && !UsedTransactionTagIds.Contains(tag.TransactionTagId)));

    protected override async Task OnInitializedAsync()
    {
        // The create row is an affordance; the authorization that matters is server-side on
        // TransactionTagController.Post. Passing no handler is what suppresses the row — OdsCombobox
        // has no AllowCreate flag, it renders the row exactly when OnCreate is non-null.
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateTag = user.HasPermission(PermissionClaims.TransactionTagsCreate);
        TagCreator.OnCreateFailed = OnTagCreateFailed;

        if (ExistingItem is null)
            return;

        // Preselect by ID, never by comparing ExistingTransactionTag instances: it is a record, so
        // value equality over four members would silently mis-match a tag whose description changed
        // between the item read and the tag-list read.
        _tagId = ExistingItem.TransactionTagId;
        _categoryType = ExistingItem.CategoryType;
        _plannedAmountText = ExistingItem.PlannedAmount.ToString(CultureInfo.InvariantCulture);
    }

    private void OnTagChanged(Guid? value)
    {
        _tagId = value;
        _tagError = null;
    }

    private OdsOption? CreateTagOption(string text) => TagCreator.Begin(text);

    /// <summary>
    /// A staged create's POST failed. The callback carries the temporary id and nothing else, and the
    /// creator has already shown its own snackbar — so the field message here is generic and this adds
    /// NO second toast (issue #75 §11).
    /// </summary>
    private void OnTagCreateFailed(string temporaryId)
    {
        // Raised from the creator's own background task, so hop onto the renderer's context rather
        // than touching component state from it. Not `async void`: the continuation is owned by the
        // dispatcher, which is what surfaces a fault instead of losing it.
        _ = InvokeAsync(async () =>
        {
            _tagError = "That tag could not be created.";
            if (_tagPicker is not null)
                await _tagPicker.DropStagedAsync(temporaryId);
            StateHasChanged();
        });
    }

    private static decimal? ParsePlanned(string? text) => OdsMoneyText.Parse(text);

    private async Task<bool> SaveAsync()
    {
        var planned = ParsePlanned(_plannedAmountText);
        _plannedError = planned is null;
        _tagError = _tagId is null || _tagId == Guid.Empty ? "Choose a transaction tag." : null;
        if (_tagError is not null || _plannedError)
            return false;

        // Let any in-flight inline create land, so the item is written with the RESOLVED tag id.
        // Sending a temporary id is the failure this staging exists to prevent.
        await TagCreator.WhenSettledAsync();

        if (!Guid.TryParse(TagCreator.Resolve(_tagId!.Value.ToString()), out var resolvedTagId)
            || resolvedTagId == Guid.Empty)
        {
            // Resolve returns null for a staged create that failed; OnTagCreateFailed has already
            // dropped the option and shown the creator's snackbar.
            _tagError ??= "That tag could not be created.";
            return false;
        }

        var payload = new NewBudgetItem
        {
            BudgetId = BudgetId,
            CategoryType = _categoryType,
            PlannedAmount = planned!.Value,
            TransactionTagId = resolvedTagId,
        };

        var result = IsEdit
            ? await BudgetItems.UpdateAsync(ExistingItem!.BudgetItemId, payload)
            : await BudgetItems.CreateAsync(payload);

        // The service keys its tag rejections to transactionTagId, so they render at the field rather
        // than as an unattributed toast.
        if (result.Problem?.ErrorFor(nameof(NewBudgetItem.TransactionTagId)) is { } fieldError)
        {
            _tagError = fieldError;
            return false;
        }

        if (!result.Toast(Snackbar,
                IsEdit ? "Unable to save item" : "Unable to add item",
                IsEdit ? "Budget item updated." : "Budget item added."))
        {
            return false;
        }

        // ITagQuickCreate does not invalidate the cache itself, so a tag created inline would not
        // appear on any other surface until a reload.
        if (resolvedTagId != _tagId!.Value)
            ReferenceData.InvalidateTransactionTags();

        return true;
    }
}
