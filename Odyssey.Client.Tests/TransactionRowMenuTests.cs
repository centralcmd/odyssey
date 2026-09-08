using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A transaction row offers the same actions wherever the ledger is rendered.
/// </summary>
/// <remarks>
/// <para>
/// The ledger appears on the Transactions page and inside three embedded sections — an account's
/// transactions, a budget's matched transactions, and an account's smart-tag watchlist. Each embedded
/// copy used to build its own two-item menu (View details · Copy ID), so the same row that could be
/// edited, approved, flagged and deleted on the page was read-only when embedded, with no visible
/// reason for the difference.
/// </para>
/// <para>
/// Two rules are pinned here. The menu's <b>content</b> is decided by permission claims and the row's
/// own status, never by the surface it is rendered on. And the menu is built in <b>one</b> place —
/// the source lint is what stops a fifth surface from quietly growing a fourth variant.
/// </para>
/// </remarks>
public class TransactionRowMenuTests
{
    private static IReadOnlyList<OdsMenuItem> Build(
        TransactionStatus status = TransactionStatus.New,
        bool canUpdate = true,
        bool canDelete = true,
        bool expanded = false)
    {
        var transaction = new ExistingTransaction
        {
            TransactionId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Description = "Kiwi Minipris",
            Amount = -249.50m,
            TimeStamp = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc),
            CurrencyCode = "NOK",
            Status = status,
        };

        var ctx = new OdsRecordActionContext
        {
            Expanded = expanded,
            Editing = false,
            Toggle = () => { },
            StartEdit = () => { },
            Remove = () => { },
        };

