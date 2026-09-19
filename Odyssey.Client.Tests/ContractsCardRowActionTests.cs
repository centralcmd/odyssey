using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>ContractsCard</c>'s row action menu: the archived-contract state of <b>New party</b>
/// (#122 §3 state 14 / AC 7) and of <b>New term</b> (#135), plus how the latter's request reaches the
/// section that serves it.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because that branch lives in a DIFFERENT file from the party tiles
/// (<c>ContractsCard.razor.cs</c>, not <c>ContractDetailView.razor.cs</c>) and had no automated cover
/// at all — #122 §4 calls it out as easy to miss for exactly that reason. The tile's half of the same
/// rule IS covered behaviourally, by <see cref="ContractPartyTileTests"/>, which renders the menu.
/// </para>
/// <para>
/// A source lint rather than a render, deliberately and with a cost. <c>ContractsCard</c> is an
/// <c>@page</c> whose rows arrive through <c>OdsInfiniteList</c>, which materialises nothing without
/// the JS observer bUnit has no real implementation of — a render test here asserts against an empty
/// list and passes whatever the branch says. <see cref="ContactAvatarSurfaceTests"/> pins the
/// equivalent rule on the sibling contacts page the same way. The lint's limit is honest: it proves
/// the branch is present and correctly shaped, not that it fires. What makes that worth having is that
/// the defect it guards IS a literal in source — the reason text deleted, or <c>Disabled</c> set
/// without <c>Description</c>, which is what silently turns the item natively disabled and drops it
/// out of the roving tab order (WCAG 2.1.1).
/// </para>
/// </remarks>
public class ContractsCardRowActionTests
{
    private const string ArchivedReason = "Unarchive the contract to change its parties.";
    private const string ArchivedTermReason = "Restore the contract to change its terms.";

    /// <summary>
    /// The source with comments stripped. The file's own doc comments legitimately DISCUSS the reason
    /// string and the disabled branch, and a lint a comment can satisfy is a lint that proves nothing.
    /// Same reason <see cref="ContactAvatarSurfaceTests"/> strips them.
    /// </summary>
    private static string CardSource()
    {
        var source = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractsCard.razor.cs"));
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"^\s*(///?).*$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// AC 7 — New party is offered on an archived contract with its reason IN TEXT, not hidden and not
    /// silently inert. Both halves are asserted: the branch keys on <c>archived</c>, and the disabled
    /// item carries a <c>Description</c>.
    /// </summary>
    [Fact]
    public void New_party_is_disabled_with_its_reason_on_an_archived_contract()
    {
        var source = CardSource();

        // One ternary on `archived`, producing a Disabled item with the reason, or a live one.
        Assert.Matches(
            new Regex(@"items\.Add\(\s*archived\s*\?[\s\S]{0,600}?Label\s*=\s*""New party""[\s\S]{0,400}?Disabled\s*=\s*true",
                RegexOptions.None),
            source);
        Assert.Contains(ArchivedReason, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// #135 — New term takes the same treatment as its sibling: offered on an archived contract with
    /// its reason IN TEXT, never hidden and never silently inert. The server refuses every term write
    /// on an archived contract, so an action that could only fail is explained rather than removed.
    /// </summary>
    [Fact]
    public void New_term_is_disabled_with_its_reason_on_an_archived_contract()
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"items\.Add\(\s*archived\s*\?[\s\S]{0,600}?Label\s*=\s*""New term""[\s\S]{0,400}?Disabled\s*=\s*true",
                RegexOptions.None),
            source);
        Assert.Contains(ArchivedTermReason, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// #135 — the "New term" request is routed BY CONTRACT ID, never through a reference to whichever
    /// body happens to be mounted.
    /// </summary>
    /// <remarks>
    /// This pins a real defect rather than a style. A <c>ContractDetailView?</c> field is rebound on
    /// the NEXT render, so immediately after expanding record B it still holds record A's section —
    /// and when B's detail is already cached, <c>ToggleExpand</c> returns without yielding, so no
    /// render has happened in between. Opening through that field put the dialog on the wrong contract
    /// and silently dropped B's click. The token is handed only to the row whose id matches, which
    /// makes the correct section a matter of construction rather than of render timing. A lint because
    /// <c>ContractsCard</c> is an <c>@page</c> whose rows arrive through <c>OdsInfiniteList</c>, which
    /// materialises nothing under bUnit; the receiving half IS covered behaviourally, by
    /// <c>ContractTermSurfaceTests</c>' token tests.
    /// </remarks>
    [Fact]
    public void The_new_term_request_is_scoped_to_the_contract_that_asked()
    {
        var source = CardSource();

        // The request carries the asking contract's id, and the row only receives its own token.
        Assert.Matches(new Regex(@"\(Guid ContractId, Guid Token\)\?\s+_newTermRequest"), source);
        Assert.Matches(
            new Regex(@"NewTermRequestFor\(Guid contractId\)[\s\S]{0,200}?request\.ContractId\s*==\s*contractId"),
            source);

        // And the card holds no reference to the expanded body for this purpose.
        Assert.DoesNotContain("ContractDetailView? _detailView", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The load-bearing half: <c>Disabled</c> is paired with <c>Description</c>. That pairing is what
    /// makes <c>OdsMenu</c> render <c>aria-disabled</c> and keep the item focusable instead of applying
    /// MudBlazor's native <c>disabled</c>, which a roving-tabindex menu SKIPS — putting the very reason
    /// text this rule requires out of reach of a keyboard or AT user. Dropping the Description is
    /// therefore not a cosmetic regression but an accessibility one, and it is invisible in a diff.
    /// </summary>
    [Fact]
    public void Every_disabled_row_action_states_a_reason()
    {
        var source = CardSource();

        var disabled = Regex.Matches(source, @"new OdsMenuItem\s*\{[^}]*Disabled\s*=\s*true[^}]*\}");
        Assert.NotEmpty(disabled);
        Assert.All(disabled, match =>
            Assert.Contains("Description", match.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The page must never reach for the native attribute directly. <c>OdsMenuItem.Disabled</c> +
    /// <c>Description</c> is the one supported way to express this, and it is what
    /// <c>OdsMenuDisabledItemTests</c> pins on the component side.
    /// </summary>
    [Fact]
    public void The_page_never_hand_rolls_a_native_disabled_menu_item()
    {
        Assert.DoesNotContain("disabled=\"disabled\"", CardSource(), StringComparison.Ordinal);
    }
}
