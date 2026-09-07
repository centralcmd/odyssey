using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class TransactionListView
{
    /// <summary>The rows to page through — the host owns the query that produced them.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<ExistingTransaction> Rows { get; set; } = [];

    /// <summary>Drop the Account column, for a surface whose rows all belong to one account.</summary>
    [Parameter] public bool HideAccount { get; set; }

    /// <summary>
    /// Re-run the host's query. Raised after any write this view applies — a status change, a delete,
    /// or a save from the edit dialog — because the host's rows are now stale and only it can refetch.
    /// </summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;
    private bool _canDeleteFiles;

    private ExistingTransaction? _editTransaction;
    private Guid _editKey;
    private bool _editOpen;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.TransactionsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.TransactionsDelete);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canUploadFiles = user.HasPermission(PermissionClaims.FilesCreate);
        _canDeleteFiles = user.HasPermission(PermissionClaims.FilesDelete);
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingTransaction t, OdsRecordActionContext ctx) =>
        TransactionRowMenu.Build(this, t, ctx, _canUpdate, _canDelete, EditAsync, SetStatusAsync, CopyIdAsync);

    private Task EditAsync(ExistingTransaction t)
    {
        if (!_canUpdate)
            return Task.CompletedTask;

        _editTransaction = t;
        _editKey = Guid.NewGuid();
        _editOpen = true;
        return Task.CompletedTask;
    }

    private async Task SetStatusAsync(ExistingTransaction t, TransactionStatus status)
    {
        if (!_canUpdate || t.Status == status)
            return;

        if ((await Transactions.UpdateAsync(t.TransactionId, TransactionRowMenu.StatusPatch(t, status)))
            .Toast(Snackbar, "Update failed", "Transaction updated."))
        {
            await ChangedAsync();
        }
    }

    private async Task HandleDeleteAsync(object key)
    {
        if (!_canDelete)
            return;

        var transaction = Rows.FirstOrDefault(t => t.TransactionId.Equals(key));
        if (transaction is null)
            return;

        if ((await Transactions.DeleteAsync(transaction.TransactionId))
            .Toast(Snackbar, "Delete failed", "Transaction deleted."))
        {
            await ChangedAsync();
        }
    }

    private Task CopyIdAsync(Guid transactionId) =>
        Clipboard.CopyAsync(transactionId.ToString(), "Transaction ID copied to clipboard.");

    private async Task ChangedAsync()
    {
        await OnChanged.InvokeAsync();
        StateHasChanged();
    }
}
