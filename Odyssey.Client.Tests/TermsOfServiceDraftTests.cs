using Odyssey.Dtos.Application;
using Odyssey.Client.Pages;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Terms of Service editor's publish rules. Publishing is irreversible — the version is retained
/// forever and every user is asked to re-accept — so what enables the button is worth pinning directly.
/// </summary>
public class TermsOfServiceDraftTests
{
    private const int Cap = LegalLimits.MaxTermsOfServiceContentLength;

    // ── Dirty tracking ────────────────────────────────────────────────────────
    [Fact]
    public void WithNothingPublished_anyContentIsDirty()
    {
        Assert.True(TermsOfServiceDraft.IsDirty("Terms v1", publishedContent: null));
        Assert.False(TermsOfServiceDraft.IsDirty(string.Empty, publishedContent: null));
    }

    [Fact]
    public void AgainstAPublishedVersion_onlyAChangeIsDirty()
    {
        Assert.False(TermsOfServiceDraft.IsDirty("Terms v1", "Terms v1"));
        Assert.True(TermsOfServiceDraft.IsDirty("Terms v2", "Terms v1"));
    }

    /// <summary>Whitespace-only edits still count — the comparison is ordinal, not trimmed.</summary>
    [Fact]
    public void AWhitespaceOnlyEdit_isDirty() =>
        Assert.True(TermsOfServiceDraft.IsDirty("Terms v1 ", "Terms v1"));

    // ── The cap ───────────────────────────────────────────────────────────────
    [Fact]
    public void AtTheCap_thereIsNoError() =>
        Assert.Null(TermsOfServiceDraft.Error(new string('x', Cap)));

    [Fact]
    public void OneCharacterOverTheCap_reportsTheOverageExactly()
    {
        var error = TermsOfServiceDraft.Error(new string('x', Cap + 1));

        Assert.NotNull(error);
        Assert.Contains("50,000", error);
        Assert.Contains("by 1.", error);
    }

    /// <summary>
    /// The cap is measured on the raw draft, not the trimmed one — the server counts every character it
    /// receives, so trimming here would let a draft the API will reject look publishable.
    /// </summary>
    [Fact]
    public void TrailingWhitespacePushingPastTheCap_stillErrors() =>
        Assert.NotNull(TermsOfServiceDraft.Error(new string('x', Cap) + "   "));

    // ── Publishability ────────────────────────────────────────────────────────
    [Fact]
    public void AChangedDraftWithinTheCap_isPublishable() =>
        Assert.True(TermsOfServiceDraft.IsPublishable("Terms v2", "Terms v1"));

    [Fact]
    public void AnUnchangedDraft_isNotPublishable() =>
        Assert.False(TermsOfServiceDraft.IsPublishable("Terms v1", "Terms v1"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void ABlankDraft_isNotPublishable(string draft) =>
        Assert.False(TermsOfServiceDraft.IsPublishable(draft, publishedContent: null));

    [Fact]
    public void AnOverLongDraft_isNotPublishable() =>
        Assert.False(TermsOfServiceDraft.IsPublishable(new string('x', Cap + 1), "Terms v1"));

    /// <summary>The first-ever version: nothing published, real content — publishable.</summary>
    [Fact]
    public void TheFirstVersion_isPublishable() =>
        Assert.True(TermsOfServiceDraft.IsPublishable("Terms v1", publishedContent: null));
}
