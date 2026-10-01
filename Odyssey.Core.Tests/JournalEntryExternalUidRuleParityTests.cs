using System.ComponentModel.DataAnnotations;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The ICS import and the create/update DTOs must accept the same journal-entry UIDs (issue #287 L8).
/// The import used to re-implement the rule with <c>char.IsControl</c>, which also rejected the C1
/// range the DTO pattern allows; it now runs the DTO's own pattern, and this pins the two together.
/// </summary>
public sealed class JournalEntryExternalUidRuleParityTests
{
    private static readonly RegularExpressionAttribute DtoRule = new(JournalEntryExternalUidRules.Pattern);

    [Theory]
    [InlineData("plain-uid")]
    [InlineData("uid with inner spaces")]
    [InlineData("ünïcødé@example.org")]
    [InlineData("c1\u0085control")]
    [InlineData("bad\tuid")]
    [InlineData("bad\nuid")]
    [InlineData("del\u007Fchar")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("trailing-newline\n")]
    [InlineData(" nbsp-leading")]
    public void ImportAcceptsExactlyWhatTheDtoAccepts(string uid) =>
        Assert.Equal(DtoRule.IsValid(uid), JournalEntryIcsService.IsValidExternalUid(uid));

    [Fact]
    public void ImportRejectsEmptyUid_WhichTheAttributeSkips()
    {
        Assert.True(DtoRule.IsValid(string.Empty));
        Assert.False(JournalEntryIcsService.IsValidExternalUid(string.Empty));
    }

    [Theory]
    [InlineData("bad\tuid")]
    [InlineData("trailing-newline\n")]
    [InlineData(" leading")]
    public void ImportRejectsControlAndEdgeWhitespace(string uid) =>
        Assert.False(JournalEntryIcsService.IsValidExternalUid(uid));
}