        return TransactionRowMenu.Build(
            new object(), transaction, ctx, canUpdate, canDelete,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask);
    }

    private static IReadOnlyList<string> Labels(IReadOnlyList<OdsMenuItem> items) =>
        [.. items.Where(i => !i.Divider).Select(i => i.Label ?? string.Empty)];

    /// <summary>The full menu, in order — what an Admin sees on a New row on every surface.</summary>
    [Fact]
    public void A_permitted_user_gets_every_action()
    {
        Assert.Equal(
            ["View details", "Edit", "Approve", "Flag", "Copy ID", "Delete"],
            Labels(Build()));
    }

    /// <summary>The disclosure item names the state it moves TO, so the label is never stale.</summary>
    [Fact]
    public void The_disclosure_item_follows_the_row_state()
    {
        Assert.Equal("View details", Labels(Build(expanded: false))[0]);
        Assert.Equal("Collapse", Labels(Build(expanded: true))[0]);
    }

    /// <summary>
    /// A status the row already carries is not offered — an item that would be a no-op is worse than
    /// no item, because it reads as an action that silently did nothing.
    /// </summary>
    [Theory]
    [InlineData(TransactionStatus.New, "Approve", "Flag")]
    [InlineData(TransactionStatus.Approved, "Flag", "Reset to New")]
    [InlineData(TransactionStatus.Flagged, "Approve", "Reset to New")]
    public void Only_the_states_the_row_is_not_in_are_offered(
        TransactionStatus status, string first, string second)
    {
        var labels = Labels(Build(status));
        var transitions = labels.Where(l => l is "Approve" or "Flag" or "Reset to New").ToList();

        Assert.Equal([first, second], transitions);
    }

    /// <summary>
    /// Claims are the ONLY reason an item is absent. Without <c>transactions.update</c> there is no
    /// Edit and no status transition; the server-side <c>[Authorize]</c> stays the real gate, but a
    /// user who would meet a 403 is never shown the control.
    /// </summary>
    [Fact]
    public void Without_update_there_is_no_edit_and_no_status_transition()
    {
        var labels = Labels(Build(canUpdate: false));

        Assert.Equal(["View details", "Copy ID", "Delete"], labels);
    }

    [Fact]
    public void Without_delete_there_is_no_delete()
    {
        var labels = Labels(Build(canDelete: false));

        Assert.DoesNotContain("Delete", labels);
        Assert.Contains("Edit", labels);
    }

    /// <summary>A reader with neither write claim still gets the two actions that only read.</summary>
    [Fact]
    public void A_read_only_user_keeps_the_actions_that_only_read()
    {
        Assert.Equal(["View details", "Copy ID"], Labels(Build(canUpdate: false, canDelete: false)));
    }

    /// <summary>
    /// The quick status write is a full PUT, so every field has to be carried across — one missed
    /// property would be silently cleared by an Approve.
    /// </summary>
    [Fact]
    public void The_status_patch_carries_the_record_forward_and_changes_only_the_status()
    {
        var tagId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var t = new ExistingTransaction
        {
            TransactionId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Description = "Kiwi Minipris",
            Amount = -249.50m,
            TimeStamp = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc),
            CurrencyCode = "NOK",
            ExternalId = "ext-1",
            InternalId = "int-1",
            ExtraData = "{}",
            Status = TransactionStatus.New,
            StatusComment = "imported",
            ContactId = contactId,
            TransactionTags = [new ExistingTransactionTag { TransactionTagId = tagId, Name = "Groceries", Archived = null }],
        };

        var patch = TransactionRowMenu.StatusPatch(t, TransactionStatus.Approved);

        Assert.Equal(TransactionStatus.Approved, patch.Status);
        Assert.Equal(t.Description, patch.Description);
        Assert.Equal(t.Amount, patch.Amount);
        Assert.Equal(t.TimeStamp, patch.TimeStamp);
        Assert.Equal(t.AccountId, patch.AccountId);
        Assert.Equal(t.CurrencyCode, patch.CurrencyCode);
        Assert.Equal(t.ExternalId, patch.ExternalId);
        Assert.Equal(t.InternalId, patch.InternalId);
        Assert.Equal(t.ExtraData, patch.ExtraData);
        Assert.Equal(t.StatusComment, patch.StatusComment);
        Assert.Equal(contactId, patch.ContactId);
        Assert.Equal([tagId], patch.TransactionTagIds);
    }

    /// <summary>
    /// Every ledger routes through the one builder. A surface that hand-rolls its own menu is how the
    /// three embedded copies drifted into a two-item stub in the first place, and the drift is
    /// invisible until someone opens the menu on the right page.
    /// </summary>
    [Fact]
    public void Every_ledger_builds_its_row_menu_in_one_place()
    {
        var offenders = new List<string>();
        var checkedHosts = new List<string>();

        foreach (var file in ClientSource.SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("<OdsTxnTable", StringComparison.Ordinal))
                continue;

            // The component itself declares the parameter; a HOST that binds it must delegate.
            if (Path.GetFileName(file) is "OdsTxnTable.razor" or "OdsInlinePager.razor")
                continue;

            if (!text.Contains("Actions=", StringComparison.Ordinal))
                continue;

            checkedHosts.Add(ClientSource.Relative(file));
            var partner = file.EndsWith(".razor", StringComparison.Ordinal) ? file + ".cs" : file;
            var behind = File.Exists(partner) ? File.ReadAllText(partner) : string.Empty;
            if (!text.Contains("TransactionRowMenu.Build", StringComparison.Ordinal)
                && !behind.Contains("TransactionRowMenu.Build", StringComparison.Ordinal))
            {
                offenders.Add(ClientSource.Relative(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "These surfaces bind OdsTxnTable.Actions without going through TransactionRowMenu.Build, "
            + "so their row menu can drift from every other ledger:\n" + string.Join("\n", offenders));

        // A lint that scanned nothing would pass forever. Two ledgers bind the menu — the Transactions
        // page and the shared embedded view — and a third appearing here is the signal to check it
        // delegates rather than to raise this number.
        Assert.True(checkedHosts.Count >= 2,
            "The lint found no ledger to check, so it proves nothing. Scanned: "
            + string.Join(", ", checkedHosts));
    }
}
