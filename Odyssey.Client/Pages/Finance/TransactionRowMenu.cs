using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The one definition of a transaction row's action menu, and of the write behind its quick status
/// actions.
/// </summary>
/// <remarks>
/// <para>
/// The ledger is rendered in four places — the Transactions page and the three embedded sections
/// (an account's transactions, a budget's matched transactions, an account's smart-tag watchlist) —
/// and a row means the same thing in all four. Before this, the three embedded copies offered only
/// <i>View details</i> and <i>Copy ID</i>: the same row that could be edited, approved, flagged and
/// deleted on the Transactions page was read-only wherever it was embedded, with no visible reason
/// for the difference. Building the menu here rather than per host is what keeps the four from
/// drifting apart again.
/// </para>
/// <para>
/// The permission gates stay — <c>transactions.update</c> for Edit and the status transitions,
/// <c>transactions.delete</c> for Delete — because a user who would meet a 403 must not be shown the
/// control. They are the ONLY reason an item is ever absent; the surface a row happens to be rendered
/// on is not.
/// </para>
/// </remarks>
public static class TransactionRowMenu
{
    /// <summary>
    /// Builds the row menu. <paramref name="receiver"/> is the component that owns the callbacks, so
    /// each invocation re-renders it.
    /// </summary>
    public static IReadOnlyList<OdsMenuItem> Build(
        object receiver,
        ExistingTransaction transaction,
        OdsRecordActionContext ctx,
        bool canUpdate,
        bool canDelete,
        Func<ExistingTransaction, Task> onEdit,
        Func<ExistingTransaction, TransactionStatus, Task> onSetStatus,
        Func<Guid, Task> onCopyId)
    {
        var items = new List<OdsMenuItem>
        {
            new()
            {
                Icon = ctx.Expanded ? "close" : "expand_more",
                Label = ctx.Expanded ? "Collapse" : "View details",
                OnClick = EventCallback.Factory.Create(receiver, ctx.Toggle),
            },
        };

        if (canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit",
                OnClick = EventCallback.Factory.Create(receiver, () => onEdit(transaction)),
            });

            // Status transitions (New · Approved · Flagged) — offered only for the states the row
            // isn't already in, so the menu never carries an item that would be a no-op.
            var statusItems = new List<OdsMenuItem>();
            if (transaction.Status != TransactionStatus.Approved)
            {
                statusItems.Add(new OdsMenuItem
                {
                    Icon = "check_circle",
                    Label = "Approve",
                    OnClick = EventCallback.Factory.Create(receiver, () => onSetStatus(transaction, TransactionStatus.Approved)),
                });
            }

            if (transaction.Status != TransactionStatus.Flagged)
            {
                statusItems.Add(new OdsMenuItem
                {
                    Icon = "flag",
                    Label = "Flag",
                    OnClick = EventCallback.Factory.Create(receiver, () => onSetStatus(transaction, TransactionStatus.Flagged)),
                });
            }

            if (transaction.Status != TransactionStatus.New)
            {
                statusItems.Add(new OdsMenuItem
                {
                    Icon = "undo",
                    Label = "Reset to New",
                    OnClick = EventCallback.Factory.Create(receiver, () => onSetStatus(transaction, TransactionStatus.New)),
                });
            }

            if (statusItems.Count > 0)
            {
                items.Add(new OdsMenuItem { Divider = true });
                items.AddRange(statusItems);
            }
        }

        items.Add(new OdsMenuItem { Divider = true });
        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(receiver, () => onCopyId(transaction.TransactionId)),
        });

        if (canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(receiver, ctx.Remove),
            });
        }

        return items;
    }

    /// <summary>
    /// The body for a quick status change: a full patch mirroring the current record with the new
    /// status, so Approve / Flag / Reset don't require opening the editor.
    /// </summary>
    /// <remarks>
    /// The write is a full PUT, so every field has to be carried across — a property added to
    /// <see cref="NewTransaction"/> and missed here would be silently cleared by an Approve. It lives
    /// beside the menu that triggers it for exactly that reason.
    /// </remarks>
    public static NewTransaction StatusPatch(ExistingTransaction t, TransactionStatus status) => new()
    {
        Description = t.Description,
        Amount = t.Amount,
        TimeStamp = t.TimeStamp,
        AccountId = t.AccountId,
        TransactionTagIds = [.. t.TransactionTags.Select(tag => tag.TransactionTagId)],
        ContactId = t.ContactId ?? t.Contact?.ContactId,
        CurrencyCode = t.CurrencyCode,
        ExternalId = t.ExternalId,
        InternalId = t.InternalId,
        ExtraData = t.ExtraData,
        Status = status,
        StatusComment = t.StatusComment,
    };
}
