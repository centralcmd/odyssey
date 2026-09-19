using Microsoft.JSInterop;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Focus return across a tile removal, shared by the policy-party and contract-party tile surfaces
/// (issue #122 §4, §6.4). Domain-agnostic: it knows only opaque string keys and the selector a key
/// maps to, which is what lets one implementation serve both.
/// </summary>
/// <remarks>
/// <para>
/// The tile that had focus is gone by the time the new list arrives, so the neighbour to land on is
/// computed BEFORE the write and applied after the list actually shrinks. A removal that fails leaves
/// this armed, which is harmless: the only thing that disarms it is the member disappearing, and that
/// is a removal too.
/// </para>
/// <para>
/// Extracted rather than copied. <c>InsurancePolicyLinkTiles</c> cannot be dropped into the contract
/// surface — it takes a required <c>InsurancePartyRole</c> and is instantiated once per collection,
/// while a contract's parties are one flat list — so the alternative was a second copy of the
/// pre-compute-neighbour + JS-interop dance, and two copies of a focus mechanism drift in exactly the
/// way that is invisible until a keyboard user hits it.
/// </para>
/// </remarks>
internal sealed class TileRemovalFocus : IAsyncDisposable
{
    private readonly IJSRuntime js;
    private string? awaitingRemovalOf;
    private string? focusAfterRemoval;
    private bool pendingFocus;
    private IJSObjectReference? focusJs;

    public TileRemovalFocus(IJSRuntime js) => this.js = js;

    /// <summary>
    /// Arms the return: records which key is going away and which neighbour should take focus. Call
    /// this immediately BEFORE raising the removal, while the list still holds the member.
    /// </summary>
    public void Arm(string removedKey, IReadOnlyList<string> keys)
    {
        awaitingRemovalOf = removedKey;
        focusAfterRemoval = NeighbourOf(removedKey, keys);
    }

    /// <summary>
    /// Call from <c>OnParametersSet</c> with the current keys. Until the host re-fetches, the list
    /// still holds the removed member, so nothing happens; once it is gone the focus move is queued
    /// for the next render.
    /// </summary>
    public void OnKeysChanged(IReadOnlyList<string> keys)
    {
        if (awaitingRemovalOf is not { } removed || keys.Contains(removed, StringComparer.Ordinal))
        {
            return;
        }

        awaitingRemovalOf = null;
        pendingFocus = true;
    }

    /// <summary>
    /// Call from <c>OnAfterRenderAsync</c>. <paramref name="selectorFor"/> turns the neighbour's key
    /// into a CSS selector; <paramref name="fallbackSelectors"/> is where focus lands when the removal
    /// emptied the section, so focus is never dropped on the document.
    /// </summary>
    public async Task ApplyAsync(Func<string, string> selectorFor, IReadOnlyList<string>? fallbackSelectors)
    {
        if (!pendingFocus)
        {
            return;
        }

        pendingFocus = false;
        var neighbour = focusAfterRemoval;
        focusAfterRemoval = null;

        try
        {
            focusJs ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            string?[] candidates =
            [
                neighbour is null ? null : selectorFor(neighbour),
                .. fallbackSelectors ?? [],
            ];
            await focusJs.InvokeVoidAsync("focusFirst", candidates);
        }
        catch (Exception)
        {
            // Best-effort: the removal is already announced through the page's live region, so a
            // failed focus return degrades rather than losing the outcome.
        }
    }

    /// <summary>The next key, or the previous one when the removed tile was last. Null when it was the only one.</summary>
    internal static string? NeighbourOf(string key, IReadOnlyList<string> keys)
    {
        var index = -1;
        for (var i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal)) { index = i; break; }
        }

        if (index < 0) return null;
        if (index + 1 < keys.Count) return keys[index + 1];
        return index > 0 ? keys[index - 1] : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (focusJs is not null)
        {
            try { await focusJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
