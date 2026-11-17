using Microsoft.JSInterop;
using Odyssey.Dtos;

namespace Odyssey.Client.Pages;

// The News group's one gated row (issue #473 §3.6): the private-host allow-list. Widening it lets the worker reach
// an address on the household's own network, so the page's Save stops in front of any change to it and lists, as
// text, the hosts it adds and removes. The confirmation is one-shot per save, like the relay-credential gate.
public partial class Settings
{
    internal const string NewsAllowListKey = "newsPrivateHostAllowList";

    internal const string NewsAllowListAdvisory = "Never list mariadb, api, worker or other internal service names.";

    private bool _allowListConfirmed;

    private bool _allowListDialogOpen;

    private AllowListChange? _allowListChange;

    /// <summary>The hosts a save would add to and remove from the allow-list, in the stored spelling.</summary>
    internal sealed record AllowListChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

    /// <summary>
    /// The change this save makes to the allow-list, or <see langword="null"/> when it makes none the worker would
    /// notice: the row is not editable by this caller, not dirty, not valid (Save is blocked anyway), or differs only
    /// in spelling. Both sides are parsed with the shared rule, so <c>NAS.lan</c> and <c>nas.lan</c> are one grant.
    /// </summary>
    private AllowListChange? PendingAllowListChange
    {
        get
        {
            if (AllItems.FirstOrDefault(item => item.Key == NewsAllowListKey) is not { } row
                || !Editable(row.Claim) || !IsDirty(row))
            {
                return null;
            }

            var draft = GetText(NewsAllowListKey);
            if (NewsPrivateHostAllowListRule.Validate(draft) is not null)
            {
                return null;
            }

            var saved = _texts.TryGetValue(NewsAllowListKey, out var state) ? state.Saved : string.Empty;
            return DiffAllowList(saved, draft);
        }
    }

    /// <summary>The added and removed grants between two allow-list values; <see langword="null"/> when they agree.</summary>
    internal static AllowListChange? DiffAllowList(string? saved, string? draft)
    {
        var before = NewsPrivateHostAllowListRule.Parse(saved);
        var after = NewsPrivateHostAllowListRule.Parse(draft);
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        return new AllowListChange(
            [.. added.Select(g => NewsPrivateHostAllowListRule.Format([g]))],
            [.. removed.Select(g => NewsPrivateHostAllowListRule.Format([g]))]);
    }

    internal static string AllowListAddedSentence(AllowListChange change) =>
        $"The worker will be allowed to fetch from: {string.Join(", ", change.Added)}.";

    internal static string AllowListRemovedSentence(AllowListChange change) =>
        $"The worker will no longer be allowed to fetch from: {string.Join(", ", change.Removed)}.";

    private void OpenAllowListConfirmation(AllowListChange change)
    {
        _allowListChange = change;
        _allowListDialogOpen = true;
        Announce("Confirmation required: this save changes the hosts the news worker may fetch from.");
    }

    private async Task<bool> ConfirmAllowListAsync()
    {
        _allowListConfirmed = true;
        _allowListDialogOpen = false;
        await FocusAllowListRowAsync();
        await Save();
        return true;
    }

    private async Task OnAllowListDialogOpenChanged(bool open)
    {
        _allowListDialogOpen = open;
        if (!open)
        {
            // Cancel discards nothing: the edit stays, the page stays dirty, and neither gate is pre-approved.
            _allowListConfirmed = false;
            _clearConfirmed = false;
            await FocusAllowListRowAsync();
        }
    }

    private async Task FocusAllowListRowAsync()
    {
        StateHasChanged();
        try
        {
            await JS.InvokeVoidAsync("odsFocusById", TitleId(NewsAllowListKey));
        }
        catch
        {
            // The row is not rendered (a search filters it away) — only the focus courtesy is lost.
        }
    }
}
