using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>ContractsCard</c>'s row action menu: that none of the record's write actions is gated on the
/// archive state, and how the New term / New event requests reach the sections that serve them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Archiving never locks a contract.</b> New party, New term, New event and Attach documents are
/// all offered on an archived contract, because the server refuses none of them — archival hides a
/// contract from the default list, it does not freeze it, and the closing rent, the final invoice
/// and the handover note are exactly what get recorded after an agreement has ended. This file used
/// to pin the opposite for three of the four; what it pins now is that no "consistency" pass
/// re-introduces a gate the API does not have.
/// </para>
/// <para>
/// A source lint rather than a render, deliberately and with a cost. <c>ContractsCard</c> is an
/// <c>@page</c> whose rows arrive through <c>OdsInfiniteList</c>, which materialises nothing without
/// the JS observer bUnit has no real implementation of — a render test here asserts against an empty
/// list and passes whatever the branch says. <see cref="ContactAvatarSurfaceTests"/> pins the
/// equivalent rule on the sibling contacts page the same way. The lint's limit is honest: it proves
/// the branch is present and correctly shaped, not that it fires. The tile's half of the party rule
/// IS covered behaviourally, by <see cref="ContractPartyTileTests"/>, which renders the menu.
/// </para>
/// </remarks>
public class ContractsCardRowActionTests
{
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
    /// None of the four record write actions is gated on the archive state. Each is declared exactly
    /// once and as a plain live item, which is what says there is no <c>archived ?</c> ternary in
    /// front of it — a gated action declares its label twice, once per branch.
    /// </summary>
    /// <remarks>
    /// The single-occurrence assertion is the load-bearing one. A regex that merely finds a live
    /// <c>items.Add(new OdsMenuItem { … OnClick … })</c> would still match if a disabled branch were
    /// added beside it, because the live branch of a ternary looks exactly like an ungated item.
    /// </remarks>
    [Theory]
    [InlineData("New party")]
    [InlineData("New term")]
    [InlineData("New event")]
    [InlineData("Attach documents")]
    public void No_record_write_action_is_archive_gated(string label)
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"items\.Add\(new OdsMenuItem\s*\{[^}]*Label\s*=\s*""" + Regex.Escape(label) + @"""[^}]*OnClick"),
            source);
        Assert.Single(Regex.Matches(source, @"Label\s*=\s*""" + Regex.Escape(label) + @""""));
    }

    /// <summary>
    /// A row-menu request ("New event", "New term") must be consumed when the open card changes. The
    /// section is created afresh on every expand and cannot remember a token it already handled, so a
    /// request left in place reopened its dialog every time the card was collapsed and re-expanded.
    /// </summary>
    /// <remarks>
    /// A source-lint rather than a render, for the reason this class records: the card's rows arrive
    /// through <c>OdsInfiniteList</c>, which materialises nothing in bUnit, so there is no card to
    /// collapse. The property card is pinned the same way, beside it.
    /// </remarks>
    [Fact]
    public void Changing_the_open_card_consumes_the_pending_row_menu_requests()
    {
        var contracts = MethodBody(CardSource(), "private async Task ToggleExpand(Guid id)");
        Assert.Contains("_newEventRequest = null;", contracts, StringComparison.Ordinal);
        Assert.Contains("_newTermRequest = null;", contracts, StringComparison.Ordinal);

        var properties = MethodBody(
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "PropertiesCard.razor.cs")),
            "private void ToggleExpand(Guid id)");
        Assert.Contains("_newEventTokens.Clear();", properties, StringComparison.Ordinal);
        Assert.Contains("_newEstimateTokens.Clear();", properties, StringComparison.Ordinal);
        Assert.Contains("_attachTokens.Clear();", properties, StringComparison.Ordinal);
    }

    /// <summary>The text from a method's signature to the first closing brace at its own indent.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is still declared");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }

    /// <summary>
    /// The record write actions run in the design system's order (Contracts.jsx): the three that
    /// shape the agreement, then the document that evidences it, then the log of what happened to it.
    /// </summary>
    [Fact]
    public void Record_write_actions_follow_the_design_order()
    {
        var source = CardSource();

        var order = new[] { "New party", "New term", "Attach documents", "New event" }
            .Select(label => source.IndexOf($"Label = \"{label}\"", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    /// <summary>
    /// The reason an unavailable action cannot be taken is never carried as a menu row. The four
    /// write actions above are ungated outright; the two lifecycle items that CAN be unavailable
    /// (Pause on an unsigned contract, Archive before it has ended) are simply absent when they are,
    /// and must not acquire an explanation that only an opened menu could deliver.
    /// </summary>
    [Fact]
    public void An_unavailable_row_action_carries_no_explanatory_note()
    {
        var source = CardSource();

        // Scoped to the menu items: `Description` is an ordinary field name elsewhere in this file
        // (a contract document carries one), so an unscoped match would fail for the wrong reason.
        var items = Regex.Matches(source, @"new OdsMenuItem\s*\{[^}]*\}");
        Assert.NotEmpty(items);
        Assert.All(items, match =>
            Assert.DoesNotContain("Description", match.Value, StringComparison.Ordinal));

        Assert.DoesNotContain("Unarchive the contract", source, StringComparison.Ordinal);
        Assert.DoesNotContain("has to be restored", source, StringComparison.Ordinal);
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
    /// #138 — the "New event" request is routed BY CONTRACT ID, for the same reason the term one is
    /// (see above): a field holding the expanded body is rebound a render too late, so it can still
    /// point at the previously expanded record when the click is handled.
    /// </summary>
    [Fact]
    public void The_new_event_request_is_scoped_to_the_contract_that_asked()
    {
        var source = CardSource();

        Assert.Matches(new Regex(@"\(Guid ContractId, Guid Token\)\?\s+_newEventRequest"), source);
        Assert.Matches(
            new Regex(@"NewEventRequestFor\(Guid contractId\)[\s\S]{0,200}?request\.ContractId\s*==\s*contractId"),
            source);
    }

    /// <summary>
    /// The page must never reach for the native attribute directly. <c>OdsMenuItem.Disabled</c> is
    /// the one supported way to express this, and <c>OdsMenuUnavailableItemTests</c> pins what it
    /// does on the component side — the item is not rendered at all.
    /// </summary>
    [Fact]
    public void The_page_never_hand_rolls_a_native_disabled_menu_item()
    {
        Assert.DoesNotContain("disabled=\"disabled\"", CardSource(), StringComparison.Ordinal);
    }
}
