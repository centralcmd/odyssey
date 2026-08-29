using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

/// <summary>
/// The publish rules for the Terms of Service editor (issue #354 §3 state 5), as pure functions over
/// the draft and the currently published content.
/// </summary>
/// <remarks>
/// Extracted from <see cref="LegalDocuments"/> so the cap arithmetic and the "is this publishable"
/// decision are testable without a render harness. Publishing is irreversible — every version is
/// retained and every user is asked to re-accept — so the conditions that enable the button are worth
/// pinning directly rather than inferring from a page test.
/// </remarks>
public static class TermsOfServiceDraft
{
    /// <summary>Whether <paramref name="draft"/> differs from what is currently published (or exists at all, if nothing is).</summary>
    public static bool IsDirty(string draft, string? publishedContent) =>
        publishedContent is null ? draft.Length > 0 : !string.Equals(draft, publishedContent, StringComparison.Ordinal);

    /// <summary>The validation message for an over-long draft, or <see langword="null"/> when within the cap.</summary>
    /// <remarks>
    /// Measured on the raw draft, not the trimmed one: the server's <c>[StringLength]</c> counts every
    /// character it receives, so trimming here would let a draft that the API will reject look valid.
    /// </remarks>
    public static string? Error(string draft) =>
        draft.Length > LegalLimits.MaxTermsOfServiceContentLength
            ? $"Content exceeds the {LegalLimits.MaxTermsOfServiceContentLength:N0}-character limit by "
              + $"{draft.Length - LegalLimits.MaxTermsOfServiceContentLength:N0}."
            : null;

    /// <summary>
    /// Whether the draft may be published: it must differ from the current version, be within the cap,
    /// and carry something other than whitespace (the server rejects a blank body).
    /// </summary>
    public static bool IsPublishable(string draft, string? publishedContent) =>
        IsDirty(draft, publishedContent) && Error(draft) is null && draft.Trim().Length > 0;
}
