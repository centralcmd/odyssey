using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The announcement logic behind <c>OdsTagMultiSelect</c>'s bulk <b>Clear</b> (issue #27 P0.9).
/// </summary>
/// <remarks>
/// <para>
/// This is a shared component — the transaction-tag fields, Journal contacts, Photos people and
/// albums, and the four insurance link pickers all render it — so a regression here reaches further
/// than any one page.
/// </para>
/// <para>
/// The rule being pinned: Clear does not clear everything. A member the write path refuses to remove
/// (an archived or unresolvable link) is <b>kept</b>, and the announcement is the only way a
/// screen-reader user learns that — they cannot see the chips that stayed. A confirmation that said
/// a flat "Selection cleared." while three chips remained would be actively wrong, not merely terse.
/// </para>
/// </remarks>
public class OdsTagMultiSelectTests
{
    [Fact]
    public void ClearAnnouncement_confirms_plainly_when_nothing_was_kept() =>
        Assert.Equal("Selection cleared.", OdsTagMultiSelect.ClearAnnouncement(0, "tag", null));

    [Fact]
    public void ClearAnnouncement_reports_a_single_kept_member_in_the_singular()
    {
        var announcement = OdsTagMultiSelect.ClearAnnouncement(1, "beneficiary", "beneficiaries");

        Assert.Contains("1 beneficiary kept", announcement, StringComparison.Ordinal);
        Assert.Contains("it cannot", announcement, StringComparison.Ordinal);
        Assert.DoesNotContain("they cannot", announcement, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearAnnouncement_reports_several_kept_members_in_the_plural()
    {
        var announcement = OdsTagMultiSelect.ClearAnnouncement(3, "beneficiary", "beneficiaries");

        Assert.Contains("3 beneficiaries kept", announcement, StringComparison.Ordinal);
        Assert.Contains("they cannot", announcement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is the whole point: a user pressing Clear on a collection holding an archived link
    /// has to be told that something stayed, or the action reads as having done nothing.
    /// </summary>
    [Fact]
    public void ClearAnnouncement_always_states_that_kept_members_cannot_be_removed_here()
    {
        foreach (var kept in new[] { 1, 2, 50 })
        {
            var announcement = OdsTagMultiSelect.ClearAnnouncement(kept, "insurer", null);

            Assert.Contains(kept.ToString(), announcement, StringComparison.Ordinal);
            Assert.Contains("be removed here", announcement, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, "tag", null, "tag")]
    [InlineData(2, "tag", null, "tags")]
    [InlineData(1, "person", "people", "person")]
    // The reason NounPlural exists at all: "persons" is what naive pluralisation produces, and the
    // Photos people picker would announce it on every add and remove.
    [InlineData(2, "person", "people", "people")]
    [InlineData(0, "beneficiary", "beneficiaries", "beneficiaries")]
    public void Plural_uses_the_explicit_plural_where_one_is_supplied(
        int count, string noun, string? nounPlural, string expected) =>
        Assert.Equal(expected, OdsTagMultiSelect.Plural(count, noun, nounPlural));
}
